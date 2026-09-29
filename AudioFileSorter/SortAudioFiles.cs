using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Copies an OpenAudible library into an Author / Series / Book folder structure, one folder per book.
/// </summary>
public class FileSorter
{
    private const int CopyBufferSize = 81920;
    private const int ComparisonBufferSize = 131072;
    private const string PartialFileSuffix = ".oabo-partial";
    private const string NotFoundMessage = "No audio file for this book in the source folder";

    /// <summary>
    /// Sorts Open Audible books into the provided destination path.
    /// </summary>
    /// <param name="source">Source folder containing audio files.</param>
    /// <param name="destination">Destination folder to sort files into.</param>
    /// <param name="books">List of audiobook metadata. Never modified.</param>
    /// <param name="options">Per-run settings. Defaults to <see cref="SortOptions.Default"/>.</param>
    /// <param name="progress">
    /// Optional progress reporter, called once after planning and once per finished book. Reports
    /// are made in order from the worker threads, so the handler should be quick.
    /// </param>
    /// <param name="cancellationToken">Token used to abort the run.</param>
    /// <exception cref="SortPathException">The paths cannot be used; see <see cref="SortPathValidator"/>.</exception>
    public async Task<SortSummary> SortAudioFiles(
        string source,
        string destination,
        IReadOnlyList<OpenAudible> books,
        SortOptions? options = null,
        IProgress<SortProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        options ??= SortOptions.Default;

        // These used to be logged and swallowed, which left the UI waiting for a run that was
        // never going to report anything.
        var pathProblem = SortPathValidator.Validate(null, source, destination, options.CreateDestination);
        if (pathProblem is not null)
        {
            throw new SortPathException(pathProblem);
        }

        var planned = new SortPlanner().Plan(books, source, Path.GetFullPath(destination), cancellationToken);
        var tally = new RunTally(books.Count, progress);

        foreach (var item in planned.Where(item => item.HasWork && item.Warning is not null))
        {
            tally.AddProblem(new SortProblem(SortProblemKind.Warning, item.Title, item.Warning!));
        }

        cancellationToken.ThrowIfCancellationRequested();
        tally.ReportStart();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxParallelism,
            CancellationToken = cancellationToken
        };

        var vacatedFolders = new ConcurrentBag<string>();
        try
        {
            await Parallel.ForEachAsync(planned, parallelOptions, async (item, ct) =>
            {
                var (outcome, problem) = await SortBookAsync(item, options.ComparisonMode, vacatedFolders, ct);
                tally.Finish(item.Title, outcome, problem);
            });
        }
        finally
        {
            // Only once every copy has finished: a folder one book moved out of can be the folder
            // another book is about to be copied into.
            RemoveEmptiedFolders(vacatedFolders, Path.GetFullPath(destination));
        }

