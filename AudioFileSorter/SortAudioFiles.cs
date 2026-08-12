using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Copies an OpenAudible library into an Author / Series / Book folder structure.
/// </summary>
public class FileSorter
{
    private const int MaxReportedWarnings = 200;
    private const int CopyBufferSize = 81920;
    private const int ComparisonBufferSize = 131072;
    private const string PartialFileSuffix = ".oabo-partial";

    private static readonly object ConsoleLock = new();

    /// <summary>
    /// Sorts Open Audible books into the provided destination path.
    /// </summary>
    /// <param name="source">Source folder containing audio files.</param>
    /// <param name="destination">Destination folder to sort files into.</param>
    /// <param name="openAudibles">List of audiobook metadata. Never modified.</param>
    /// <param name="options">Per-run settings. Defaults to <see cref="SortOptions.Default"/>.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Token used to abort the run.</param>
    /// <exception cref="ArgumentException">A required path was not supplied.</exception>
    /// <exception cref="DirectoryNotFoundException">The source folder does not exist.</exception>
    /// <exception cref="IOException">The destination folder could not be created.</exception>
    public async Task<SortSummary> SortAudioFiles(
        string? source,
        string? destination,
        List<OpenAudible> openAudibles,
        SortOptions? options = null,
        IProgress<SortProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openAudibles);

        var comparisonMode = (options ?? SortOptions.Default).ComparisonMode;

