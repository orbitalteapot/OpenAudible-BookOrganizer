using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

public class SortService
{
    private readonly CsvParser _csvParser = new();
    private readonly FileSorter _fileSorter = new();
    private List<OpenAudible> _books = new();
    private SortProgressInfo _currentProgress = new();
    private volatile bool _isSorting;

    public bool IsSorting => _isSorting;

    public async Task<List<OpenAudible>> ParseBooks(string csvPath)
    {
        _books = await _csvParser.ParseDataCsv(csvPath, CancellationToken.None);
        return _books;
    }

    public List<OpenAudible> GetBooks() => _books;

    public SortProgressInfo GetProgress() => _currentProgress;

    public async Task StartSort(string csvPath, string sourcePath, string destinationPath)
    {
        if (_isSorting) return;
        _isSorting = true;
        _currentProgress = new SortProgressInfo();

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

            await _fileSorter.SortAudioFiles(sourcePath, destinationPath, _books, progress);

            _currentProgress = new SortProgressInfo
            {
                CurrentBook = _books.Count,
                TotalBooks = _books.Count,
                CopiedBooks = _currentProgress.CopiedBooks,
                Percentage = 100,
                IsComplete = true
            };
        }
        catch (Exception ex)
        {
            _currentProgress = new SortProgressInfo
            {
                Error = ex.Message,
                IsComplete = true
            };
        }
        finally
        {
            _isSorting = false;
        }
    }
}
