using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AudioFileSorter.Model;

namespace AudioFileSorter;

public class FileSorter
{
    private static readonly object ConsoleLock = new();
    private static readonly string[] PlaceholderValues = ["unknown", "n/a", "na", "none", "null"];
    private static readonly string[] LeadingArticles = ["the ", "a ", "an "];
    private static readonly string[] SeriesDecorators = [" series", " saga", " cycle"];
    private static readonly string[] ContributorDescriptors = ["foreword", "afterword", "editor", "contributor", "adaptation", "music", "translator", "translatoreditor", "introduction", "preface", "illustrator"];
    private static readonly string[] KnownNonAuthorSegments = ["the great courses", "crystal lake publishing", "crystal lake audio"];
    private static readonly Regex SequenceValueRegex = new(@"(?<value>\d+(?:\.\d+)?(?:-\d+(?:\.\d+)?)?)", RegexOptions.Compiled);

    /// <summary>
    /// Sorts Open Audible books into the provided destination path in parallel.
    /// </summary>
    /// <param name="source">Source folder containing audio files.</param>
    /// <param name="destination">Destination folder to sort files into.</param>
    /// <param name="openAudibles">List of audiobook metadata.</param>
    /// <param name="progress">Optional progress reporter.</param>
    public async Task SortAudioFiles(
        string? source,
        string? destination,
        List<OpenAudible> openAudibles,
        IProgress<SortProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            Console.WriteLine("Error: Source or destination path is missing.");
            return;
        }

        var progressCount = 0;
        var copyBooks = 0;
        var totalBooks = openAudibles.Count;
        var maxLineLength = 0;
        