        return tally.ToSummary();
    }

    /// <summary>
    /// Deletes the folders that moving books left empty, such as an old "Book 1" folder or a
    /// standalone book's folder after the book joined a series, and any parent left empty as a
    /// result. A folder holding anything at all, a hidden file included, is left alone, and the
    /// destination folder itself is never removed.
    /// </summary>
    private static void RemoveEmptiedFolders(IEnumerable<string> folders, string destinationRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(destinationRoot);

        // Deepest first, so a parent is only looked at after the folders inside it are gone.
        foreach (var start in folders.Distinct(StringComparer.Ordinal).OrderByDescending(folder => folder.Length))
        {
            for (var folder = start;
                 folder is not null && PathSanitizer.IsWithin(root, folder) &&
                 !string.Equals(Path.TrimEndingDirectorySeparator(folder), root, StringComparison.Ordinal);
                 folder = Path.GetDirectoryName(folder))
            {
                try
                {
                    if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        break;
                    }

                    Directory.Delete(folder, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Tidying up is a courtesy; a folder that cannot go stays, empty and harmless.
                    break;
                }
            }
        }
    }

    /// <summary>Where one book ended up. Each maps to one bucket of <see cref="SortCounts"/>.</summary>
    private enum BookOutcome
    {
        New,
        Updated,
        Moved,
        UpToDate,
        NotFound,
        Failed
    }

    /// <summary>What one file needed.</summary>
    private enum FileOutcome
    {
        /// <summary>Already up to date, so nothing was written.</summary>
        UpToDate,

        /// <summary>Written where nothing was before.</summary>
        Created,

        /// <summary>An existing, out-of-date file was replaced.</summary>
        Replaced
    }

    /// <summary>Sorts one book, turning anything that goes wrong into a problem for the report.</summary>
    private static async Task<(BookOutcome Outcome, SortProblem? Problem)> SortBookAsync(
        PlannedCopy item,
        FileComparisonMode comparisonMode,
        ConcurrentBag<string> vacatedFolders,
        CancellationToken cancellationToken)
    {
        // Nothing to copy is not the same as nothing to do. A book listed in the export whose file
        // is not in the source folder has to be reported as missing, not as up to date — telling
        // someone their un-downloaded books are already organised is worse than saying nothing.
        if (item.IsMissingFromSource)
        {
            return (BookOutcome.NotFound, new SortProblem(SortProblemKind.NotFound, item.Title, NotFoundMessage));
        }

        if (!item.HasWork)
        {
            // No warning means the same file was listed twice, and the first listing copies it.
            return item.Warning is null
                ? (BookOutcome.UpToDate, null)
                : (BookOutcome.Failed, new SortProblem(SortProblemKind.Failed, item.Title, item.Warning));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (await ProcessPlannedCopy(item, comparisonMode, vacatedFolders, cancellationToken), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (BookOutcome.Failed, new SortProblem(SortProblemKind.Failed, item.Title, $"Could not copy the file: {ex.Message}"));
        }
    }

    private static async Task<BookOutcome> ProcessPlannedCopy(
        PlannedCopy item,
        FileComparisonMode comparisonMode,
        ConcurrentBag<string> vacatedFolders,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(item.TargetDirectory!);

        // Both files, whatever happens to the first: '|' does not short-circuit.
        var moved = AdoptLegacyFile(item.AudioLegacyPath, item.AudioDestination, vacatedFolders) |
                    AdoptLegacyFile(item.PdfLegacyPath, item.PdfDestination, vacatedFolders);

        var audio = await CopyIfNeededAsync(item.AudioSource, item.AudioDestination, comparisonMode, cancellationToken);
        var pdf = await CopyIfNeededAsync(item.PdfSource, item.PdfDestination, comparisonMode, cancellationToken);

        // One bucket per book, in the order SortCounts documents: a moved file that then turned out
        // to be stale is Updated, and a book whose audio is new but whose PDF was already there is New.
        if (audio == FileOutcome.Replaced || pdf == FileOutcome.Replaced)
        {
            return BookOutcome.Updated;
        }

        if (audio == FileOutcome.Created || pdf == FileOutcome.Created)
        {
            return BookOutcome.New;
        }

        return moved ? BookOutcome.Moved : BookOutcome.UpToDate;
    }

    /// <summary>
    /// Moves a book that an older version filed elsewhere (see <see cref="PlannedCopy.AudioLegacyPath"/>)
    /// into the book's own folder. It is a rename within the destination, so nothing is copied or
    /// lost, and the update check that follows still replaces it if the source has changed since.
    /// Returns whether a file was moved, and adds the folder it came from to <paramref name="vacatedFolders"/>.
    /// </summary>
    private static bool AdoptLegacyFile(string? legacyPath, string? destinationFile, ConcurrentBag<string> vacatedFolders)
    {
        if (legacyPath is null || destinationFile is null || File.Exists(destinationFile) || !File.Exists(legacyPath))
        {
            return false;
        }

        File.Move(legacyPath, destinationFile, overwrite: false);
        vacatedFolders.Add(Path.GetDirectoryName(Path.GetFullPath(legacyPath))!);
        return true;
    }

    private static async Task<FileOutcome> CopyIfNeededAsync(
        string? sourceFile,
        string? destinationFile,
        FileComparisonMode comparisonMode,
        CancellationToken cancellationToken)
    {
        if (sourceFile is null || destinationFile is null)
        {
            return FileOutcome.UpToDate;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var destinationExists = File.Exists(destinationFile);
        if (destinationExists && await IsDestinationUpToDateAsync(sourceFile, destinationFile, comparisonMode, cancellationToken))
        {
            return FileOutcome.UpToDate;
        }

        await CopyFileAtomicAsync(sourceFile, destinationFile, cancellationToken);
        return destinationExists ? FileOutcome.Replaced : FileOutcome.Created;
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
            : Task.FromResult(AreFilesSame(sourceFile, destinationFile));
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
    /// every run is not viable, so size plus three sampled chunks is used instead. It reads a few
    /// kilobytes, so it is synchronous: the planner uses it too, to tell whose an old file is.
    /// </summary>
    internal static bool AreFilesSame(string filePath1, string filePath2)
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

            using var stream1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize);
            using var stream2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize);

            foreach (var offset in GetSampleOffsets(length, chunkSize))
            {
                stream1.Seek(offset, SeekOrigin.Begin);
                stream2.Seek(offset, SeekOrigin.Begin);

                var read1 = stream1.ReadAtLeast(buffer1, chunkSize, throwOnEndOfStream: false);
                var read2 = stream2.ReadAtLeast(buffer2, chunkSize, throwOnEndOfStream: false);

                if (read1 != read2 || !buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                {
                    return false;
                }
            }

            return true;
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

    /// <summary>
    /// The running totals of one sort. Workers finish books concurrently, so counting, recording
    /// the problem and reporting happen under one lock: each report is a consistent snapshot (the
    /// counts add up to the books finished), and reports arrive in the order the books finished,
    /// so a slow report can never overwrite a newer one.
    /// </summary>
    private sealed class RunTally(int totalBooks, IProgress<SortProgressInfo>? progress)
    {
        private readonly object _lock = new();

        // Immutable, so every snapshot can share it instead of copying up to 500 problems per book.
        private ImmutableList<SortProblem> _problems = [];

        // Kept apart from the capped list above, which stops at the oldest 500: a page following
        // the run wants the latest ones.
        private ImmutableList<SortProblem> _recentProblems = [];
        private int _problemCount;
        private SortCounts _counts = SortCounts.Empty;

        public void AddProblem(SortProblem problem)
        {
            lock (_lock)
            {
                RecordProblem(problem);
            }
        }

        /// <summary>Reports the run as started, so the total and any planning problems show at once.</summary>
        public void ReportStart()
        {
            lock (_lock)
            {
                progress?.Report(Snapshot(currentTitle: null));
            }
        }

        public void Finish(string title, BookOutcome outcome, SortProblem? problem)
        {
            lock (_lock)
            {
                _counts = Add(_counts, outcome);
                if (problem is not null)
                {
                    RecordProblem(problem);
                }

                progress?.Report(Snapshot(title));
            }
        }

        public SortSummary ToSummary()
        {
            lock (_lock)
            {
                return new SortSummary(totalBooks, _counts, _problems, _problemCount);
            }
        }

        private void RecordProblem(SortProblem problem)
        {
            _problemCount++;
            if (_problems.Count < SortSummary.MaxReportedProblems)
            {
                _problems = _problems.Add(problem);
            }

            _recentProblems = _recentProblems.Count < SortProgressInfo.RecentProblemLimit
                ? _recentProblems.Add(problem)
                : _recentProblems.RemoveAt(0).Add(problem);
        }

        private SortProgressInfo Snapshot(string? currentTitle)
        {
            return new SortProgressInfo(
                totalBooks, _counts.Total, currentTitle, _counts, _problems, _recentProblems, _problemCount);
        }

        private static SortCounts Add(SortCounts counts, BookOutcome outcome)
        {
            return outcome switch
            {
                BookOutcome.New => counts with { New = counts.New + 1 },
                BookOutcome.Updated => counts with { Updated = counts.Updated + 1 },
                BookOutcome.Moved => counts with { Moved = counts.Moved + 1 },
                BookOutcome.UpToDate => counts with { UpToDate = counts.UpToDate + 1 },
                BookOutcome.NotFound => counts with { NotFound = counts.NotFound + 1 },
                BookOutcome.Failed => counts with { Failed = counts.Failed + 1 },
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
            };
        }
    }
}
