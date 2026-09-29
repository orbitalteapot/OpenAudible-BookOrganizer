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

    private readonly Dictionary<string, string> _resolvedDirectoryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _directoryFileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedDestinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedBookDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedLooseNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimedLegacyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _standaloneBookFolders = new(StringComparer.Ordinal);

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
        /// <summary>Loose in the author folder, or in the series folder for a series book without a number.</summary>
        Loose,

        /// <summary>Filed as a standalone book, before the book gained series metadata.</summary>
        Standalone,

        /// <summary>Filed as a series book without a number, before it gained one.</summary>
        Unnumbered
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

        // Known before any series folder is resolved, so a series never settles on a folder that
        // is a standalone book's own (see ResolveDirectoryName).
        foreach (var plan in namingPlans.Where(plan => plan.SeriesName is null))
        {
            _standaloneBookFolders.Add(BookFolderKey(ResolveAuthorDirectory(fullDestinationRoot, plan), plan.FileStem));
        }

        // Reserve every series folder first, so a standalone book titled like a series ("Bobiverse")
        // gets a "Bobiverse (2)" folder of its own instead of being dropped loose into the series,
        // wherever it happens to sit in the list.
        foreach (var plan in namingPlans.Where(plan => plan.SeriesName is not null))
        {
            _claimedBookDirectories.TryAdd(ResolveParentDirectory(fullDestinationRoot, plan), SeriesFolderClaim);
        }

        var planned = books
            .Select((book, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PlanBook(book, namingPlans[index], index, sourceRoot, fullDestinationRoot);
            })
            .ToList();

        // Only now is every destination known, so an old file can be matched to its book without
        // the risk of it being a path some other book is about to write to.
        return ClaimLegacyFiles(planned, namingPlans, fullDestinationRoot, cancellationToken);
    }

    /// <summary>A destination path one book holds in this run, and the file (or stand-in) that owns it.</summary>
    private sealed record HeldFile(string Owner, string Destination);

    /// <summary>One book's plan, with every destination it holds, written to or not.</summary>
    private sealed record PlannedBook(PlannedCopy Copy, IReadOnlyList<HeldFile> Held);

    private PlannedBook PlanBook(OpenAudible book, BookSortPlan plan, int index, string sourceRoot, string destinationRoot)
    {
        var title = BuildTitle(book, plan);

        var audioSource = SourceFileLocator.FindAudioFile(book, sourceRoot);
        var pdfSource = SourceFileLocator.FindPdfFile(book, sourceRoot);

        if (audioSource is null && pdfSource is null)
        {
            return new PlannedBook(
                new PlannedCopy { Book = book, Title = title, IsMissingFromSource = true },
                HoldMissingBookNames(plan, index, destinationRoot));
        }

        var copy = PlanCopy(book, plan, title, audioSource, pdfSource, destinationRoot);
        return new PlannedBook(copy, HeldFiles(copy));
    }

    /// <summary>
    /// Reserves the names a book missing from the source would be given, without writing anything.
    /// A book's names must not depend on whether its file is in the source today: otherwise the
    /// next book with the same title takes over its folder or its old loose file, and overwrites
    /// what may now be the missing book's only copy with its own audio. Which format the missing
    /// file was is unknown, so every one is held; at worst a same-named book is left with a
    /// duplicate, never an overwrite.
    /// </summary>
    private List<HeldFile> HoldMissingBookNames(BookSortPlan plan, int index, string destinationRoot)
    {
        // Stands in for the source path as the owner of the names; it can never be a real path.
        var owner = $"\u0000missing\u0000{index}";
        var targetDirectory = ResolveTargetDirectory(destinationRoot, plan, owner);

        return SourceFileLocator.AudioExtensions
            .Append(".pdf")
            .Select(extension => ClaimDestination(targetDirectory, plan.FileStem, extension, owner, destinationRoot, []))
            .OfType<string>()
            .Select(destination => new HeldFile(owner, destination))
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
        string? audioSource,
        string? pdfSource,
        string destinationRoot)
    {
        // Folders are only named here, never created: a book whose files turn out to be
        // unreadable should not leave an empty folder tree behind.
        var targetDirectory = ResolveTargetDirectory(destinationRoot, plan, audioSource ?? pdfSource!);
        if (!PathSanitizer.IsWithin(destinationRoot, targetDirectory))
        {
            return new PlannedCopy
            {
                Book = book,
                Title = title,
                Warning = $"Refusing to write \"{plan.FileStem}\" outside the destination folder"
            };
        }

        string? audioDestination = null;
        string? pdfDestination = null;
        var warnings = new List<string>();

        if (audioSource is not null)
        {
            audioDestination = ClaimDestination(
                targetDirectory, plan.FileStem, Path.GetExtension(audioSource), audioSource, destinationRoot, warnings);
        }

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
    private string ResolveTargetDirectory(string destinationRoot, BookSortPlan plan, string owner)
    {
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        // "Book 3" already is a folder per book, and always has been.
        return plan.SeriesSequence is null
            ? ClaimBookDirectory(parentDirectory, plan.FileStem, owner)
            : Path.Combine(parentDirectory, $"Book {plan.SeriesSequence}");
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
    /// Names the folder for a book filed by title. Two different books with the same title must
    /// not share one, or a library tool would merge them into a single book, so the second becomes
    /// "Title (2)", in list order.
    /// </summary>
    private string ClaimBookDirectory(string parentDirectory, string title, string owner)
    {
        var baseName = ResolveDirectoryName(parentDirectory, title, FolderKind.Name);
        var claim = ClaimFirstFree(
            _claimedBookDirectories, owner, suffix => Path.Combine(parentDirectory, baseName + suffix));

        // Out of names: share the folder; ClaimDestination still keeps the files apart.
        return claim?.Path ?? Path.Combine(parentDirectory, baseName);
    }

    /// <summary>
    /// Finds the copy of each book that an older version filed somewhere else, so the sort can move
    /// it into the book's folder instead of copying the book a second time and leaving the old file
    /// behind to keep confusing library tools. Books missing from the source take part too: they
    /// claim their old files like any other book, so no other book can take them, but nothing of
    /// theirs is moved.
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
        var candidates = new List<List<(LegacyLayout Layout, string Path)>>(held.Count);
        foreach (var (file, plan) in held)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(LegacyCandidates(plan, file.Owner, file.Destination, destinationRoot));
        }

        var legacy = new string?[held.Count];
        foreach (var layout in Enum.GetValues<LegacyLayout>())
        {
            for (var i = 0; i < held.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                legacy[i] ??= ClaimLegacyFile(candidates[i], layout, destinationRoot);
            }
        }

        var legacyByDestination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < held.Count; i++)
        {
            if (legacy[i] is { } path)
            {
                legacyByDestination[held[i].File.Destination] = path;
            }
        }

        // A missing book has no destinations of its own to move a file to, so its claims stay put.
        return planned
            .Select(book => book.Copy with
            {
                AudioLegacyPath = LegacyFileFor(book.Copy.AudioDestination),
                PdfLegacyPath = LegacyFileFor(book.Copy.PdfDestination)
            })
            .ToList();

        string? LegacyFileFor(string? destination)
        {
            return destination is null ? null : legacyByDestination.GetValueOrDefault(destination);
        }
    }

    /// <summary>
    /// Where older versions may have left the file that belongs at <paramref name="destination"/>,
    /// most likely first. Empty when it is already where it belongs.
    /// </summary>
    private List<(LegacyLayout Layout, string Path)> LegacyCandidates(
        BookSortPlan plan,
        string owner,
        string destination,
        string destinationRoot)
    {
        var candidates = new List<(LegacyLayout Layout, string Path)>();
        var stem = plan.FileStem;
        var extension = Path.GetExtension(destination);
        var fileName = stem + extension;
        var authorDirectory = ResolveAuthorDirectory(destinationRoot, plan);
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        // Replayed even when the file is already in place: later books' old names depend on it.
        var looseFile = plan.SeriesSequence is null
            ? ReplayLooseFileName(parentDirectory, stem, extension, owner)
            : null;

        if (File.Exists(destination))
        {
            return candidates;
        }

        if (looseFile is not null)
        {
            candidates.Add((LegacyLayout.Loose, looseFile));
        }

        if (plan.SeriesName is not null)
        {
            candidates.Add((LegacyLayout.Standalone, Path.Combine(authorDirectory, fileName)));
            candidates.Add((LegacyLayout.Standalone, Path.Combine(BookFolderIn(authorDirectory, stem), fileName)));
        }

        if (plan.SeriesSequence is not null)
        {
            candidates.Add((LegacyLayout.Unnumbered, Path.Combine(BookFolderIn(parentDirectory, stem), fileName)));
            candidates.Add((LegacyLayout.Unnumbered, Path.Combine(parentDirectory, fileName)));
        }

        return candidates;
    }

    /// <summary>
    /// The name an older version gave a book it left loose in <paramref name="directory"/>. Those
    /// versions named files one at a time, in list order: the plain name, or "Title (2)" only when
    /// another book had already taken the plain name with the same extension. That numbering has
    /// nothing to do with the "(2)" of the new book folders (which ignore extensions and make way
    /// for series folders), so it is replayed on its own. Null when this file was a repeat of
    /// another book's and so never got a name of its own.
    /// </summary>
    private string? ReplayLooseFileName(string directory, string stem, string extension, string owner)
    {
        var fileIndex = GetDirectoryFileIndex(directory);
        var plainName = fileIndex.TryGetValue(BuildFileIndexKey(stem, extension), out var existingName)
            ? existingName
            : stem + extension;

        var claim = ClaimFirstFree(
            _claimedLooseNames,
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

    /// <summary>The first candidate of <paramref name="layout"/> that exists and nothing else owns.</summary>
    private string? ClaimLegacyFile(List<(LegacyLayout Layout, string Path)> candidates, LegacyLayout layout, string destinationRoot)
    {
        foreach (var candidate in candidates.Where(candidate => candidate.Layout == layout))
        {
            var existing = FindExistingFile(candidate.Path);
            if (existing is not null &&
                PathSanitizer.IsWithin(destinationRoot, existing) &&
                !_claimedDestinations.ContainsKey(existing) &&
                _claimedLegacyFiles.Add(existing))
            {
                return existing;
            }
        }

        return null;
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
    private static Claim? ClaimFirstFree(Dictionary<string, string> claims, string owner, Func<string, string> pathForSuffix)
    {
        for (var attempt = 1; attempt <= MaxDisambiguationAttempts; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $" ({attempt})";
            var candidate = pathForSuffix(suffix);

            if (claims.TryAdd(candidate, owner))
            {
                return new Claim(candidate, AlreadyOwned: false);
            }

            if (string.Equals(claims[candidate], owner, StringComparison.OrdinalIgnoreCase))
            {
                return new Claim(candidate, AlreadyOwned: true);
            }
        }

        return null;
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
