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
    /// <exception cref="IOException">
    /// The destination's record of its books cannot be read right now (see <see cref="LibraryManifest.Load"/>).
    /// Nothing was sorted, and the record is left as it is.
    /// </exception>
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

        var attempted = new ConcurrentBag<PlannedCopy>();
        var vacatedFolders = new ConcurrentBag<string>();
        var ownership = new FileOwnership(manifest);
        try
        {
            var planner = new SortPlanner();
            var planned = planner.Plan(books, source, destinationRoot, manifest, cancellationToken);

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
                // Before its files move, whatever then comes of it: a book whose copy fails or is
                // cancelled once its files are in its new folder is in that folder, and the record
                // has to say so, or a later book of its title may be given it.
                if (item.HasWork)
                {
                    attempted.Add(item);
                }

                var (outcome, problem) = await SortBookAsync(item, options.ComparisonMode, ownership, vacatedFolders, ct);
                tally.Finish(item.Title, outcome, problem);
            });

            // After every book's outcome: the problems list keeps only the first few hundred, and a
            // library with hundreds of loose files must not crowd out why a book failed.
            foreach (var warning in planner.Warnings)
            {
                tally.AddProblem(new SortProblem(SortProblemKind.Warning, DestinationProblemSubject, warning));
            }

        }
        finally
        {
            // Only once every copy has finished: a folder one book moved out of can be the folder
            // another book is about to be copied into.
            RemoveEmptiedFolders(vacatedFolders, destinationRoot);

            // Whatever ended the run: the books sorted before a cancel or a failure are where they
            // are, and the next sort has to know they are theirs.
            SaveManifest(manifest, attempted, tally);
        }

        return tally.ToSummary();
    }

    /// <summary>
    /// Records where each book this run worked on is, beside what earlier runs recorded, and writes
    /// the manifest. Not being able to write it costs no book anything, so it is reported rather than
    /// allowed to fail the run: the next sort finds the books in their folders again.
    ///
    /// A book keeps on record every file of its own that is still there beside the ones written this
    /// run: a PDF no longer in the source, or the audio in a format it has since changed from, is still
    /// its own, and must not look like a stray file another book may take. That holds for a file a
    /// book that moved could not take along too (see <see cref="KeepLeftBehind"/>).
    ///
    /// Where the book's files are is what counts, not whether its copy succeeded: one that failed or was
    /// cancelled after its files moved is in its new folder. Only a book none of whose files reached
    /// its folder keeps what was recorded for it.
    /// </summary>
    private static void SaveManifest(LibraryManifest manifest, IEnumerable<PlannedCopy> attempted, RunTally tally)
    {
        foreach (var item in attempted.Where(item => item.BookId is not null))
        {
            var recorded = manifest.Get(item.BookId!);
            var moved = recorded is not null && !string.Equals(recorded.Folder, item.TargetDirectory, StringComparison.OrdinalIgnoreCase);
            var kept = recorded is not null && !moved ? recorded.Files.Select(name => Path.Combine(item.TargetDirectory!, name)) : [];
            var files = new[] { item.AudioDestination, item.PdfDestination }
                .Concat(item.OtherMoves.Where(move => !File.Exists(move.From)).Select(move => move.To))
                .OfType<string>()
                .Concat(kept)
                .Where(File.Exists)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Distinct(StringComparer.FromComparison(PathSanitizer.PathComparison))
                .ToList();

            if (files.Count == 0)
            {
                continue;
            }

            Record(manifest, item.BookId!, new ManifestEntry(item.TargetDirectory!, files, item.Title), tally);
            if (moved)
            {
                KeepLeftBehind(manifest, item, recorded!, tally);
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
    /// Keeps on record, apart from the book, the files a book that moved left in its old folder (its
    /// new folder already had a file of the name, which is never replaced), so that folder is still
    /// given to no other book, and names them in the problems for the person to sort out.
    /// </summary>
    private static void KeepLeftBehind(LibraryManifest manifest, PlannedCopy item, ManifestEntry recorded, RunTally tally)
    {
        var left = recorded.Files.Where(name => File.Exists(Path.Combine(recorded.Folder, name))).ToList();
        if (left.Count == 0)
        {
            return;
        }

        var relativeFolder = Path.GetRelativePath(manifest.Root, recorded.Folder);
        Record(manifest, $"{item.BookId} left in {relativeFolder.Replace(Path.DirectorySeparatorChar, '/')}", recorded with { Files = left }, tally);
        tally.AddProblem(new SortProblem(
            SortProblemKind.Warning,
            item.Title,
            $"Left {string.Join(", ", left.Select(name => $"\"{Path.Combine(relativeFolder, name)}\""))} in the folder the book moved out of, " +
            "as its new folder already has a file of that name. No other book is filed in that folder; if it is an old copy, delete it."));
    }

    /// <summary>Records a book's entry, and says so when it cannot be: then nothing keeps another book out of its folder.</summary>
    private static void Record(LibraryManifest manifest, string bookId, ManifestEntry entry, RunTally tally)
    {
        if (!manifest.Set(bookId, entry))
        {
            tally.AddProblem(new SortProblem(
                SortProblemKind.Warning,
                entry.Title,
                $"Could not record that the book is in \"{Path.GetRelativePath(manifest.Root, entry.Folder)}\", so a later book of " +
                "the same title could be filed in that folder."));
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
        Replaced,

        /// <summary>
        /// A different file is already there that is not provably this book's, so it was left alone
        /// (see <see cref="FileOwnership"/>).
        /// </summary>
        Refused
    }

    /// <summary>
    /// The last line of defence against one book overwriting another. Whatever the planner decided,
    /// a file already in the destination is only replaced, or moved into a book's folder, when it is
    /// provably that book's: the record says so (see <see cref="LibraryManifest"/>), or its content is
    /// the book's own download. A wrong guess about which folder belongs to which book then costs a
    /// book being skipped with a message, never someone's only copy.
    /// </summary>
    private sealed class FileOwnership(LibraryManifest manifest)
    {
        // Read-only during a run: the record is only written once every book is done.
        public bool IsRecordedFor(string? bookId, string path)
        {
            if (bookId is null || manifest.Get(bookId) is not { } entry)
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            return entry.Files.Any(name =>
                string.Equals(Path.GetFullPath(Path.Combine(entry.Folder, name)), fullPath, PathSanitizer.PathComparison));
        }

        /// <summary>
        /// Whether <paramref name="file"/> may be moved into place for the book whose download is
        /// <paramref name="source"/>. A companion such as the book's PDF may also go when it lies beside
        /// audio already proven to be the book's (<paramref name="provenAudioFolder"/>): older versions
        /// filed the two side by side, and a PDF often has no download left to compare with.
        /// </summary>
        public bool MayMove(string? bookId, string file, string? source, string? provenAudioFolder)
        {
            return IsRecordedFor(bookId, file) ||
                   (source is not null && FileComparison.AreSameQuick(source, file)) ||
                   (provenAudioFolder is not null &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), provenAudioFolder, PathSanitizer.PathComparison));
        }
    }

    /// <summary>Sorts one book, turning anything that goes wrong into a problem for the report.</summary>
    private static async Task<(BookOutcome Outcome, SortProblem? Problem)> SortBookAsync(
        PlannedCopy item,
        FileComparisonMode comparisonMode,
        FileOwnership ownership,
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
            return await ProcessPlannedCopy(item, comparisonMode, ownership, vacatedFolders, cancellationToken);
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

    private static async Task<(BookOutcome Outcome, SortProblem? Problem)> ProcessPlannedCopy(
        PlannedCopy item,
        FileComparisonMode comparisonMode,
        FileOwnership ownership,
        ConcurrentBag<string> vacatedFolders,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(item.TargetDirectory!);

        var movedHere = new List<string>();
        var leftAlone = new List<string>();
        string? provenAudioFolder = null;
        var moves = new List<(string? From, string? To, string? Source)>
        {
            (item.AudioMoveFrom, item.AudioDestination, item.AudioSource),
            (item.PdfMoveFrom, item.PdfDestination, item.PdfSource),
        };
        moves.AddRange(item.OtherMoves.Select(move => ((string?)move.From, (string?)move.To, (string?)null)));

        foreach (var (from, to, source) in moves)
        {
            if (from is null || to is null || File.Exists(to) || !File.Exists(from))
            {
                continue;
            }

            // The audio comes first in the list, so its companions can go with it.
            var isAudio = from == item.AudioMoveFrom;
            if (!ownership.MayMove(item.BookId, from, source, isAudio ? null : provenAudioFolder))
            {
                leftAlone.Add(from);
                continue;
            }

            MoveIntoPlace(from, to, vacatedFolders);
            movedHere.Add(to);
            if (isAudio)
            {
                provenAudioFolder = Path.GetDirectoryName(Path.GetFullPath(from));
            }
        }

        // A file moved in above is provably this book's; anything else already there has to be on record as its.
        bool MayReplace(string destination) =>
            ownership.IsRecordedFor(item.BookId, destination) ||
            movedHere.Contains(destination, StringComparer.FromComparison(PathSanitizer.PathComparison));

        var audio = await CopyIfNeededAsync(item.AudioSource, item.AudioDestination, comparisonMode, MayReplace, cancellationToken);
        var pdf = await CopyIfNeededAsync(item.PdfSource, item.PdfDestination, comparisonMode, MayReplace, cancellationToken);

        if (audio == FileOutcome.Refused)
        {
            return (BookOutcome.Failed, new SortProblem(SortProblemKind.Failed, item.Title, RefusedMessage(item.AudioDestination!)));
        }

        var problem = pdf == FileOutcome.Refused
            ? new SortProblem(SortProblemKind.Warning, item.Title, RefusedMessage(item.PdfDestination!))
            : leftAlone.Count > 0
                ? new SortProblem(SortProblemKind.Warning, item.Title, LeftAloneMessage(leftAlone))
                : null;

        // One bucket per book, in the order SortCounts documents: a moved file that then turned out
        // to be stale is Updated, and a book whose audio is new but whose PDF was already there is New.
        if (audio == FileOutcome.Replaced || pdf == FileOutcome.Replaced)
        {
            return (BookOutcome.Updated, problem);
        }

        if (audio == FileOutcome.Created || pdf == FileOutcome.Created)
        {
            return (BookOutcome.New, problem);
        }

        return (movedHere.Count > 0 ? BookOutcome.Moved : BookOutcome.UpToDate, problem);
    }

    private static string RefusedMessage(string destination) =>
        $"A different file is already at {destination}, and it is not on record as this book's, so it was left alone. " +
        "If it is an old copy of this book, delete it and sort again.";

    private static string LeftAloneMessage(IEnumerable<string> files) =>
        $"Left {string.Join(", ", files)} where it was: it is not on record as this book's and differs from the download. " +
        "If it is an old copy, delete it.";

    /// <summary>
    /// Moves a copy of the book already in the destination (see <see cref="PlannedCopy.AudioMoveFrom"/>)
    /// into the book's own folder, once <see cref="FileOwnership"/> has said it is the book's. It is a
    /// rename within the destination that never replaces a file, so nothing is copied or lost, and the
    /// update check that follows still replaces it if the source has changed since. Adds the folder it
    /// came from to <paramref name="vacatedFolders"/>.
    /// </summary>
    private static void MoveIntoPlace(string moveFrom, string destinationFile, ConcurrentBag<string> vacatedFolders)
    {
        File.Move(moveFrom, destinationFile, overwrite: false);
        vacatedFolders.Add(Path.GetDirectoryName(Path.GetFullPath(moveFrom))!);
    }

    private static async Task<FileOutcome> CopyIfNeededAsync(
        string? sourceFile,
        string? destinationFile,
        FileComparisonMode comparisonMode,
        Func<string, bool> mayReplace,
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

        if (destinationExists && !mayReplace(destinationFile))
        {
            return FileOutcome.Refused;
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
