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

    private readonly Dictionary<string, string> _resolvedDirectoryNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _directoryFileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedDestinations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the copy plan for every book in <paramref name="books"/>.</summary>
    public List<PlannedCopy> Plan(IReadOnlyList<OpenAudible> books, string sourceRoot, string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var planned = new List<PlannedCopy>(books.Count);

        foreach (var book in books)
        {
            planned.Add(PlanBook(book, sourceRoot, fullDestinationRoot));
        }

        return planned;
    }

    private PlannedCopy PlanBook(OpenAudible book, string sourceRoot, string destinationRoot)
    {
        var plan = BookNaming.BuildPlan(book);
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
        var targetDirectory = ResolveTargetDirectory(destinationRoot, plan);
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
            Label = label,
            TargetDirectory = audioDestination is null && pdfDestination is null ? null : targetDirectory,
            AudioSource = audioDestination is null ? null : audioSource,
            AudioDestination = audioDestination,
            PdfSource = pdfDestination is null ? null : pdfSource,
            PdfDestination = pdfDestination,
            Warning = warnings.Count > 0 ? string.Join("; ", warnings) : null
        };
    }

    private string ResolveTargetDirectory(string destinationRoot, BookSortPlan plan)
    {
        var authorDirectory = Path.Combine(
            destinationRoot,
            ResolveDirectoryName(destinationRoot, plan.Author, PathSanitizer.NormalizeComparisonKey));

        if (plan.SeriesName is null)
        {
            return authorDirectory;
        }

        var seriesDirectory = Path.Combine(
            authorDirectory,
            ResolveDirectoryName(authorDirectory, plan.SeriesName, PathSanitizer.NormalizeSeriesKey));

        return plan.SeriesSequence is null
            ? seriesDirectory
            : Path.Combine(seriesDirectory, $"Book {plan.SeriesSequence}");
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

        for (var attempt = 1; attempt <= MaxDisambiguationAttempts; attempt++)
        {
            var candidateName = attempt == 1 ? fileName : $"{stem} ({attempt}){extension}";
            var candidatePath = Path.Combine(directory, candidateName);

            if (!PathSanitizer.IsWithin(destinationRoot, candidatePath))
            {
                warnings.Add($"Refusing to write \"{candidateName}\" outside the destination folder");
                return null;
            }

            if (!_claimedDestinations.TryGetValue(candidatePath, out var claimedBy))
            {
                _claimedDestinations[candidatePath] = sourcePath;
                fileIndex.TryAdd(BuildFileIndexKey(Path.GetFileNameWithoutExtension(candidateName), extension), candidateName);
                return candidatePath;
            }

            if (string.Equals(claimedBy, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                // The same physical file listed twice in the export: copy it once.
                return null;
            }
        }

        warnings.Add($"Could not find a free file name for \"{stem}{extension}\"");
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
