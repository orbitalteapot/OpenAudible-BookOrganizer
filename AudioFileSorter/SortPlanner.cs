using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Works out, up front and in list order, exactly which file is copied where.
///
/// Planning is deliberately single threaded and separate from copying. Deciding folder names
/// while dozens of workers race to create them is how a library ends up with both
/// "J.K. Rowling" and "JK Rowling", or with two books quietly overwriting each other; doing it
/// once, in order, makes the outcome of a sort deterministic and repeatable.
/// </summary>
public sealed class SortPlanner
{
    private const int MaxDisambiguationAttempts = 100;

    /// <summary>Marks a folder in <see cref="_claimedBookDirectories"/> as a series, which no book may use.</summary>
    private const string SeriesFolderClaim = "\u0000series";

    /// <summary>
    /// Stands in for the source path as the owner of a missing book's names (followed by the book's
    /// index); it can never be a real path.
    /// </summary>
    private const string MissingBookOwner = "\u0000missing\u0000";

    private const string EmptySourceMessage =
        "The book's file in the source folder is empty, as it is while OpenAudible is still downloading or " +
        "converting it. It is copied once it is complete.";

    private readonly Dictionary<string, string> _resolvedDirectoryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _directoryFileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedBookDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedSharedFolderNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedSharedFolderNamesIfMissingHadFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimedLegacyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _standaloneBookFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _subdirectoryNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder each book (by its audio source) was found in before; see <see cref="KeepBookFolders"/>.</summary>
    private readonly Dictionary<string, string> _keptBookDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The books that could not be given back a folder of their own, by <see cref="RivalKeys"/>:
    /// the folders they could be in are not free just because nobody claims them.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _unplacedOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which normaliser decides that two folder names mean the same thing.</summary>
    private enum FolderKind
    {
        /// <summary>Author and book folders: spelling and punctuation are ignored.</summary>
        Name,

        /// <summary>Series folders: also ignores a leading "The" and a trailing "Series", "Saga"...</summary>
        Series
    }

    /// <summary>
    /// The layouts older versions left a book in, in the order they are tried. Every book's
    /// candidates of one layout are claimed before any book's of the next, so a book that has
    /// changed shape never takes a file that is exactly where an older version put another book.
    /// </summary>
    private enum LegacyLayout
    {
        /// <summary>
        /// In a folder older versions shared between books, under the name they gave this one:
        /// loose in the author folder (or the series folder, for a series book without a number),
        /// or in the series' plain "Book N" folder, which every book with that number shared.
        /// </summary>
        SharedFolder,

        /// <summary>
        /// In that folder under another "Title (n)" name, which the export no longer explains: a
        /// same-titled book listed before this one has left the export since, or the order changed.
        /// </summary>
        SharedFolderOtherName,

        /// <summary>Filed as a standalone book, before the book gained series metadata.</summary>
        Standalone,

        /// <summary>Filed as a series book without a number, before it gained one.</summary>
        Unnumbered,

        /// <summary>
        /// In another folder of its title or number ("Title (2)", "Book 3 (2)"), handed out in a
        /// different list order, or in the folder a series named like it has since taken over.
        /// </summary>
        OtherBookFolder
    }

    /// <summary>Builds the copy plan for every book in <paramref name="books"/>.</summary>
    /// <param name="cancellationToken">
    /// Checked once per book: planning a large library on a slow network drive can take minutes,
    /// and Cancel has to work during that time too.
    /// </param>
    public List<PlannedCopy> Plan(
        IReadOnlyList<OpenAudible> books,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var namingPlans = books.Select(BookNaming.BuildPlan).ToList();

        // Found for every book before any is planned: which folder a book keeps depends on the others' files.
        var audioSources = new List<string?>(books.Count);
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            audioSources.Add(SourceFileLocator.FindAudioFile(book, sourceRoot));
        }

        // A file listed again (the book is in two accounts or regions, say) is the same book, so
        // only its first listing is planned, whatever title the others give it: a second title
        // would otherwise get a folder and a copy of its own, and the next run, finding the first
        // listing's folder, would move that copy in beside it and double the book's length.
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isRepeat = audioSources.Select(source => source is not null && !seenSources.Add(source)).ToList();
        var listedPlans = namingPlans.Where((_, index) => !isRepeat[index]).ToList();

        // Known before any series folder is resolved, so a series never settles on a folder that
        // is a standalone book's own (see ResolveDirectoryName).
        foreach (var plan in listedPlans.Where(plan => plan.SeriesName is null))
        {
            _standaloneBookFolders.Add(BookFolderKey(ResolveAuthorDirectory(fullDestinationRoot, plan), plan.FileStem));
        }