        var maxParallelism = Math.Max(1, Environment.ProcessorCount / 4); // speed up transfers for high end cpus
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken
        };
        
        
        // Run parallel sorting operations
        await Parallel.ForEachAsync(openAudibles, parallelOptions, async (audioFile, ct) =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var copied = await ProcessAudioFile(audioFile, source, destination, ct);
                if (copied) Interlocked.Increment(ref copyBooks);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError processing {audioFile.Filename}: {ex.Message}");
            }

            var currentProgress = Interlocked.Increment(ref progressCount);
            var progressLabel = BuildProgressLabel(audioFile);
            UpdateProgress(currentProgress, totalBooks, copyBooks, progressLabel, ref maxLineLength);

            progress?.Report(new SortProgressInfo
            {
                CurrentBook = currentProgress,
                TotalBooks = totalBooks,
                CopiedBooks = copyBooks,
                CurrentTitle = progressLabel,
                Percentage = Math.Round((double)currentProgress / totalBooks * 100, 2)
            });
        });

        progress?.Report(new SortProgressInfo
        {
            CurrentBook = totalBooks,
            TotalBooks = totalBooks,
            CopiedBooks = copyBooks,
            Percentage = 100,
            IsComplete = true
        });

        Console.WriteLine("\nSorting complete.");
    }

    private static async Task<bool> ProcessAudioFile(OpenAudible audioFile, string source, string destination, CancellationToken cancellationToken)
    {
        // Sanitize file names to prevent invalid path issues
        audioFile.Author = ResolveAuthorDirectoryName(destination, SanitizeAuthorName(audioFile.Author));
        audioFile.SeriesName = SanitizeOptionalFileName(audioFile.SeriesName);
        audioFile.SeriesSequence = SanitizeSeriesSequence(audioFile.SeriesSequence);
        audioFile.ShortTitle = SanitizeRequiredFileName(audioFile.ShortTitle);
        audioFile.Title = SanitizeRequiredFileName(audioFile.Title);

        if (string.IsNullOrWhiteSpace(audioFile.Author))
        {
            Console.WriteLine($"\nWarning: Missing author for {audioFile.Filename}");
            return false;
        }

        var directory = CreateTargetDirectory(destination, audioFile);
        var copiedAudio = await CopyAudioFileAsync(audioFile, source, directory, cancellationToken);
        var copiedPdf = await CopyPdfCompanionAsync(audioFile, source, directory, cancellationToken);
        return copiedAudio || copiedPdf;
    }

    private static string CreateTargetDirectory(string destination, OpenAudible audioFile)
    {
        var authorPath = Path.Combine(destination, audioFile.Author ?? throw new InvalidOperationException());
        var directory = Directory.CreateDirectory(authorPath).FullName;

        if (!string.IsNullOrWhiteSpace(audioFile.SeriesName))
        {
            var seriesDirectory = ResolveSeriesDirectoryName(directory, audioFile.SeriesName);
            directory = Path.Combine(directory, seriesDirectory);
            Directory.CreateDirectory(directory);

            if (!string.IsNullOrWhiteSpace(audioFile.SeriesSequence))
            {
                directory = Path.Combine(directory, $"Book {audioFile.SeriesSequence}");
                Directory.CreateDirectory(directory);
            }
        }

        return directory;
    }

    private static async Task<bool> CopyAudioFileAsync(OpenAudible audioFile, string source, string targetDirectory, CancellationToken cancellationToken)
    {
        var fileExtension = GetAudioFileExtension(audioFile);
        if (fileExtension == null) return false;

        var sourceFile = Path.Combine(source, $"{audioFile.Filename}{fileExtension}");
        var destinationFile = Path.Combine(targetDirectory, $"{audioFile.ShortTitle}{fileExtension}");

        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"\nWarning: Source file missing: {sourceFile}");
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(destinationFile) || !await AreFilesSameAsync(sourceFile, destinationFile, cancellationToken))
            {
                await CopyFileAsync(sourceFile, destinationFile, cancellationToken);
                // File.Copy(sourceFile, destinationFile, true);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError copying {sourceFile}: {ex.Message}");
        }

        return false;
    }

    private static async Task<bool> CopyPdfCompanionAsync(OpenAudible audioFile, string source, string targetDirectory, CancellationToken cancellationToken)
    {
        var sourcePdf = ResolvePdfSourcePath(audioFile, source);
        if (sourcePdf == null)
        {
            return false;
        }

        var destinationPdf = Path.Combine(targetDirectory, $"{audioFile.ShortTitle}.pdf");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(destinationPdf) || !await AreFilesSameAsync(sourcePdf, destinationPdf, cancellationToken))
            {
                await CopyFileAsync(sourcePdf, destinationPdf, cancellationToken);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError copying {sourcePdf}: {ex.Message}");
        }

        return false;
    }

    private static string? GetAudioFileExtension(OpenAudible audioFile)
    {
        if (!string.IsNullOrWhiteSpace(audioFile.M4B)) return ".m4b";
        if (!string.IsNullOrWhiteSpace(audioFile.MP3)) return ".mp3";
        return null;
    }

    private static string? ResolvePdfSourcePath(OpenAudible audioFile, string sourceRoot)
    {
        foreach (var candidate in GetPdfPathCandidates(audioFile, sourceRoot))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPdfPathCandidates(OpenAudible audioFile, string sourceRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in EnumerateFilePaths(audioFile.FilePaths))
        {
            if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var normalizedPath in ExpandSourcePathCandidates(filePath, sourceRoot))
            {
                if (seen.Add(normalizedPath))
                {
                    yield return normalizedPath;
                }
            }
        }

        foreach (var rawValue in new[] { audioFile.PDF, audioFile.Filename })
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            foreach (var candidate in ExpandPdfValueCandidates(rawValue.Trim(), sourceRoot))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilePaths(string? rawFilePaths)
    {
        if (string.IsNullOrWhiteSpace(rawFilePaths))
        {
            yield break;
        }

        foreach (var entry in rawFilePaths.Split(['|', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = entry.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandPdfValueCandidates(string value, string sourceRoot)
    {
        foreach (var candidate in ExpandSourcePathCandidates(value, sourceRoot))
        {
            yield return candidate;
        }

        if (!string.Equals(Path.GetExtension(value), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in ExpandSourcePathCandidates($"{value}.pdf", sourceRoot))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandSourcePathCandidates(string value, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var trimmed = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        if (Path.IsPathRooted(trimmed))
        {
            yield return trimmed;
            yield break;
        }

        yield return Path.Combine(sourceRoot, trimmed);

        var fileName = Path.GetFileName(trimmed);
        if (!string.Equals(fileName, trimmed, StringComparison.Ordinal))
        {
            yield return Path.Combine(sourceRoot, fileName);
        }
    }

    private static string? SanitizeOptionalFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(ch => !invalidChars.Contains(ch)).ToArray());
        var trimmed = sanitized.TrimEnd('.', ' ').Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        return IsPlaceholderValue(trimmed) ? null : trimmed;
    }

    private static string SanitizeRequiredFileName(string? fileName)
    {
        return SanitizeOptionalFileName(fileName) ?? "Unknown";
    }

    private static string SanitizeAuthorName(string? value)
    {
        var sanitized = SanitizeOptionalFileName(value);
        if (sanitized is null)
        {
            return "Unknown";
        }

        var authorSegments = new List<string>();
        foreach (var rawSegment in sanitized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment.Trim();
            var namePart = segment;
            string? descriptorPart = null;

            var separatorIndex = segment.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                namePart = segment[..separatorIndex].Trim();
                descriptorPart = segment[(separatorIndex + 3)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(descriptorPart) && IsContributorDescriptor(descriptorPart))
            {
                if (authorSegments.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (authorSegments.Count > 0 && IsKnownNonAuthorSegment(namePart))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(namePart))
            {
                authorSegments.Add(namePart);
            }
        }

        if (authorSegments.Count == 0)
        {
            return "Unknown";
        }

        return string.Join(", ", authorSegments.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsPlaceholderValue(string value)
    {
        return PlaceholderValues.Contains(value.Trim().ToLowerInvariant());
    }

    private static bool IsContributorDescriptor(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return ContributorDescriptors.Any(normalized.Contains);
    }

    private static bool IsKnownNonAuthorSegment(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return KnownNonAuthorSegments.Contains(normalized);
    }

    private static string? SanitizeSeriesSequence(string? value)
    {
        var sanitized = SanitizeOptionalFileName(value);
        if (sanitized is null)
        {
            return null;
        }

        if (sanitized.StartsWith("Book ", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized[5..].Trim();
        }

        var match = SequenceValueRegex.Match(sanitized);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["value"].Value;
    }

    private static string ResolveSeriesDirectoryName(string authorDirectory, string requestedSeriesName)
    {
        var normalizedRequested = NormalizeSeriesKey(requestedSeriesName);
        if (string.IsNullOrWhiteSpace(normalizedRequested))
        {
            return requestedSeriesName;
        }

        var existingSeriesDirectories = Directory.GetDirectories(authorDirectory);
        foreach (var existingSeriesDirectory in existingSeriesDirectories)
        {
            var existingName = Path.GetFileName(existingSeriesDirectory);
            if (string.IsNullOrWhiteSpace(existingName))
            {
                continue;
            }

            if (NormalizeSeriesKey(existingName) == normalizedRequested)
            {
                return existingName;
            }
        }

        return requestedSeriesName;
    }

    private static string ResolveAuthorDirectoryName(string destinationRoot, string requestedAuthorName)
    {
        var normalizedRequested = NormalizeAuthorKey(requestedAuthorName);
        if (string.IsNullOrWhiteSpace(normalizedRequested) || !Directory.Exists(destinationRoot))
        {
            return requestedAuthorName;
        }

        foreach (var existingAuthorDirectory in Directory.GetDirectories(destinationRoot))
        {
            var existingName = Path.GetFileName(existingAuthorDirectory);
            if (string.IsNullOrWhiteSpace(existingName))
            {
                continue;
            }

            if (NormalizeAuthorKey(existingName) == normalizedRequested)
            {
                return existingName;
            }
        }

        return requestedAuthorName;
    }

    private static string NormalizeAuthorKey(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return normalized;
    }

    private static string NormalizeSeriesKey(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();

        foreach (var article in LeadingArticles)
        {
            if (normalized.StartsWith(article))
            {
                normalized = normalized[article.Length..];
                break;
            }
        }

        foreach (var decorator in SeriesDecorators)
        {
            if (normalized.EndsWith(decorator))
            {
                normalized = normalized[..^decorator.Length];
                break;
            }
        }

        normalized = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        return normalized;
    }

    private static async Task<bool> AreFilesSameAsync(string filePath1, string filePath2, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            // 🔹 Fastest check: Compare file size first
            if (fileInfo1.Length != fileInfo2.Length) return false;

            const int chunkSize = 4096; // 4KB buffer for speed
            var buffer1 = new byte[chunkSize];
            var buffer2 = new byte[chunkSize];

            // 🔹 Open files in `FileShare.ReadWrite` mode to prevent locking issues
            await using var stream1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);
            await using var stream2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);

            // 🔹 Compare first chunk
            var bytesRead1 = await stream1.ReadAsync(buffer1.AsMemory(0, chunkSize), cancellationToken);
            var bytesRead2 = await stream2.ReadAsync(buffer2.AsMemory(0, chunkSize), cancellationToken);
            if (bytesRead1 != bytesRead2 || !buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)))
                return false;

            // 🔹 Compare last chunk if file is larger than chunk size
            if (fileInfo1.Length > chunkSize)
            {
                stream1.Seek(-chunkSize, SeekOrigin.End);
                stream2.Seek(-chunkSize, SeekOrigin.End);

                bytesRead1 = await stream1.ReadAsync(buffer1.AsMemory(0, chunkSize), cancellationToken);
                bytesRead2 = await stream2.ReadAsync(buffer2.AsMemory(0, chunkSize), cancellationToken);
                if (bytesRead1 != bytesRead2 || !buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)))
                    return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false; // Assume files are different if any error occurs
        }
    }


    private static void UpdateProgress(int currentProgress, int totalBooks, int copyBooks, string? title, ref int maxLineLength)
    {
        var message = $"{Math.Round((decimal)currentProgress / totalBooks * 100, 2)}% ({currentProgress}/{totalBooks}) Transferred: {copyBooks} => {title}";

        lock (ConsoleLock)
        {
            // Clear previous message with spaces to prevent text corruption
            Console.Write("\r" + new string(' ', maxLineLength) + "\r");
            Console.Write(message);

            // Track the longest message to properly clear on next update
            maxLineLength = Math.Max(maxLineLength, message.Length);
        }
    }

    private static string BuildProgressLabel(OpenAudible audioFile)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(audioFile.Author))
        {
            parts.Add($"Artist: {audioFile.Author}");
        }

        if (!string.IsNullOrWhiteSpace(audioFile.SeriesName))
        {
            parts.Add($"Series: {audioFile.SeriesName}");
        }

        if (!string.IsNullOrWhiteSpace(audioFile.SeriesSequence))
        {
            parts.Add($"Book: {audioFile.SeriesSequence}");
        }

        if (!string.IsNullOrWhiteSpace(audioFile.Title))
        {
            parts.Add($"Title: {audioFile.Title}");
        }

        var fileName = audioFile.Filename;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            parts.Add($"File: {fileName}");
        }

        return parts.Count > 0
            ? string.Join(" | ", parts)
            : audioFile.Title ?? "Unknown";
    }

    private static async Task CopyFileAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken)
    {
        const int bufferSize = 81920; // 80KB buffer for efficiency

        await using var sourceStream = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destinationStream = new FileStream(
            destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

}
