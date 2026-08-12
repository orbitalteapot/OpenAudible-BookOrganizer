using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Owns the single sort run the backend allows at a time, plus the library it works from.
/// </summary>
public class SortService
{
    private readonly CsvParser _csvParser = new();
    private readonly FileSorter _fileSorter = new();
    private readonly object _sortLock = new();

    private List<OpenAudible> _books = [];
    private string? _booksCsvPath;
    private (DateTime LastWriteUtc, long Length)? _booksCsvStamp;
    private SortProgressInfo _currentProgress = new();
    private CancellationTokenSource? _sortCancellation;
    private bool _isSorting;

    public bool IsSorting
    {
        get
        {
            lock (_sortLock)
            {
                return _isSorting;
            }
        }
    }

    /// <summary>Reads a book list into memory, replacing whatever was loaded before.</summary>
    /// <exception cref="InvalidOperationException">A sort is currently running.</exception>
    public async Task<CsvParseResult> ParseBooks(string csvPath, CancellationToken cancellationToken = default)
    {
        lock (_sortLock)
        {
            if (_isSorting)
            {
                throw new InvalidOperationException("A sort is currently running. Cancel it before loading a different library.");
            }
        }

        var stamp = ReadFileStamp(csvPath);
        var result = await _csvParser.ParseAsync(csvPath, cancellationToken);

        lock (_sortLock)
        {
            // Re-check: a sort may have started while the file was being read. Losing the parse
            // result is better than swapping the list out from under a running sort.
            if (_isSorting)
            {
                throw new InvalidOperationException("A sort started while the library was loading. Try again once it finishes.");
            }

            _books = result.Books;
            _booksCsvPath = Path.GetFullPath(csvPath);
            _booksCsvStamp = stamp;
        }

        return result;
    }

    public List<OpenAudible> GetBooks()
    {
        lock (_sortLock)
        {
            return _books;
        }
    }

    public SortProgressInfo GetProgress()
    {
        lock (_sortLock)
        {
            return _currentProgress;
        }
    }

