using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

public class SortService
{
    private readonly CsvParser _csvParser = new();
    private readonly FileSorter _fileSorter = new();
    private readonly object _sortLock = new();
    private List<OpenAudible> _books = new();
    private SortProgressInfo _currentProgress = new();
    private CancellationTokenSource? _sortCancellation;
    private volatile bool _isSorting;

    public bool IsSorting => _isSorting;

    public async Task<List<OpenAudible>> ParseBooks(string csvPath)
    {
        _books = await _csvParser.ParseDataCsv(csvPath, CancellationToken.None);
        return _books;
    }

    public List<OpenAudible> GetBooks() => _books;

    public SortProgressInfo GetProgress() => _currentProgress;

    public bool CancelSort()
    {
        lock (_sortLock)
        {
            if (!_isSorting || _sortCancellation is null)
            {
                return false;
            }

            _sortCancellation.Cancel();
            return true;
        }
    }

    public async Task StartSort(string csvPath, string sourcePath, string destinationPath)
    {
        CancellationTokenSource cancellation;

        lock (_sortLock)
        {
            if (_isSorting) return;

            _isSorting = true;
            _currentProgress = new SortProgressInfo();
            _sortCancellation = new CancellationTokenSource();
            cancellation = _sortCancellation;
        }

        try
        {
            if (_books.Count == 0)
            {
                _books = await _csvParser.ParseDataCsv(csvPath, CancellationToken.None);
            }

            var progress = new Progress<SortProgressInfo>(p =>
            {
                _currentProgress = p;
            });

            await _fileSorter.SortAudioFiles(sourcePath, destinationPath, _books, progress, cancellation.Token);

            _currentProgress = new SortProgressInfo
            {
                CurrentBook = _books.Count,
                TotalBooks = _books.Count,
                CopiedBooks = _currentProgress.CopiedBooks,
                Percentage = 100,
                IsComplete = true
            };
        }
        catch (OperationCanceledException)
        {
            _currentProgress = new SortProgressInfo
            {
                CurrentBook = _currentProgress.CurrentBook,
                TotalBooks = _currentProgress.TotalBooks,
                CopiedBooks = _currentProgress.CopiedBooks,
                CurrentTitle = _currentProgress.CurrentTitle,
                Percentage = _currentProgress.Percentage,
                IsComplete = true,
                IsCanceled = true
            };
        }
        catch (Exception ex)
        {
            _currentProgress = new SortProgressInfo
            {
                CurrentBook = _currentProgress.CurrentBook,
                TotalBooks = _currentProgress.TotalBooks,
                CopiedBooks = _currentProgress.CopiedBooks,
                CurrentTitle = _currentProgress.CurrentTitle,
                Percentage = _currentProgress.Percentage,
                Error = ex.Message,
                IsComplete = true
            };
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
}
