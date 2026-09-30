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
    private const string PartialFileSuffix = ".oabo-partial";
    private const string NotFoundMessage = "No audio file for this book in the source folder";

    /// <summary>What problems about the destination as a whole, rather than one book, are listed under.</summary>
    private const string DestinationProblemSubject = "Destination folder";

    /// <summary>
    /// Sorts Open Audible books into the provided destination path, going by and then updating the
    /// record of which book is in which folder that sorts keep there (see <see cref="LibraryManifest"/>).
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

        var destinationRoot = Path.GetFullPath(destination);
        var manifest = LibraryManifest.Load(destinationRoot);
        var tally = new RunTally(books.Count, progress);
        if (manifest.Problem is not null)
        {
            tally.AddProblem(new SortProblem(SortProblemKind.Warning, DestinationProblemSubject, manifest.Problem));
        }

        var sorted = new ConcurrentBag<PlannedCopy>();
        var vacatedFolders = new ConcurrentBag<string>();
        try
        {
            var planned = new SortPlanner().Plan(books, source, destinationRoot, manifest, cancellationToken);

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

            await Parallel.ForEachAsync(planned, parallelOptions, async (item, ct) =>
            {
                var (outcome, problem) = await SortBookAsync(item, options.ComparisonMode, vacatedFolders, ct);
                if (item.HasWork && outcome is not (BookOutcome.Failed or BookOutcome.NotFound))
                {
                    sorted.Add(item);
                }

                tally.Finish(item.Title, outcome, problem);
            });
        }
        finally
        {
            // Only once every copy has finished: a folder one book moved out of can be the folder
            // another book is about to be copied into.
            RemoveEmptiedFolders(vacatedFolders, destinationRoot);

            // Whatever ended the run: the books sorted before a cancel or a failure are where they
            // are, and the next sort has to know they are theirs.
            SaveManifest(manifest, sorted, tally);
        }

        return tally.ToSummary();
    }

    /// <summary>
    /// Records where each book sorted in this run is, beside what earlier runs recorded, and writes
    /// the manifest. Not being able to write it costs no book anything, so it is reported rather than
    /// allowed to fail the run: the next sort finds the books in their folders again.
    /// </summary>
    private static void SaveManifest(LibraryManifest manifest, IEnumerable<PlannedCopy> sorted, RunTally tally)
    {
        foreach (var item in sorted.Where(item => item.BookId is not null))
        {
            var files = new[] { item.AudioDestination, item.PdfDestination }
                .OfType<string>()
                .Where(File.Exists)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList();

            if (files.Count > 0)
            {
                manifest.Set(item.BookId!, new ManifestEntry(item.TargetDirectory!, files, item.Title));
            }
        }

        try
        {
            manifest.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tally.AddProblem(new SortProblem(
                SortProblemKind.Warning,
                DestinationProblemSubject,
                $"Could not save its record of which book is in which folder (the {LibraryManifest.FileName} file): {ex.Message} " +
                "The next sort finds the books in their folders again."));
        }
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
            // The warning says why, when there is a file but it cannot be the book yet.
            return (BookOutcome.NotFound, new SortProblem(SortProblemKind.NotFound, item.Title, item.Warning ?? NotFoundMessage));
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
        var moved = MoveIntoPlace(item.AudioMoveFrom, item.AudioDestination, vacatedFolders) |
                    MoveIntoPlace(item.PdfMoveFrom, item.PdfDestination, vacatedFolders);

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
    /// Moves a copy of the book already in the destination (see <see cref="PlannedCopy.AudioMoveFrom"/>)
    /// into the book's own folder. It is a rename within the destination that never replaces a file,
    /// so nothing is copied or lost, and the update check that follows still replaces it if the source
    /// has changed since. Returns whether a file was moved, and adds the folder it came from to
    /// <paramref name="vacatedFolders"/>.
    /// </summary>
    private static bool MoveIntoPlace(string? moveFrom, string? destinationFile, ConcurrentBag<string> vacatedFolders)
    {
        if (moveFrom is null || destinationFile is null || File.Exists(destinationFile) || !File.Exists(moveFrom))
        {
            return false;
        }

        File.Move(moveFrom, destinationFile, overwrite: false);
        vacatedFolders.Add(Path.GetDirectoryName(Path.GetFullPath(moveFrom))!);
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
            ? FileComparison.AreIdenticalAsync(sourceFile, destinationFile, cancellationToken)
            : Task.FromResult(FileComparison.AreSameQuick(sourceFile, destinationFile));
    }

    /// <summary>
    /// Copies through a temporary file in the destination folder and renames it into place, so an
    /// interrupted run (cancel, crash, full disk) can never leave a half written book behind that
    /// a later run would mistake for a complete one.
    ///
    /// The source is shared for writing, because OpenAudible may be working on it. A copy taken
    /// while it changed is only part of a book, so it never replaces anything: the book is copied
    /// again on the next sort, once the file has settled.
    /// </summary>
    private static async Task CopyFileAtomicAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken)
    {
        var partialFile = destinationFile + PartialFileSuffix;

        try
        {
            var before = FileStamp(sourceFile);
            long copied;
            await using (var sourceStream = new FileStream(
                             sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destinationStream = new FileStream(
                             partialFile, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(destinationStream, CopyBufferSize, cancellationToken);
                await destinationStream.FlushAsync(cancellationToken);

                // On the disk before the rename replaces the old copy: without it, a power cut or an
                // unplugged drive can leave an empty or truncated book where the good copy was on
                // file systems that do not order the two (exFAT, NTFS-3g, XFS, many network shares).
                destinationStream.Flush(flushToDisk: true);
                copied = destinationStream.Length;
            }

            if (copied != before.Length || FileStamp(sourceFile) != before)
            {
                throw new IOException(
                    "The file in the source folder changed while it was being copied, as it does while OpenAudible " +
                    "is still writing it. It is copied again on the next sort.");
            }

            File.Move(partialFile, destinationFile, overwrite: true);
        }
        catch
        {
            TryDelete(partialFile);
            throw;
        }
    }

    /// <summary>A file's size and when it was last written, which change whenever anything writes to it.</summary>
    private static (long Length, DateTime LastWriteUtc) FileStamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
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