    public bool CancelSort()
    {
        lock (_sortLock)
        {
            if (!_isSorting || _sortCancellation is null)
            {
                return false;
            }

            try
            {
                _sortCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>Starts a sort with the default settings.</summary>
    public bool TryStartSort(string csvPath, string sourcePath, string destinationPath, out Task sortTask)
    {
        return TryStartSort(csvPath, sourcePath, destinationPath, null, out sortTask);
    }

    /// <summary>
    /// Starts a sort if none is running. Returns false when one already is, so the caller can tell
    /// the user the truth instead of reporting a run that never started.
    /// </summary>
    public bool TryStartSort(string csvPath, string sourcePath, string destinationPath, SortOptions? options, out Task sortTask)
    {
        lock (_sortLock)
        {
            if (_isSorting)
            {
                sortTask = Task.CompletedTask;
                return false;
            }

            _isSorting = true;
            _currentProgress = new SortProgressInfo();
            _sortCancellation = new CancellationTokenSource();
        }

        sortTask = RunSort(csvPath, sourcePath, destinationPath, options ?? SortOptions.Default);
        return true;
    }

    /// <summary>Starts a sort and waits for it to finish. Used by tests and by direct callers.</summary>
    public Task StartSort(string csvPath, string sourcePath, string destinationPath, SortOptions? options = null)
    {
        return TryStartSort(csvPath, sourcePath, destinationPath, options, out var sortTask)
            ? sortTask
            : Task.CompletedTask;
    }

    private async Task RunSort(string csvPath, string sourcePath, string destinationPath, SortOptions options)
    {
        CancellationTokenSource cancellation;
        lock (_sortLock)
        {
            cancellation = _sortCancellation!;
        }

        try
        {
            var books = await EnsureBooksLoaded(csvPath, cancellation.Token);

            // Deliberately not Progress<T>: it marshals each report through the thread pool, so a
            // per-book report could be delivered after the final one and leave the run looking
            // unfinished forever.
            var progress = new InlineProgress<SortProgressInfo>(SetProgress);
            var summary = await _fileSorter.SortAudioFiles(sourcePath, destinationPath, books, options, progress, cancellation.Token);

            SetProgress(new SortProgressInfo
            {
                CurrentBook = summary.TotalBooks,
                TotalBooks = summary.TotalBooks,
                CopiedBooks = summary.CopiedBooks,
                UpdatedBooks = summary.UpdatedBooks,
                SkippedBooks = summary.SkippedBooks,
                MissingBooks = summary.MissingBooks,
                FailedBooks = summary.FailedBooks,
                WarningCount = summary.WarningCount,
                Percentage = 100,
                IsComplete = true
            });
        }
        catch (OperationCanceledException)
        {
            var snapshot = GetProgress();
            SetProgress(new SortProgressInfo
            {
                CurrentBook = snapshot.CurrentBook,
                TotalBooks = snapshot.TotalBooks,
                CopiedBooks = snapshot.CopiedBooks,
                UpdatedBooks = snapshot.UpdatedBooks,
                SkippedBooks = snapshot.SkippedBooks,
                MissingBooks = snapshot.MissingBooks,
                FailedBooks = snapshot.FailedBooks,
                WarningCount = snapshot.WarningCount,
                CurrentTitle = snapshot.CurrentTitle,
                Percentage = snapshot.Percentage,
                IsComplete = true,
                IsCanceled = true
            });
        }
        catch (Exception ex)
        {
            var snapshot = GetProgress();
            SetProgress(new SortProgressInfo
            {
                CurrentBook = snapshot.CurrentBook,
                TotalBooks = snapshot.TotalBooks,
                CopiedBooks = snapshot.CopiedBooks,
                UpdatedBooks = snapshot.UpdatedBooks,
                SkippedBooks = snapshot.SkippedBooks,
                MissingBooks = snapshot.MissingBooks,
                FailedBooks = snapshot.FailedBooks,
                WarningCount = snapshot.WarningCount,
                CurrentTitle = snapshot.CurrentTitle,
                Percentage = snapshot.Percentage,
                Error = ex.Message,
                IsComplete = true
            });

            Console.Error.WriteLine($"Sort failed: {ex}");
        }
        finally
        {
            lock (_sortLock)
            {
                _sortCancellation?.Dispose();
                _sortCancellation = null;
                _isSorting = false;
            }
        }
    }

    /// <summary>
    /// Returns the loaded library, re-reading the CSV when nothing is loaded, when the caller asked
    /// to sort a different file than the one in memory, or when that file has changed on disk since
    /// it was read.
    /// </summary>
    private async Task<List<OpenAudible>> EnsureBooksLoaded(string csvPath, CancellationToken cancellationToken)
    {
        string? loadedPath;
        (DateTime, long)? loadedStamp;
        List<OpenAudible> books;
        lock (_sortLock)
        {
            loadedPath = _booksCsvPath;
            loadedStamp = _booksCsvStamp;
            books = _books;
        }

        var requestedPath = Path.GetFullPath(csvPath);
        var currentStamp = ReadFileStamp(csvPath);

        // OpenAudible exports over the top of the same file every time, and a container's CSV_PATH
        // never changes at all. Matching on the path alone meant that re-exporting your library and
        // pressing Start sorting quietly sorted whatever was read the first time — in a long-lived
        // container, potentially days earlier.
        var sameFile = books.Count > 0 && string.Equals(loadedPath, requestedPath, StringComparison.OrdinalIgnoreCase);
        var unchanged = loadedStamp is not null && currentStamp is not null && loadedStamp == currentStamp;

        if (sameFile && unchanged)
        {
            return books;
        }

        var result = await _csvParser.ParseAsync(csvPath, cancellationToken);

        lock (_sortLock)
        {
            _books = result.Books;
            _booksCsvPath = requestedPath;
            _booksCsvStamp = currentStamp;
        }

        return result.Books;
    }

    /// <summary>
    /// How the file looked when it was read. Null when it cannot be inspected, which counts as
    /// "changed" — re-reading costs milliseconds, and sorting a stale library costs the user a
    /// wrong answer they have no way of noticing.
    /// </summary>
    private static (DateTime LastWriteUtc, long Length)? ReadFileStamp(string csvPath)
    {
        try
        {
            var info = new FileInfo(csvPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Publishes a progress snapshot. Internal rather than private so the "a finished run stays
    /// finished" guarantee can be tested without racing the thread pool.
    /// </summary>
    internal void SetProgress(SortProgressInfo progress)
    {
        lock (_sortLock)
        {
            // Once a run is finished, nothing from that run may un-finish it. The UI stops
            // polling on the completed flag, so losing it would leave it spinning forever.
            if (_currentProgress.IsComplete && !progress.IsComplete)
            {
                return;
            }

            _currentProgress = progress;
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes the handler on the reporting thread instead of
    /// queueing it, so reports are applied in the order they were made.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