        // These used to be logged and swallowed, which left the UI waiting for a run that was
        // never going to report anything.
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Source path is required.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("Destination path is required.", nameof(destination));
        }

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source folder not found: {source}");
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new IOException($"Destination folder is not writable: {destination}. {ex.Message}", ex);
        }

        var totalBooks = openAudibles.Count;
        var warnings = new ConcurrentQueue<string>();
        var warningCount = 0;

        if (totalBooks == 0)
        {
            progress?.Report(new SortProgressInfo { Percentage = 100, IsComplete = true });
            WriteLine("No books to sort.");
            return new SortSummary { Warnings = [] };
        }

        var planned = new SortPlanner().Plan(openAudibles, source, Path.GetFullPath(destination));
        foreach (var item in planned)
        {
            if (item.Warning is not null)
            {
                RecordWarning(warnings, ref warningCount, item.Warning);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        WriteLine($"Sorting {totalBooks} books using the {DescribeMode(comparisonMode)} update check.");

        var progressCount = 0;
        var copiedBooks = 0;
        var updatedBooks = 0;
        var skippedBooks = 0;
        var missingBooks = 0;
        var failedBooks = 0;
        var maxLineLength = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = GetMaxParallelism(),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(planned, parallelOptions, async (item, ct) =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await ProcessPlannedCopy(item, comparisonMode, ct);

                switch (outcome)
                {
                    case CopyOutcome.Skipped:
                        Interlocked.Increment(ref skippedBooks);
                        break;
                    case CopyOutcome.NotFound:
                        Interlocked.Increment(ref missingBooks);
                        break;
                    default:
                        Interlocked.Increment(ref copiedBooks);
                        if (outcome == CopyOutcome.Updated)
                        {
                            Interlocked.Increment(ref updatedBooks);
                        }
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedBooks);
                RecordWarning(warnings, ref warningCount, $"Error processing {item.Book.Filename ?? item.Label}: {ex.Message}");
            }

            var currentProgress = Interlocked.Increment(ref progressCount);

            // Read the sub-counts before the total they are part of. A worker increments
            // copiedBooks and only then updatedBooks, so reading them the other way round can
            // pair an old copied count with a newer updated count and publish a snapshot
            // claiming more books were updated than were copied.
            var currentUpdated = Volatile.Read(ref updatedBooks);
            var currentSkipped = Volatile.Read(ref skippedBooks);
            var currentMissing = Volatile.Read(ref missingBooks);
            var currentFailed = Volatile.Read(ref failedBooks);
            var currentCopied = Volatile.Read(ref copiedBooks);

            UpdateProgress(currentProgress, totalBooks, currentCopied, item.Label, ref maxLineLength);

            progress?.Report(new SortProgressInfo
            {
                CurrentBook = currentProgress,
                TotalBooks = totalBooks,
                CopiedBooks = currentCopied,
                UpdatedBooks = currentUpdated,
                SkippedBooks = currentSkipped,
                MissingBooks = currentMissing,
                FailedBooks = currentFailed,
                CurrentTitle = item.Label,
                Percentage = CalculatePercentage(currentProgress, totalBooks),
                WarningCount = Volatile.Read(ref warningCount)
            });
        });

        var summary = new SortSummary
        {
            TotalBooks = totalBooks,
            CopiedBooks = copiedBooks,
            UpdatedBooks = updatedBooks,
            SkippedBooks = skippedBooks,
            MissingBooks = missingBooks,
            FailedBooks = failedBooks,
            Warnings = warnings.ToArray(),
            WarningCount = warningCount
        };

        progress?.Report(new SortProgressInfo
        {
            CurrentBook = totalBooks,
            TotalBooks = totalBooks,
            CopiedBooks = summary.CopiedBooks,
            UpdatedBooks = summary.UpdatedBooks,
            SkippedBooks = summary.SkippedBooks,
            MissingBooks = summary.MissingBooks,
            FailedBooks = summary.FailedBooks,
            Percentage = 100,
            IsComplete = true,
            WarningCount = summary.WarningCount
        });

        WriteLine(
            $"Sorting complete. Copied {summary.CopiedBooks} (of which {summary.UpdatedBooks} updated), " +
            $"skipped {summary.SkippedBooks}, not found {summary.MissingBooks}, " +
            $"failed {summary.FailedBooks} of {totalBooks}.");
        return summary;
    }

    /// <summary>What a single file, or a whole book, needed.</summary>
    private enum CopyOutcome
    {
        /// <summary>Already up to date, so nothing was written.</summary>
        Skipped = 0,

        /// <summary>Written where nothing was before.</summary>
        Created = 1,

        /// <summary>An existing, out-of-date file was replaced.</summary>
        Updated = 2,

        /// <summary>No file to copy: the book is in the export but not in the source folder.</summary>
        NotFound = 3
    }

    private static async Task<CopyOutcome> ProcessPlannedCopy(
        PlannedCopy item,
        FileComparisonMode comparisonMode,
        CancellationToken cancellationToken)
    {
        // Nothing to copy is not the same as nothing to do. A book listed in the export whose file
        // is not in the source folder has to be reported as missing, not as up to date — telling
        // someone their un-downloaded books are already organised is worse than saying nothing.
        if (!item.HasWork)
        {
            return CopyOutcome.NotFound;
        }

        Directory.CreateDirectory(item.TargetDirectory!);

        var audio = await CopyIfNeededAsync(item.AudioSource, item.AudioDestination, comparisonMode, cancellationToken);
        var pdf = await CopyIfNeededAsync(item.PdfSource, item.PdfDestination, comparisonMode, cancellationToken);

        // A book counts as updated when any of its files replaced an existing one; a book whose
        // audio is new but whose PDF was already there is simply a copy.
        if (audio == CopyOutcome.Updated || pdf == CopyOutcome.Updated)
        {
            return CopyOutcome.Updated;
        }

        return audio == CopyOutcome.Created || pdf == CopyOutcome.Created
            ? CopyOutcome.Created
            : CopyOutcome.Skipped;
    }

    private static async Task<CopyOutcome> CopyIfNeededAsync(
        string? sourceFile,
        string? destinationFile,
        FileComparisonMode comparisonMode,
        CancellationToken cancellationToken)
    {
        if (sourceFile is null || destinationFile is null)
        {
            return CopyOutcome.Skipped;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var destinationExists = File.Exists(destinationFile);
        if (destinationExists && await IsDestinationUpToDateAsync(sourceFile, destinationFile, comparisonMode, cancellationToken))
        {
            return CopyOutcome.Skipped;
        }

        await CopyFileAtomicAsync(sourceFile, destinationFile, cancellationToken);
        return destinationExists ? CopyOutcome.Updated : CopyOutcome.Created;
    }

    /// <summary>
    /// Decides whether the file already at the destination still matches its source. Returning
    /// false means "replace it", so every uncertain case has to answer false: a book the user
    /// re-downloaded must not stay stale because a check could not make up its mind.
    /// </summary>
    private static Task<bool> IsDestinationUpToDateAsync(
        string sourceFile,
        string destinationFile,
        FileComparisonMode comparisonMode,
        CancellationToken cancellationToken)
    {
        return comparisonMode == FileComparisonMode.Full
            ? AreFilesIdenticalAsync(sourceFile, destinationFile, cancellationToken)
            : AreFilesSameAsync(sourceFile, destinationFile, cancellationToken);
    }

    /// <summary>
    /// Copies through a temporary file in the destination folder and renames it into place, so an
    /// interrupted run (cancel, crash, full disk) can never leave a half written book behind that
    /// a later run would mistake for a complete one.
    /// </summary>
    private static async Task CopyFileAtomicAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken)
    {
        var partialFile = destinationFile + PartialFileSuffix;

        try
        {
            await using (var sourceStream = new FileStream(
                             sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destinationStream = new FileStream(
                             partialFile, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(destinationStream, CopyBufferSize, cancellationToken);
                await destinationStream.FlushAsync(cancellationToken);
            }

            File.Move(partialFile, destinationFile, overwrite: true);
        }
        catch
        {
            TryDelete(partialFile);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .oabo-partial file is overwritten by the next run.
        }
    }

    /// <summary>
    /// Cheap "is this the same file" check. Comparing every byte of a multi-gigabyte library on
    /// every run is not viable, so size plus three sampled chunks is used instead.
    /// </summary>
    private static async Task<bool> AreFilesSameAsync(string filePath1, string filePath2, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            if (!fileInfo1.Exists || !fileInfo2.Exists || fileInfo1.Length != fileInfo2.Length)
            {
                return false;
            }

            var length = fileInfo1.Length;
            if (length == 0)
            {
                return true;
            }

            const int chunkSize = 4096;
            var buffer1 = new byte[chunkSize];
            var buffer2 = new byte[chunkSize];

            await using var stream1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);
            await using var stream2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);

            foreach (var offset in GetSampleOffsets(length, chunkSize))
            {
                stream1.Seek(offset, SeekOrigin.Begin);
                stream2.Seek(offset, SeekOrigin.Begin);

                var read1 = await stream1.ReadAtLeastAsync(buffer1, chunkSize, throwOnEndOfStream: false, cancellationToken);
                var read2 = await stream2.ReadAtLeastAsync(buffer2, chunkSize, throwOnEndOfStream: false, cancellationToken);

                if (read1 != read2 || !buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // Assume the files differ so the copy is retried.
        }
    }

    /// <summary>
    /// Byte-for-byte comparison of two files. Used by <see cref="FileComparisonMode.Full"/>, where
    /// the point is to notice a re-released book that happens to be exactly the same size as the
    /// copy already on disk — something the sampled check cannot see.
    ///
    /// Internal rather than private so the "cancel stops it promptly" guarantee can be tested
    /// directly: this is the only unbounded loop in a sort, and on a large library it is where a
    /// cancelled run would otherwise keep grinding.
    /// </summary>
    internal static async Task<bool> AreFilesIdenticalAsync(string filePath1, string filePath2, CancellationToken cancellationToken)
    {
        byte[]? buffer1 = null;
        byte[]? buffer2 = null;

        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            if (!fileInfo1.Exists || !fileInfo2.Exists || fileInfo1.Length != fileInfo2.Length)
            {
                return false;
            }

            if (fileInfo1.Length == 0)
            {
                return true;
            }

            buffer1 = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);
            buffer2 = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);

            await using var stream1 = new FileStream(
                filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ComparisonBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var stream2 = new FileStream(
                filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ComparisonBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                // Checked here as well as passed to the reads: a whole-file comparison of a large
                // book is many iterations long, and cancellation must not have to wait for the
                // reads to notice it.
                cancellationToken.ThrowIfCancellationRequested();

                var read1 = await stream1.ReadAtLeastAsync(
                    buffer1.AsMemory(0, ComparisonBufferSize), ComparisonBufferSize, throwOnEndOfStream: false, cancellationToken);
                var read2 = await stream2.ReadAtLeastAsync(
                    buffer2.AsMemory(0, ComparisonBufferSize), ComparisonBufferSize, throwOnEndOfStream: false, cancellationToken);

                if (read1 != read2)
                {
                    // The lengths matched a moment ago, so one of the files is being written to
                    // right now. Treat it as different and copy again on this or the next run.
                    return false;
                }

                if (read1 == 0)
                {
                    return true;
                }

                if (!buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                {
                    return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // Assume the files differ so the copy is retried.
        }
        finally
        {
            if (buffer1 is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer1);
            }

            if (buffer2 is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer2);
            }
        }
    }

    private static IEnumerable<long> GetSampleOffsets(long length, int chunkSize)
    {
        yield return 0;

        if (length <= chunkSize)
        {
            yield break;
        }

        var middle = Math.Max(0, (length / 2) - (chunkSize / 2));
        if (middle > 0)
        {
            yield return middle;
        }

        yield return Math.Max(0, length - chunkSize);
    }

    private static string DescribeMode(FileComparisonMode mode)
    {
        return mode == FileComparisonMode.Full ? "full (byte-for-byte)" : "quick (size and sampled contents)";
    }

    internal static double CalculatePercentage(int current, int total)
    {
        if (total <= 0)
        {
            return 100;
        }

        return Math.Round(Math.Clamp((double)current / total, 0, 1) * 100, 2);
    }

    private static int GetMaxParallelism()
    {
        var configured = Environment.GetEnvironmentVariable("OABO_MAX_PARALLELISM");
        if (int.TryParse(configured, out var requested) && requested > 0)
        {
            return requested;
        }

        // Copying is bound by the slowest of the two volumes, and on a network share or a spinning
        // disk more concurrency makes throughput worse, not better.
        return Math.Clamp(Environment.ProcessorCount / 4, 1, 8);
    }

    private static void RecordWarning(ConcurrentQueue<string> warnings, ref int warningCount, string message)
    {
        var count = Interlocked.Increment(ref warningCount);
        if (count <= MaxReportedWarnings)
        {
            warnings.Enqueue(message);
            WriteLine($"Warning: {message}");
        }
        else if (count == MaxReportedWarnings + 1)
        {
            WriteLine($"Warning: more than {MaxReportedWarnings} warnings, further warnings are suppressed.");
        }
    }

    private static void UpdateProgress(int currentProgress, int totalBooks, int copyBooks, string? title, ref int maxLineLength)
    {
        // When output is redirected (a service log, or the backend piped into the desktop app) the
        // carriage-return trick just produces one enormous line, so log periodically instead.
        if (Console.IsOutputRedirected)
        {
            if (currentProgress == totalBooks || currentProgress % 100 == 0)
            {
                WriteLine($"{CalculatePercentage(currentProgress, totalBooks):0.##}% ({currentProgress}/{totalBooks}) transferred: {copyBooks}");
            }

            return;
        }

        var message = $"{CalculatePercentage(currentProgress, totalBooks):0.##}% ({currentProgress}/{totalBooks}) Transferred: {copyBooks} => {title}";

        lock (ConsoleLock)
        {
            try
            {
                Console.Write("\r" + new string(' ', maxLineLength) + "\r");
                Console.Write(message);
                maxLineLength = Math.Max(maxLineLength, message.Length);
            }
            catch (IOException)
            {
                // The console went away (the desktop app closed the pipe). Never fail a sort for it.
            }
        }
    }

    private static void WriteLine(string message)
    {
        lock (ConsoleLock)
        {
            try
            {
                Console.WriteLine(Console.IsOutputRedirected ? message : $"\n{message}");
            }
            catch (IOException)
            {
                // See UpdateProgress.
            }
        }
    }
}