        // Reserve every series folder first, so a standalone book titled like a series ("Bobiverse")
        // gets a "Bobiverse (2)" folder of its own instead of being dropped loose into the series,
        // wherever it happens to sit in the list.
        foreach (var plan in listedPlans.Where(plan => plan.SeriesName is not null))
        {
            _claimedBookDirectories.TryAdd(ResolveParentDirectory(fullDestinationRoot, plan), SeriesFolderClaim);
        }

        KeepBookFolders(namingPlans, audioSources, isRepeat, fullDestinationRoot, cancellationToken);

        var planned = books
            .Select((book, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Nothing to write and no warning: the sort counts it as up to date, as the first listing copies it.
                return isRepeat[index]
                    ? new PlannedBook(new PlannedCopy { Book = book, Title = BuildTitle(book, namingPlans[index]) }, [])
                    : PlanBook(book, namingPlans[index], index, audioSources[index], sourceRoot, fullDestinationRoot);
            })
            .ToList();

        // Only now is every destination known, so an old file can be matched to its book without
        // the risk of it being a path some other book is about to write to.
        return ClaimLegacyFiles(planned, namingPlans, fullDestinationRoot, cancellationToken);
    }

    /// <summary>A destination path one book holds in this run, and the file (or stand-in) that owns it.</summary>
    /// <param name="IsMissingFromSource">The book is missing from the source, so the owner is a stand-in, not a file.</param>
    private sealed record HeldFile(string Owner, string Destination, bool IsMissingFromSource = false);

    /// <summary>A place an older version may have left a book's file.</summary>
    private sealed record LegacyCandidate(LegacyLayout Layout, string Path);

    /// <summary>One book's plan, with every destination it holds, written to or not.</summary>
    private sealed record PlannedBook(PlannedCopy Copy, IReadOnlyList<HeldFile> Held);

    private PlannedBook PlanBook(
        OpenAudible book,
        BookSortPlan plan,
        int index,
        string? audioSource,
        string sourceRoot,
        string destinationRoot)
    {
        var title = BuildTitle(book, plan);
        var pdfSource = SourceFileLocator.FindPdfFile(book, sourceRoot);

        // A book is its audio. A PDF on its own (downloaded while the audio failed, or left behind
        // when the audio was moved away) is not copied: that would report the book as sorted and
        // give library tools a book folder with nothing to play. It is copied with the audio once
        // that turns up.
        if (audioSource is null)
        {
            return new PlannedBook(
                new PlannedCopy
                {
                    Book = book,
                    Title = title,
                    IsMissingFromSource = true,
                    Warning = SourceFileLocator.HasEmptyAudioFile(book, sourceRoot) ? EmptySourceMessage : null
                },
                HoldMissingBookNames(plan, index, destinationRoot));
        }

        var copy = PlanCopy(book, plan, title, audioSource, pdfSource, destinationRoot);
        return new PlannedBook(copy, HeldFiles(copy));
    }

    /// <summary>
    /// Reserves the names a book missing from the source would be given, without writing anything.
    /// A book's names must not depend on whether its file is in the source today: otherwise the
    /// next book with the same title takes over its folder, and overwrites what may now be the
    /// missing book's only copy with its own audio. Which format the missing file was is unknown,
    /// so every one is held; at worst a same-named book is left with a duplicate, never an
    /// overwrite. (Its files from older layouts are kept safe another way: see <see cref="ClaimLegacyFile"/>.)
    /// </summary>
    private List<HeldFile> HoldMissingBookNames(BookSortPlan plan, int index, string destinationRoot)
    {
        var owner = MissingBookOwner + index;
        var targetDirectory = ResolveTargetDirectory(destinationRoot, plan, owner);

        return SourceFileLocator.AudioExtensions
            .Append(".pdf")
            .Select(extension => ClaimDestination(targetDirectory, plan.FileStem, extension, owner, destinationRoot, []))
            .OfType<string>()
            .Select(destination => new HeldFile(owner, destination, IsMissingFromSource: true))
            .ToList();
    }

    private static List<HeldFile> HeldFiles(PlannedCopy copy)
    {
        var held = new List<HeldFile>(2);
        if (copy.AudioSource is not null && copy.AudioDestination is not null)
        {
            held.Add(new HeldFile(copy.AudioSource, copy.AudioDestination));
        }

        if (copy.PdfSource is not null && copy.PdfDestination is not null)
        {
            held.Add(new HeldFile(copy.PdfSource, copy.PdfDestination));
        }

        return held;
    }

    private PlannedCopy PlanCopy(
        OpenAudible book,
        BookSortPlan plan,
        string title,
        string audioSource,
        string? pdfSource,
        string destinationRoot)
    {
        // Folders are only named here, never created: a book whose files turn out to be
        // unreadable should not leave an empty folder tree behind.
        var passedOver = new List<string>();
        var targetDirectory = ResolveTargetDirectory(destinationRoot, plan, audioSource, folder =>
        {
            var taken = MayHoldAnotherBook(folder, plan, audioSource, destinationRoot);
            if (taken)
            {
                passedOver.Add(folder);
            }

            return taken;
        });
        if (!PathSanitizer.IsWithin(destinationRoot, targetDirectory))
        {
            return new PlannedCopy
            {
                Book = book,
                Title = title,
                Warning = $"Refusing to write \"{plan.FileStem}\" outside the destination folder"
            };
        }

        string? pdfDestination = null;
        var warnings = passedOver
            .Select(folder =>
                $"Left \"{Path.GetRelativePath(destinationRoot, folder)}\" alone and filed this book in " +
                $"\"{Path.GetRelativePath(destinationRoot, targetDirectory)}\": it holds a different recording, which may be " +
                "the only copy of another book with this title or number. Delete it if it is an old copy of this book.")
            .ToList();

        var audioDestination = ClaimDestination(
            targetDirectory, plan.FileStem, Path.GetExtension(audioSource), audioSource, destinationRoot, warnings);

        if (pdfSource is not null)
        {
            pdfDestination = ClaimDestination(
                targetDirectory, plan.FileStem, ".pdf", pdfSource, destinationRoot, warnings);
        }

        return new PlannedCopy
        {
            Book = book,
            Title = title,
            TargetDirectory = audioDestination is null && pdfDestination is null ? null : targetDirectory,
            AudioSource = audioDestination is null ? null : audioSource,
            AudioDestination = audioDestination,
            PdfSource = pdfDestination is null ? null : pdfSource,
            PdfDestination = pdfDestination,
            Warning = warnings.Count > 0 ? string.Join("; ", warnings) : null
        };
    }

    /// <summary>
    /// Every book gets a folder of its own. Audiobookshelf, Plex and friends treat a folder as one
    /// book, and an audio file lying loose in an author or series folder makes them read that whole
    /// folder as a single book, hiding every series underneath it.
    /// </summary>
    /// <param name="isTaken">Whether a folder nobody has claimed in this run is taken all the same.</param>
    private string ResolveTargetDirectory(string destinationRoot, BookSortPlan plan, string owner, Func<string, bool>? isTaken = null)
    {
        if (_keptBookDirectories.TryGetValue(owner, out var kept))
        {
            return kept;
        }

        var (parentDirectory, baseName) = BookFolder(destinationRoot, plan);
        return ClaimBookDirectory(parentDirectory, baseName, owner, isTaken);
    }

    /// <summary>
    /// Where a book's folder goes, and its name before any "(2)": the title, or "Book 3" in a series.
    /// Books with the same title, or the same number in a series, share it and are told apart only
    /// by the "(n)" that follows.
    /// </summary>
    private (string Parent, string BaseName) BookFolder(string destinationRoot, BookSortPlan plan)
    {
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        return (parentDirectory, plan.SeriesSequence is null
            ? ResolveDirectoryName(parentDirectory, plan.FileStem, FolderKind.Name)
            : NumberedFolderName(plan));
    }

    /// <summary>
    /// Gives each book back the folder it is already filed in, found by its audio, before any folder
    /// is handed out in list order. The "(2)" that tells same-titled books (or two editions with one
    /// number) apart follows list order, so without this a change in that order, or a new edition
    /// listed first, gave a book another one's folder, where the update check then replaced that
    /// book's file (perhaps its only copy) with this one's.
    ///
    /// A book that cannot be placed this way (missing from the source, new, or changed since) is
    /// remembered as a possible owner of the folders it could be in: see <see cref="MayHoldAnotherBook"/>.
    /// </summary>
    private void KeepBookFolders(
        List<BookSortPlan> plans,
        List<string?> audioSources,
        List<bool> isRepeat,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < plans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isRepeat[i] || (audioSources[i] is { } source && KeepBookFolder(plans[i], source, destinationRoot)))
            {
                continue;
            }

            var owner = audioSources[i] ?? MissingBookOwner + i;
            foreach (var key in RivalKeys(plans[i], destinationRoot))
            {
                if (!_unplacedOwners.TryGetValue(key, out var owners))
                {
                    _unplacedOwners[key] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                owners.Add(owner);
            }
        }
    }

    /// <summary>Claims the folder of this book's title or number that already holds its audio, if there is one.</summary>
    private bool KeepBookFolder(BookSortPlan plan, string audioSource, string destinationRoot)
    {
        var (parentDirectory, baseName) = BookFolder(destinationRoot, plan);
        var fileName = plan.FileStem + Path.GetExtension(audioSource);
        foreach (var folder in ExistingBookFolders(parentDirectory, baseName))
        {
            var existing = FindExistingFile(Path.Combine(folder, fileName));
            if (existing is not null &&
                FileSorter.AreFilesSame(audioSource, existing) &&
                _claimedBookDirectories.TryAdd(folder, audioSource))
            {
                _keptBookDirectories[audioSource] = folder;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="folder"/>, which nobody has claimed in this run, may be another book's
    /// all the same: it holds audio that is not this book's, and a book this run could not place could
    /// have been filed there, having the same title or the same number. The export cannot tell a
    /// changed recording of this book from that book's only copy, so the folder is left to it and this
    /// book gets the next one.
    /// </summary>
    private bool MayHoldAnotherBook(string folder, BookSortPlan plan, string audioSource, string destinationRoot)
    {
        var hasRival = RivalKeys(plan, destinationRoot).Any(key =>
            _unplacedOwners.TryGetValue(key, out var owners) &&
            owners.Any(owner => !string.Equals(owner, audioSource, StringComparison.OrdinalIgnoreCase)));
        if (!hasRival)
        {
            return false;
        }

        var audio = ExistingAudioFiles(folder);
        return audio.Count > 0 && !audio.Any(file => FileSorter.AreFilesSame(audioSource, file));
    }

    /// <summary>
    /// What books that could be in each other's folders have in common: the folder name their "(n)"
    /// is counted from, or the title (and so the file name) under one author, which a book keeps when
    /// its number in the series changes.
    /// </summary>
    private IEnumerable<string> RivalKeys(BookSortPlan plan, string destinationRoot)
    {
        var (parentDirectory, baseName) = BookFolder(destinationRoot, plan);
        return
        [
            $"folder\u0000{Path.Combine(parentDirectory, baseName)}",
            $"title\u0000{ResolveAuthorDirectory(destinationRoot, plan)}\u0000{PathSanitizer.NormalizeComparisonKey(plan.FileStem)}"
        ];
    }

    /// <summary>"Name", "Name (2)", "Name (3)"... inside <paramref name="parentDirectory"/> that are on disk, in that order.</summary>
    private IEnumerable<string> ExistingBookFolders(string parentDirectory, string baseName)
    {
        if (!_subdirectoryNames.TryGetValue(parentDirectory, out var names))
        {
            // Looked up without regard to case, as folders are claimed; spelled as on disk.
            names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SafeEnumerateDirectories(parentDirectory).Select(Path.GetFileName).OfType<string>())
            {
                names.TryAdd(name, name);
            }

            _subdirectoryNames[parentDirectory] = names;
        }

        return Enumerable.Range(1, MaxDisambiguationAttempts)
            .Select(attempt => names.GetValueOrDefault(baseName + Suffix(attempt)))
            .OfType<string>()
            .Select(name => Path.Combine(parentDirectory, name));
    }

    /// <summary>The audio files on disk in <paramref name="folder"/>.</summary>
    private List<string> ExistingAudioFiles(string folder)
    {
        return GetDirectoryFileIndex(folder).Values
            .Where(name => SourceFileLocator.AudioExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            .Select(name => Path.Combine(folder, name))
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>
    /// "Book 3": the folder a numbered series book is filed in. Two books can have the same number
    /// (two narrations of one book), so this is only the first one's; see <see cref="ClaimBookDirectory"/>.
    /// </summary>
    private static string NumberedFolderName(BookSortPlan plan)
    {
        return $"Book {plan.SeriesSequence}";
    }

    private string ResolveAuthorDirectory(string destinationRoot, BookSortPlan plan)
    {
        return Path.Combine(destinationRoot, ResolveDirectoryName(destinationRoot, plan.Author, FolderKind.Name));
    }

    /// <summary>The author folder, or the series folder inside it when the book is in a series.</summary>
    private string ResolveParentDirectory(string destinationRoot, BookSortPlan plan)
    {
        var authorDirectory = ResolveAuthorDirectory(destinationRoot, plan);

        return plan.SeriesName is null
            ? authorDirectory
            : Path.Combine(authorDirectory, ResolveDirectoryName(authorDirectory, plan.SeriesName, FolderKind.Series));
    }

    /// <summary>
    /// Names a book's own folder. Two different books with the same title, or the same series
    /// number, must not share one, or a library tool would merge them into a single book, so the
    /// second becomes "Title (2)" or "Book 1 (2)", in list order. That order only decides the first
    /// time: after that, each keeps the folder that holds its audio (see <see cref="KeepBookFolders"/>).
    /// </summary>
    private string ClaimBookDirectory(string parentDirectory, string baseName, string owner, Func<string, bool>? isTaken)
    {
        var claim = ClaimFirstFree(
            _claimedBookDirectories, owner, suffix => Path.Combine(parentDirectory, baseName + suffix), isTaken);

        // Out of names: share the folder; ClaimDestination still keeps the files apart.
        return claim?.Path ?? Path.Combine(parentDirectory, baseName);
    }

    /// <summary>
    /// Finds the copy of each book that an older version filed somewhere else, so the sort can move
    /// it into the book's folder instead of copying the book a second time and leaving the old file
    /// behind to keep confusing library tools. Books missing from the source take part only in the
    /// replay of the old names, which has to see them; nothing is moved for them.
    /// </summary>
    private List<PlannedCopy> ClaimLegacyFiles(
        List<PlannedBook> planned,
        List<BookSortPlan> namingPlans,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var held = planned
            .SelectMany((book, i) => book.Held.Select(file => (File: file, Plan: namingPlans[i])))
            .ToList();

        // Worked out for every book, in list order, before anything is claimed: the old names are
        // a replay of the old list-order naming, which has to see every book to come out right.
        var candidates = new List<List<LegacyCandidate>>(held.Count);
        foreach (var (file, plan) in held)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(LegacyCandidates(plan, file, destinationRoot));
        }

        var legacy = new string?[held.Count];
        foreach (var layout in Enum.GetValues<LegacyLayout>())
        {
            for (var i = 0; i < held.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                legacy[i] ??= ClaimLegacyFile(candidates[i], layout, held[i].File.Owner, destinationRoot);
            }
        }

        var legacyByDestination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var leftInPlaceByDestination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < held.Count; i++)
        {
            if (legacy[i] is { } path)
            {
                legacyByDestination[held[i].File.Destination] = path;
            }

            if (LeftInPlace(candidates[i], destinationRoot) is { } left)
            {
                leftInPlaceByDestination[held[i].File.Destination] = left;
            }
        }

        // A missing book has no destinations of its own to move a file to, and no candidates.
        return planned
            .Select(book => book.Copy with
            {
                AudioLegacyPath = LegacyFileFor(book.Copy.AudioDestination),
                PdfLegacyPath = LegacyFileFor(book.Copy.PdfDestination),
                Warning = JoinWarnings(
                    book.Copy.Warning,
                    LeftInPlaceWarningFor(book.Copy.AudioDestination),
                    LeftInPlaceWarningFor(book.Copy.PdfDestination))
            })
            .ToList();

        string? LegacyFileFor(string? destination)
        {
            return destination is null ? null : legacyByDestination.GetValueOrDefault(destination);
        }

        string? LeftInPlaceWarningFor(string? destination)
        {
            return destination is not null && leftInPlaceByDestination.TryGetValue(destination, out var left)
                ? $"Left \"{Path.GetRelativePath(destinationRoot, left)}\" where it is: it is not the same as this " +
                  "book's file in the source folder, so it may be another book's. Delete it if it is an old copy of this book."
                : null;
        }
    }

    private static string? JoinWarnings(params string?[] warnings)
    {
        var present = warnings.OfType<string>().ToList();
        return present.Count > 0 ? string.Join("; ", present) : null;
    }

    /// <summary>
    /// Where older versions may have left the file that belongs at <paramref name="file"/>'s
    /// destination, most likely first. Empty when it is already where it belongs, and for a book
    /// missing from the source, which has nothing to compare an old file with.
    /// </summary>
    private List<LegacyCandidate> LegacyCandidates(BookSortPlan plan, HeldFile file, string destinationRoot)
    {
        var stem = plan.FileStem;
        var extension = Path.GetExtension(file.Destination);
        var fileName = stem + extension;
        var authorDirectory = ResolveAuthorDirectory(destinationRoot, plan);
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);
        var sharedDirectory = plan.SeriesSequence is null
            ? parentDirectory
            : Path.Combine(parentDirectory, NumberedFolderName(plan));

        // Replayed even when the file is already in place: later books' old names depend on it.
        var candidates = SharedFolderCandidates(sharedDirectory, stem, extension, file);

        if (file.IsMissingFromSource || File.Exists(file.Destination))
        {
            return [];
        }

        candidates.AddRange(OtherSharedFolderNames(sharedDirectory, stem, extension));

        if (plan.SeriesName is not null)
        {
            candidates.Add(new LegacyCandidate(LegacyLayout.Standalone, Path.Combine(authorDirectory, fileName)));
            candidates.Add(new LegacyCandidate(LegacyLayout.Standalone, Path.Combine(BookFolderIn(authorDirectory, stem), fileName)));
        }

        if (plan.SeriesSequence is not null)
        {
            candidates.Add(new LegacyCandidate(LegacyLayout.Unnumbered, Path.Combine(BookFolderIn(parentDirectory, stem), fileName)));
            candidates.Add(new LegacyCandidate(LegacyLayout.Unnumbered, Path.Combine(parentDirectory, fileName)));
        }

        var (bookParent, baseName) = BookFolder(destinationRoot, plan);
        var ownFolder = Path.GetDirectoryName(file.Destination);
        candidates.AddRange(ExistingBookFolders(bookParent, baseName)
            .Where(folder => !string.Equals(folder, ownFolder, StringComparison.OrdinalIgnoreCase))
            .Select(folder => new LegacyCandidate(LegacyLayout.OtherBookFolder, Path.Combine(folder, fileName))));

        return candidates;
    }

    /// <summary>
    /// The name an older version gave this book in the folder it shared with other books. Those
    /// versions gave a book with no file no name, so the old names are replayed that way. But a book
    /// missing from the source today may have had its file back then, and taken the plain name before
    /// a same-titled book: so the names are also replayed as if every missing book had had its file.
    /// Where the two replays differ, both are tried; the audio says which one is this book's.
    /// </summary>
    private List<LegacyCandidate> SharedFolderCandidates(string directory, string stem, string extension, HeldFile file)
    {
        var ifMissingHadFiles = ReplaySharedFolderName(_claimedSharedFolderNamesIfMissingHadFiles, directory, stem, extension, file.Owner);
        var asReplayed = file.IsMissingFromSource
            ? null
            : ReplaySharedFolderName(_claimedSharedFolderNames, directory, stem, extension, file.Owner);

        return new[] { asReplayed, ifMissingHadFiles }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new LegacyCandidate(LegacyLayout.SharedFolder, path))
            .ToList();
    }

    /// <summary>
    /// Every other copy of the name in <paramref name="directory"/>: the plain name and each
    /// "Title (n)" on disk. A book that has left the export still has its file there, so the replay
    /// can give this book a name that was another's, and this book's own file another name.
    /// </summary>
    private IEnumerable<LegacyCandidate> OtherSharedFolderNames(string directory, string stem, string extension)
    {
        return Enumerable.Range(1, MaxDisambiguationAttempts)
            .Select(attempt => Path.Combine(directory, stem + Suffix(attempt) + extension))
            .Where(path => FindExistingFile(path) is not null)
            .Select(path => new LegacyCandidate(LegacyLayout.SharedFolderOtherName, path));
    }

    /// <summary>
    /// The name an older version gave a book it filed in a folder it shared with other books (see
    /// <see cref="LegacyLayout.SharedFolder"/>). Those versions named files one at a time, in list
    /// order: the plain name, or "Title (2)" only when another book had already taken the plain
    /// name with the same extension. That numbering has nothing to do with the "(2)" of the new
    /// book folders (which ignore extensions and make way for series folders), so it is replayed
    /// on its own. Null when this file was a repeat of another book's and so never got a name of its own.
    /// </summary>
    /// <param name="claims">The names given so far in this replay.</param>
    private string? ReplaySharedFolderName(
        Dictionary<string, string> claims,
        string directory,
        string stem,
        string extension,
        string owner)
    {
        var fileIndex = GetDirectoryFileIndex(directory);
        var plainName = fileIndex.TryGetValue(BuildFileIndexKey(stem, extension), out var existingName)
            ? existingName
            : stem + extension;

        var claim = ClaimFirstFree(
            claims,
            owner,
            suffix => Path.Combine(directory, suffix.Length == 0 ? plainName : $"{stem}{suffix}{extension}"));

        if (claim is null || claim.AlreadyOwned)
        {
            return null;
        }

        // As the old versions did, so a later title spelled differently but meaning the same
        // collides with this one. A name that is not on disk is never matched: see FindExistingFile.
        fileIndex.TryAdd(BuildFileIndexKey(Path.GetFileNameWithoutExtension(claim.Path), extension), Path.GetFileName(claim.Path));
        return claim.Path;
    }

    /// <summary>
    /// The first candidate of <paramref name="layout"/> that exists, is not already taken and holds
    /// the same audio as <paramref name="source"/> (the quick update check). A name alone never
    /// makes a file this book's: whose it is depends on history the export does not tell. A
    /// same-titled book may be missing from the source or have left the export, and its file may
    /// be the only copy, which the update check that follows the move would overwrite.
    /// </summary>
    private string? ClaimLegacyFile(List<LegacyCandidate> candidates, LegacyLayout layout, string source, string destinationRoot)
    {
        foreach (var candidate in candidates.Where(candidate => candidate.Layout == layout))
        {
            var existing = FindExistingFile(candidate.Path);
            if (existing is not null &&
                PathSanitizer.IsWithin(destinationRoot, existing) &&
                !IsWrittenInThisRun(existing) &&
                !_claimedLegacyFiles.Contains(existing) &&
                FileSorter.AreFilesSame(source, existing))
            {
                _claimedLegacyFiles.Add(existing);
                return existing;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a book in the source will write to <paramref name="path"/>. A missing book's names
    /// are only held so that nobody else writes there; a file at one that holds another book's
    /// audio is that book's (older versions put every book with one number in one "Book N" folder).
    /// </summary>
    private bool IsWrittenInThisRun(string path)
    {
        return _claimedDestinations.TryGetValue(path, out var owner) &&
               !owner.StartsWith(MissingBookOwner, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file at the name the replay gave this book, when nobody took it because it is not the
    /// same as the source. It may be a stale copy of this book or the only copy of a book that has
    /// left the export, and either way it stays in a shared folder where library tools trip over
    /// it, so the person is told. Never a file some book is filed at in this run.
    /// </summary>
    private string? LeftInPlace(List<LegacyCandidate> candidates, string destinationRoot)
    {
        var ownName = candidates.FirstOrDefault(candidate => candidate.Layout == LegacyLayout.SharedFolder);
        var existing = ownName is null ? null : FindExistingFile(ownName.Path);

        return existing is not null &&
               PathSanitizer.IsWithin(destinationRoot, existing) &&
               !_claimedDestinations.ContainsKey(existing) &&
               !_claimedLegacyFiles.Contains(existing)
            ? existing
            : null;
    }

    /// <summary>The file on disk that <paramref name="path"/> names, allowing for equivalent spellings.</summary>
    private string? FindExistingFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return null;
        }

        var key = BuildFileIndexKey(Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        if (!GetDirectoryFileIndex(directory).TryGetValue(key, out var name))
        {
            return null;
        }

        // The index also holds names planned in this run, which are not on disk yet.
        var existing = Path.Combine(directory, name);
        return File.Exists(existing) ? existing : null;
    }

    /// <summary>The folder a book titled <paramref name="stem"/> was given inside <paramref name="parentDirectory"/>.</summary>
    private string BookFolderIn(string parentDirectory, string stem)
    {
        return Path.Combine(parentDirectory, ResolveDirectoryName(parentDirectory, stem, FolderKind.Name));
    }

    /// <summary>
    /// Picks the folder name to use inside <paramref name="parentDirectory"/>, preferring a folder
    /// that already exists and means the same thing so repeat runs do not fragment a library.
    /// </summary>
    private string ResolveDirectoryName(string parentDirectory, string requestedName, FolderKind kind)
    {
        var normalized = Normalize(requestedName, kind);
        if (normalized.Length == 0)
        {
            return requestedName;
        }

        // The kind is part of the key: "The Witcher" as a series and "Witcher" as a book normalise
        // to the same text under different rules, and must not be given each other's folder.
        var cacheKey = $"{parentDirectory}\u0000{kind}\u0000{normalized}";
        if (_resolvedDirectoryNames.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var existingNames = SafeEnumerateDirectories(parentDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        // The folder this name was given before comes first. The looser series match would
        // otherwise let "The Witcher" settle on a standalone book's "Witcher" folder whenever the
        // disk happens to list that one first, and copy both the series and the book again.
        var exactKey = PathSanitizer.NormalizeComparisonKey(requestedName);
        var resolved = existingNames.FirstOrDefault(name => PathSanitizer.NormalizeComparisonKey(name) == exactKey)
            ?? existingNames.FirstOrDefault(name =>
                Normalize(name, kind) == normalized &&
                !(kind == FolderKind.Series && _standaloneBookFolders.Contains(BookFolderKey(parentDirectory, name))))
            ?? requestedName;

        _resolvedDirectoryNames[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>Identifies the folder a standalone book titled <paramref name="name"/> uses in <paramref name="parentDirectory"/>.</summary>
    private static string BookFolderKey(string parentDirectory, string name)
    {
        return $"{parentDirectory}\u0000{PathSanitizer.NormalizeComparisonKey(name)}";
    }

    /// <summary>
    /// Reserves a destination path for one source file, reusing an equivalent file that is already
    /// there and disambiguating when two different books would land on the same name.
    /// </summary>
    private string? ClaimDestination(
        string directory,
        string stem,
        string extension,
        string owner,
        string destinationRoot,
        List<string> warnings)
    {
        extension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;

        var fileIndex = GetDirectoryFileIndex(directory);
        var indexKey = BuildFileIndexKey(stem, extension);

        var fileName = fileIndex.TryGetValue(indexKey, out var existingName)
            ? existingName
            : $"{stem}{extension}";

        var claim = ClaimFirstFree(
            _claimedDestinations,
            owner,
            suffix => Path.Combine(directory, suffix.Length == 0 ? fileName : $"{stem}{suffix}{extension}"));

        if (claim is null)
        {
            warnings.Add($"Could not find a free file name for \"{stem}{extension}\"");
            return null;
        }

        if (!PathSanitizer.IsWithin(destinationRoot, claim.Path))
        {
            warnings.Add($"Refusing to write \"{Path.GetFileName(claim.Path)}\" outside the destination folder");
            return null;
        }

        // The same physical file listed twice in the export: copy it once.
        if (claim.AlreadyOwned)
        {
            return null;
        }

        fileIndex.TryAdd(BuildFileIndexKey(Path.GetFileNameWithoutExtension(claim.Path), extension), Path.GetFileName(claim.Path));
        return claim.Path;
    }

    /// <summary>A path reserved by <see cref="ClaimFirstFree"/>.</summary>
    /// <param name="AlreadyOwned">The owner had claimed this path earlier in the run.</param>
    private sealed record Claim(string Path, bool AlreadyOwned);

    /// <summary>
    /// Reserves the first of "Name", "Name (2)", "Name (3)"... that nobody else has claimed in this
    /// run. An owner asking again gets its own earlier claim back. Null when every name is taken.
    /// </summary>
    /// <param name="isTaken">Whether a name nobody has claimed is taken all the same; null when none is.</param>
    private static Claim? ClaimFirstFree(
        Dictionary<string, string> claims,
        string owner,
        Func<string, string> pathForSuffix,
        Func<string, bool>? isTaken = null)
    {
        for (var attempt = 1; attempt <= MaxDisambiguationAttempts; attempt++)
        {
            var candidate = pathForSuffix(Suffix(attempt));

            if (claims.TryGetValue(candidate, out var claimant))
            {
                if (string.Equals(claimant, owner, StringComparison.OrdinalIgnoreCase))
                {
                    return new Claim(candidate, AlreadyOwned: true);
                }

                continue;
            }

            if (isTaken?.Invoke(candidate) != true)
            {
                claims.Add(candidate, owner);
                return new Claim(candidate, AlreadyOwned: false);
            }
        }

        return null;
    }

    /// <summary>"", " (2)", " (3)"...: what tells apart the names given out one after another.</summary>
    private static string Suffix(int attempt)
    {
        return attempt == 1 ? string.Empty : $" ({attempt})";
    }

    private static string Normalize(string name, FolderKind kind)
    {
        return kind == FolderKind.Series
            ? PathSanitizer.NormalizeSeriesKey(name)
            : PathSanitizer.NormalizeComparisonKey(name);
    }

    private Dictionary<string, string> GetDirectoryFileIndex(string directory)
    {
        if (_directoryFileIndex.TryGetValue(directory, out var index))
        {
            return index;
        }

        index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in SafeEnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            index.TryAdd(
                BuildFileIndexKey(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name)),
                name);
        }

        _directoryFileIndex[directory] = index;
        return index;
    }

    private static string BuildFileIndexKey(string stem, string extension)
    {
        return $"{PathSanitizer.NormalizeComparisonKey(stem)}\u0000{extension.ToLowerInvariant()}";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"We Are Legion (We Are Bob) — Dennis E. Taylor": how a person would name the book.</summary>
    private static string BuildTitle(OpenAudible book, BookSortPlan plan)
    {
        var title = string.IsNullOrWhiteSpace(book.Title) ? plan.FileStem : book.Title.Trim();
        return $"{title} — {plan.Author}";
    }
}
