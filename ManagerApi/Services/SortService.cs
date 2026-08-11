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

    /// <summary>
    /// Starts a sort if none is running. Returns false when one already is, so the caller can tell
    /// the user the truth instead of reporting a run that never started.
    /// </summary>
    public bool TryStartSort(string csvPath, string sourcePath, string destinationPath, out Task sortTask)
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

        sortTask = RunSort(csvPath, sourcePath, destinationPath);
        return true;
    }

    /// <summary>Starts a sort and waits for it to finish. Used by tests and by direct callers.</summary>
    public Task StartSort(string csvPath, string sourcePath, string destinationPath)
    {
        return TryStartSort(csvPath, sourcePath, destinationPath, out var sortTask)
            ? sortTask
            : Task.CompletedTask;
    }

    private async Task RunSort(string csvPath, string sourcePath, string destinationPath)
    {
        CancellationTokenSource cancellation;
        lock (_sortLock)
        {
            cancellation = _sortCancellation!;
        }

        try
        {
            var books = await EnsureBooksLoaded(csvPath, cancellation.Token);

            var progress = new Progress<SortProgressInfo>(SetProgress);
            var summary = await _fileSorter.SortAudioFiles(sourcePath, destinationPath, books, progress, cancellation.Token);

            SetProgress(new SortProgressInfo
            {
                CurrentBook = summary.TotalBooks,
                TotalBooks = summary.TotalBooks,
                CopiedBooks = summary.CopiedBooks,
                SkippedBooks = summary.SkippedBooks,
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
                SkippedBooks = snapshot.SkippedBooks,
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
                SkippedBooks = snapshot.SkippedBooks,
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
    /// Returns the loaded library, re-reading the CSV when nothing is loaded or when the caller
    /// asked to sort a different file than the one in memory.
    /// </summary>
    private async Task<List<OpenAudible>> EnsureBooksLoaded(string csvPath, CancellationToken cancellationToken)
    {
        string? loadedPath;
        List<OpenAudible> books;
        lock (_sortLock)
        {
            loadedPath = _booksCsvPath;
            books = _books;
        }

        var requestedPath = Path.GetFullPath(csvPath);
        if (books.Count > 0 && string.Equals(loadedPath, requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return books;
        }

        var result = await _csvParser.ParseAsync(csvPath, cancellationToken);

        lock (_sortLock)
        {
            _books = result.Books;
            _booksCsvPath = requestedPath;
        }

        return result.Books;
    }

    private void SetProgress(SortProgressInfo progress)
    {
        lock (_sortLock)
        {
            _currentProgress = progress;
        }
    }
}
