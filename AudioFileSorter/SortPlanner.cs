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
    public List<PlannedCopy> Plan(IReadOnlyList<OpenAudible> books, string sourceRoot, string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var namingPlans = books.Select(BookNaming.BuildPlan).ToList();

        // Reserve every series folder first, so a standalone book titled like a series ("Bobiverse")
        // gets a "Bobiverse (2)" folder of its own instead of being dropped loose into the series,
        // wherever it happens to sit in the list.
        foreach (var plan in namingPlans.Where(plan => plan.SeriesName is not null))
        {
            _claimedBookDirectories.TryAdd(ResolveParentDirectory(fullDestinationRoot, plan), SeriesFolderClaim);
        }

        var planned = books
            .Select((book, index) => PlanBook(book, namingPlans[index], sourceRoot, fullDestinationRoot))
            .ToList();

        // Only now is every destination known, so an old file can be matched to its book without
        // the risk of it being a path some other book is about to write to.
        return ClaimLegacyFiles(planned, namingPlans, fullDestinationRoot);
    }

    private PlannedCopy PlanBook(OpenAudible book, BookSortPlan plan, string sourceRoot, string destinationRoot)
    {
        var title = BuildTitle(book, plan);

        var audioSource = SourceFileLocator.FindAudioFile(book, sourceRoot);
        var pdfSource = SourceFileLocator.FindPdfFile(book, sourceRoot);

        if (audioSource is null && pdfSource is null)
        {
            return new PlannedCopy { Book = book, Title = title, IsMissingFromSource = true };
        }

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
    private string ResolveTargetDirectory(string destinationRoot, BookSortPlan plan, string sourcePath)
    {
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        // "Book 3" already is a folder per book, and always has been.
        return plan.SeriesSequence is null
            ? ClaimBookDirectory(parentDirectory, plan.FileStem, sourcePath)
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
    private string ClaimBookDirectory(string parentDirectory, string title, string sourcePath)
    {
        var baseName = ResolveDirectoryName(parentDirectory, title, FolderKind.Name);
        var claim = ClaimFirstFree(
            _claimedBookDirectories, sourcePath, suffix => Path.Combine(parentDirectory, baseName + suffix));

        // Out of names: share the folder; ClaimDestination still keeps the files apart.
        return claim?.Path ?? Path.Combine(parentDirectory, baseName);
    }

    /// <summary>
    /// Finds the copy of each book that an older version filed somewhere else, so the sort can move
    /// it into the book's folder instead of copying the book a second time and leaving the old file
    /// behind to keep confusing library tools.
    /// </summary>
    private List<PlannedCopy> ClaimLegacyFiles(List<PlannedCopy> planned, List<BookSortPlan> namingPlans, string destinationRoot)
    {
        // Worked out for every book, in list order, before anything is claimed: the old names are
        // a replay of the old list-order naming, which has to see every book to come out right.
        var audioCandidates = new List<List<(LegacyLayout Layout, string Path)>>(planned.Count);
        var pdfCandidates = new List<List<(LegacyLayout Layout, string Path)>>(planned.Count);
        for (var i = 0; i < planned.Count; i++)
        {
            var copy = planned[i];
            audioCandidates.Add(LegacyCandidates(namingPlans[i], copy.AudioSource, copy.AudioDestination, destinationRoot));
            pdfCandidates.Add(LegacyCandidates(namingPlans[i], copy.PdfSource, copy.PdfDestination, destinationRoot));
        }

        var audioLegacy = new string?[planned.Count];
        var pdfLegacy = new string?[planned.Count];
        foreach (var layout in Enum.GetValues<LegacyLayout>())
        {
            for (var i = 0; i < planned.Count; i++)
            {
                audioLegacy[i] ??= ClaimLegacyFile(audioCandidates[i], layout, destinationRoot);
                pdfLegacy[i] ??= ClaimLegacyFile(pdfCandidates[i], layout, destinationRoot);
            }
        }

        return planned
            .Select((copy, i) => copy with { AudioLegacyPath = audioLegacy[i], PdfLegacyPath = pdfLegacy[i] })
            .ToList();
    }

    /// <summary>
    /// Where older versions may have left one of a book's files, most likely first. Empty when the
    /// book has nothing to write or its file is already where it belongs.
    /// </summary>
    private List<(LegacyLayout Layout, string Path)> LegacyCandidates(
        BookSortPlan plan,
        string? sourcePath,
        string? destination,
        string destinationRoot)
    {
        var candidates = new List<(LegacyLayout Layout, string Path)>();
        if (sourcePath is null || destination is null)
        {
            return candidates;
        }

        var stem = plan.FileStem;
        var extension = Path.GetExtension(destination);
        var fileName = stem + extension;
        var authorDirectory = ResolveAuthorDirectory(destinationRoot, plan);
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        // Replayed even when the file is already in place: later books' old names depend on it.
        var looseFile = plan.SeriesSequence is null
            ? ReplayLooseFileName(parentDirectory, stem, extension, sourcePath)
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
    private string? ReplayLooseFileName(string directory, string stem, string extension, string sourcePath)
    {
        var fileIndex = GetDirectoryFileIndex(directory);
        var plainName = fileIndex.TryGetValue(BuildFileIndexKey(stem, extension), out var existingName)
            ? existingName
            : stem + extension;

        var claim = ClaimFirstFree(
            _claimedLooseNames,
            sourcePath,
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

        var resolved = requestedName;
        foreach (var existingDirectory in SafeEnumerateDirectories(parentDirectory))
        {
            var existingName = Path.GetFileName(existingDirectory);
            if (!string.IsNullOrWhiteSpace(existingName) && Normalize(existingName, kind) == normalized)
            {
                resolved = existingName;
                break;
            }
        }

        _resolvedDirectoryNames[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// Reserves a destination path for one source file, reusing an equivalent file that is already
    /// there and disambiguating when two different books would land on the same name.
    /// </summary>
    private string? ClaimDestination(
        string directory,
        string stem,
        string extension,
        string sourcePath,
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
            sourcePath,
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
