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
    private readonly HashSet<string> _claimedLegacyFiles = new(StringComparer.OrdinalIgnoreCase);

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

        return books
            .Select((book, index) => PlanBook(book, namingPlans[index], sourceRoot, fullDestinationRoot))
            .ToList();
    }

    private PlannedCopy PlanBook(OpenAudible book, BookSortPlan plan, string sourceRoot, string destinationRoot)
    {
        var label = BuildLabel(book, plan);

        var audioSource = SourceFileLocator.FindAudioFile(book, sourceRoot);
        var pdfSource = SourceFileLocator.FindPdfFile(book, sourceRoot);

        if (audioSource is null && pdfSource is null)
        {
            return new PlannedCopy
            {
                Book = book,
                Label = label,
                Warning = $"No source file found for \"{plan.FileStem}\" (file name: {book.Filename ?? "n/a"})"
            };
        }

        // Folders are only named here, never created: a book whose files turn out to be
        // unreadable should not leave an empty folder tree behind.
        var target = ResolveTargetDirectory(destinationRoot, plan, audioSource ?? pdfSource!);
        var targetDirectory = target.Directory;
        if (!PathSanitizer.IsWithin(destinationRoot, targetDirectory))
        {
            return new PlannedCopy
            {
                Book = book,
                Label = label,
                Warning = $"Refusing to write \"{plan.FileStem}\" outside the destination folder"
            };
        }

        string? audioDestination = null;
        string? pdfDestination = null;
        string? audioLegacy = null;
        string? pdfLegacy = null;
        var warnings = new List<string>();

        if (audioSource is not null)
        {
            var extension = Path.GetExtension(audioSource);
            audioDestination = ClaimDestination(
                targetDirectory, plan.FileStem, extension, audioSource, destinationRoot, warnings);
            audioLegacy = ClaimLegacyFile(target, audioDestination, extension, destinationRoot);
        }

        if (pdfSource is not null)
        {
            pdfDestination = ClaimDestination(
                targetDirectory, plan.FileStem, ".pdf", pdfSource, destinationRoot, warnings);
            pdfLegacy = ClaimLegacyFile(target, pdfDestination, ".pdf", destinationRoot);
        }

        return new PlannedCopy
        {
            Book = book,
            Label = label,
            TargetDirectory = audioDestination is null && pdfDestination is null ? null : targetDirectory,
            AudioSource = audioDestination is null ? null : audioSource,
            AudioDestination = audioDestination,
            AudioLegacyPath = audioLegacy,
            PdfSource = pdfDestination is null ? null : pdfSource,
            PdfDestination = pdfDestination,
            PdfLegacyPath = pdfLegacy,
            Warning = warnings.Count > 0 ? string.Join("; ", warnings) : null
        };
    }

    /// <summary>Where a book goes, and where older versions left it loose.</summary>
    /// <param name="Directory">The folder the book's files are written to.</param>
    /// <param name="LegacyDirectory">
    /// The folder that used to hold the book's files loose, when the book now gets a folder of its
    /// own; null when the layout for this book has not changed.
    /// </param>
    /// <param name="LegacyStem">The file name, without extension, the book had in that folder.</param>
    private sealed record TargetDirectory(string Directory, string? LegacyDirectory, string? LegacyStem);

    /// <summary>
    /// Every book gets a folder of its own. Audiobookshelf, Plex and friends treat a folder as one
    /// book, and an audio file lying loose in an author or series folder makes them read that whole
    /// folder as a single book, hiding every series underneath it.
    /// </summary>
    private TargetDirectory ResolveTargetDirectory(string destinationRoot, BookSortPlan plan, string sourcePath)
    {
        var parentDirectory = ResolveParentDirectory(destinationRoot, plan);

        // "Book 3" already is a folder per book, and always has been.
        return plan.SeriesSequence is null
            ? ClaimBookDirectory(parentDirectory, plan.FileStem, sourcePath)
            : new TargetDirectory(Path.Combine(parentDirectory, $"Book {plan.SeriesSequence}"), null, null);
    }

    /// <summary>The author folder, or the series folder inside it when the book is in a series.</summary>
    private string ResolveParentDirectory(string destinationRoot, BookSortPlan plan)
    {
        var authorDirectory = Path.Combine(
            destinationRoot,
            ResolveDirectoryName(destinationRoot, plan.Author, PathSanitizer.NormalizeComparisonKey));

        return plan.SeriesName is null
            ? authorDirectory
            : Path.Combine(
                authorDirectory,
                ResolveDirectoryName(authorDirectory, plan.SeriesName, PathSanitizer.NormalizeSeriesKey));
    }

    /// <summary>
    /// Names the folder for a book filed by title. Two different books with the same title must
    /// not share one, or a library tool would merge them into a single book, so the second becomes
    /// "Title (2)" — in list order, which is also the order older versions used when they named the
    /// loose files "Title (2).m4b", so the two line up.
    /// </summary>
    private TargetDirectory ClaimBookDirectory(string parentDirectory, string title, string sourcePath)
    {
        var baseName = ResolveDirectoryName(parentDirectory, title, PathSanitizer.NormalizeComparisonKey);
        var claim = ClaimFirstFree(
            _claimedBookDirectories, sourcePath, suffix => Path.Combine(parentDirectory, baseName + suffix));

        // Out of names: share the folder; ClaimDestination still keeps the files apart.
        return claim is null
            ? new TargetDirectory(Path.Combine(parentDirectory, baseName), null, null)
            : new TargetDirectory(claim.Path, parentDirectory, title + claim.Suffix);
    }

    /// <summary>
    /// Finds the copy an older version left loose in the author or series folder, so the sort can
    /// move it into the book's new folder instead of copying the book a second time and leaving
    /// the loose file behind to keep confusing library tools.
    /// </summary>
    private string? ClaimLegacyFile(TargetDirectory target, string? destination, string extension, string destinationRoot)
    {
        if (destination is null || target.LegacyDirectory is null || target.LegacyStem is null || File.Exists(destination))
        {
            return null;
        }

        var fileIndex = GetDirectoryFileIndex(target.LegacyDirectory);
        if (!fileIndex.TryGetValue(BuildFileIndexKey(target.LegacyStem, extension), out var legacyName))
        {
            return null;
        }

        var legacyPath = Path.Combine(target.LegacyDirectory, legacyName);
        if (!PathSanitizer.IsWithin(destinationRoot, legacyPath) || !_claimedLegacyFiles.Add(legacyPath))
        {
            return null;
        }

        return legacyPath;
    }

    /// <summary>
    /// Picks the folder name to use inside <paramref name="parentDirectory"/>, preferring a folder
    /// that already exists and means the same thing so repeat runs do not fragment a library.
    /// </summary>
    private string ResolveDirectoryName(string parentDirectory, string requestedName, Func<string?, string> normalizer)
    {
        var normalized = normalizer(requestedName);
        if (normalized.Length == 0)
        {
            return requestedName;
        }

        var cacheKey = $"{parentDirectory}\u0000{normalized}";
        if (_resolvedDirectoryNames.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = requestedName;
        foreach (var existingDirectory in SafeEnumerateDirectories(parentDirectory))
        {
            var existingName = Path.GetFileName(existingDirectory);
            if (!string.IsNullOrWhiteSpace(existingName) && normalizer(existingName) == normalized)
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
    /// <param name="Suffix">"" for the plain name, otherwise " (2)", " (3)", ...</param>
    /// <param name="AlreadyOwned">The owner had claimed this path earlier in the run.</param>
    private sealed record Claim(string Path, string Suffix, bool AlreadyOwned);

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
                return new Claim(candidate, suffix, AlreadyOwned: false);
            }

            if (string.Equals(claims[candidate], owner, StringComparison.OrdinalIgnoreCase))
            {
                return new Claim(candidate, suffix, AlreadyOwned: true);
            }
        }

        return null;
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

    private static string BuildLabel(OpenAudible book, BookSortPlan plan)
    {
        var parts = new List<string> { $"Artist: {plan.Author}" };

        if (plan.SeriesName is not null)
        {
            parts.Add($"Series: {plan.SeriesName}");
        }

        if (plan.SeriesSequence is not null)
        {
            parts.Add($"Book: {plan.SeriesSequence}");
        }

        var title = PathSanitizer.SanitizeSegment(book.Title) ?? plan.FileStem;
        parts.Add($"Title: {title}");

        if (!string.IsNullOrWhiteSpace(book.Filename))
        {
            parts.Add($"File: {book.Filename.Trim()}");
        }

        return string.Join(" | ", parts);
    }
}
