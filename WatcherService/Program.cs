using AudioFileSorter;
using AudioFileSorter.Model;

// Read configuration from environment variables
var csvPath = Environment.GetEnvironmentVariable("CSV_PATH")
    ?? throw new InvalidOperationException("CSV_PATH environment variable is required");
var sourcePath = Environment.GetEnvironmentVariable("SOURCE_PATH")
    ?? throw new InvalidOperationException("SOURCE_PATH environment variable is required");
var destinationPath = Environment.GetEnvironmentVariable("DESTINATION_PATH")
    ?? throw new InvalidOperationException("DESTINATION_PATH environment variable is required");

if (!File.Exists(csvPath))
    throw new FileNotFoundException($"CSV file not found: {csvPath}");
if (!Directory.Exists(sourcePath))
    throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");

Directory.CreateDirectory(destinationPath);

Console.WriteLine("=== OpenAudible Book Organizer - Watch Service ===");
Console.WriteLine($"CSV:         {csvPath}");
Console.WriteLine($"Source:      {sourcePath}");
Console.WriteLine($"Destination: {destinationPath}");
Console.WriteLine();

var csvParser = new CsvParser();
var fileSorter = new FileSorter();
var sortLock = new SemaphoreSlim(1, 1);

// Initial sort on startup to handle any files already present
Console.WriteLine($"[{Timestamp()}] Performing initial sort...");
await RunSort();

// Debounce timer: triggers the sort 3 s after the last file-system event
using var debounceTimer = new System.Timers.Timer(3000) { AutoReset = false };
debounceTimer.Elapsed += async (_, _) => await RunSort();

var csvDirectory = Path.GetDirectoryName(csvPath)
    ?? throw new InvalidOperationException($"Unable to determine directory for CSV path '{csvPath}'.");
var csvFileName = Path.GetFileName(csvPath);

using var csvWatcher = new FileSystemWatcher(csvDirectory)
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
    Filter = csvFileName,
    IncludeSubdirectories = false,
    EnableRaisingEvents = true
};

csvWatcher.Created += OnCsvFileEvent;
csvWatcher.Changed += OnCsvFileEvent;
csvWatcher.Renamed += OnCsvFileEvent;

Console.WriteLine($"[{Timestamp()}] Watching '{csvPath}' for metadata updates. Press Ctrl+C to stop.");

// Block until the user presses Ctrl+C
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

Console.WriteLine($"[{Timestamp()}] Service stopped.");

// ── Helpers ─────────────────────────────────────────────────────────────────

void OnCsvFileEvent(object sender, FileSystemEventArgs e)
{
    QueueSort($"CSV updated: {Path.GetFileName(e.FullPath)}");
}

void QueueSort(string reason)
{
    Console.WriteLine($"[{Timestamp()}] {reason}");
    debounceTimer.Stop();
    debounceTimer.Start();
}

async Task RunSort()
{
    // Guard against concurrent sort runs
    if (!await sortLock.WaitAsync(0))
    {
        Console.WriteLine($"[{Timestamp()}] Sort already in progress, skipping trigger.");
        return;
    }

    try
    {
        Console.WriteLine($"[{Timestamp()}] Parsing CSV metadata...");
        var books = await csvParser.ParseDataCsv(csvPath, CancellationToken.None);
        Console.WriteLine($"[{Timestamp()}] Loaded {books.Count} books. Starting sort...");

        var progress = new Progress<SortProgressInfo>(p =>
        {
            if (p.IsComplete)
                Console.WriteLine($"\n[{Timestamp()}] Sort complete: {p.CopiedBooks}/{p.TotalBooks} files copied.");
        });

        await fileSorter.SortAudioFiles(sourcePath, destinationPath, books, progress);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{Timestamp()}] Sort error: {ex.Message}");
    }
    finally
    {
        sortLock.Release();
    }
}

static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
