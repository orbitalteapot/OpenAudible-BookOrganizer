using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Owns the single sort run the backend allows at a time, plus the library it works from. It is
/// the only judge of whether a sort is running: the page and the schedule both start runs here, and
/// both read the same <see cref="RunStatus"/>.
///
/// Hosted only so the app waits for the run it is closing (see <see cref="StopAsync"/>).
/// </summary>
public sealed class SortService : IHostedService
{
    private const string AppClosedMessage = "Canceled because the app closed.";

    private readonly CsvParser _csvParser = new();
    private readonly FileSorter _fileSorter = new();
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly CancellationToken _appStopping;
    private readonly ILogger<SortService> _logger;
    private readonly object _lock = new();

    private List<OpenAudible> _books = [];
    private string? _booksCsvPath;
    private (DateTime LastWriteUtc, long Length)? _booksCsvStamp;
    private RunStatus _status = RunStatus.Idle;
    private ActiveRun? _active;

    public SortService(SettingsService settings, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<SortService> logger)
    {
        _settings = settings;
        _time = time;
        _appStopping = lifetime.ApplicationStopping;
        _logger = logger;
    }

    public bool IsSorting
    {
        get
        {
            lock (_lock)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>
    /// Reads the library export from the settings into memory, replacing whatever was loaded before.
    /// Allowed while a sort runs: the run keeps the list it started with.
    /// </summary>
    /// <exception cref="SortPathException">No export is set, or it does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not an OpenAudible export.</exception>
    public async Task<CsvParseResult> ParseBooks(CancellationToken cancellationToken = default)
    {
        var csvPath = _settings.Effective.CsvPath;
        if (SortPathValidator.ValidateCsv(csvPath) is { } problem)
        {
            throw new SortPathException(_settings.Config.Explain(problem));
        }

        return await LoadBooks(csvPath!, cancellationToken);
    }

    public List<OpenAudible> GetBooks()
    {
        lock (_lock)
        {
            return _books;
        }
    }

    /// <summary>The current or most recent run, trimmed for polling.</summary>
    public RunStatus GetStatus()
    {
        lock (_lock)
        {
            return _status.ForPolling();
        }
    }

    /// <summary>
    /// Starts a sort with the paths in the settings, unless one is already running.
    /// </summary>
    /// <param name="run">
    /// Completes with this run's own final status — never the shared one, which a later run may
    /// already have replaced. When a run was already going, it is that run's.
    /// </param>
    /// <returns>False when a sort is already running.</returns>
    /// <exception cref="SortPathException">The paths cannot be used; nothing was started.</exception>
    public bool TryStartSort(RunTrigger trigger, SortOptions options, out Task<RunStatus> run)
    {
        // Checked before validating as well as after: a second Start while a run is going should
        // hear "already running", not a complaint about paths it never got to use.
        if (TryGetActiveRun(out run))
        {
            return false;
        }

        // Outside the lock: on a sleeping network share this can take a while, and polling for
        // progress must not wait on it.
        var settings = _settings.Effective;
        var problem = _settings.CheckForSort(settings, options.CreateDestination) ??
                      (trigger == RunTrigger.Scheduled ? _settings.CheckUnattended(settings) : null);
        if (problem is not null)
        {
            throw new SortPathException(problem);
        }

        lock (_lock)
        {
            if (_active is not null)
            {
                run = _active.Completion;
                return false;
            }

            var active = new ActiveRun(CancellationTokenSource.CreateLinkedTokenSource(_appStopping));
            _active = active;
            _status = RunStatus.Starting(trigger, UtcNow());

            // Off the caller's thread: planning a large library is synchronous work, and the caller
            // is a web request or the scheduler, neither of which should wait for it.
            active.Completion = Task.Run(() => RunSort(active, settings.CsvPath!, settings.SourcePath!, settings.DestinationPath!, options));
            run = active.Completion;
            return true;
        }
    }

    /// <param name="appClosing">
    /// The desktop app is quitting, which is not a person changing their mind: the run ends as one
    /// the app's closing cut short, so automatic sorting tries it again instead of counting it as done.
    /// </param>
    public bool CancelSort(bool appClosing = false)
    {
        lock (_lock)
        {
            if (_active is null)
            {
                return false;
            }

            // Under the lock: the run disposes its token source once it is no longer the active one.
            _active.AppClosing |= appClosing;
            _active.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Nothing to start: runs are started on request.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Waits, within the host's shutdown timeout, for the run the closing app has cancelled to wind
    /// down. The scheduler waits for the runs it starts, but nothing waited for one started from the
    /// page, so the process could exit before that run recorded and logged why it stopped, or deleted
    /// its partly copied file.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? run;
        lock (_lock)
        {
            run = _active?.Completion;
        }

        if (run is not null)
        {
            await run.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private bool TryGetActiveRun(out Task<RunStatus> run)
    {
        lock (_lock)
        {
            run = _active?.Completion ?? Task.FromResult(_status);
            return _active is not null;
        }
    }

    private async Task<RunStatus> RunSort(ActiveRun run, string csvPath, string sourcePath, string destinationPath, SortOptions options)
    {
        var token = run.Cancellation.Token;
        RunStatus final;
        try
        {
            var books = await EnsureBooksLoaded(csvPath, token);

            // Deliberately not Progress<T>: it marshals each report through the thread pool, so a
            // per-book report could be delivered after the final one.
            var progress = new InlineProgress<SortProgressInfo>(info => Update(run, status => status.With(info)));
            var summary = await _fileSorter.SortAudioFiles(sourcePath, destinationPath, books, options, progress, token);

            MarkDestination(destinationPath);
            final = Finish(run, status => status.Completed(summary, UtcNow()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // An app that is closing is not a person changing their mind: saying so lets the
            // schedule try again, and tells the user why the run stopped.
            var reason = _appStopping.IsCancellationRequested || IsClosingApp(run) ? AppClosedMessage : null;
            final = Finish(run, status => status.Canceled(reason, UtcNow()));
        }
        catch (SortPathException ex)
        {
            // The paths passed when the run started, and something (a drive unplugged) changed since.
            var problem = _settings.Config.Explain(ex.Problem);
            final = Finish(run, status => status.Failed(problem.Message, RunErrors.Code(problem), RunErrors.Field(problem.Field), UtcNow()));
        }
        catch (FileNotFoundException ex)
        {
            final = Finish(run, status => status.Failed(ex.Message, RunErrors.CsvNotFound, RunErrors.CsvPathField, UtcNow()));
        }
        catch (InvalidDataException ex)
        {
            final = Finish(run, status => status.Failed(ex.Message, RunErrors.CsvInvalid, RunErrors.CsvPathField, UtcNow()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sort failed");
            final = Finish(run, status => status.Failed(ex.Message, null, null, UtcNow()));
        }

        _logger.LogInformation("{Summary}", RunSummary.Describe(final));
        return final;
    }

    /// <summary>
    /// Leaves the marker automatic sorts look for (see <see cref="SettingsService.CheckUnattended"/>).
    /// A run that finished has just proved this folder is the library, whoever started it, so a
    /// person who emptied it on purpose re-arms automatic sorting by sorting once by hand.
    /// </summary>
    private void MarkDestination(string destinationPath)
    {
        try
        {
            SortPathValidator.MarkDestination(destinationPath);
            _settings.RecordMarkedDestination(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unmarked, the folder is sorted into as before; only the check for a stand-in is lost.
            _logger.LogWarning(ex, "Could not leave the marker in the destination {Path}", destinationPath);
        }
    }

    private bool IsClosingApp(ActiveRun run)
    {
        lock (_lock)
        {
            return run.AppClosing;
        }
    }

    /// <summary>
    /// Applies a progress report, if it belongs to the run in progress. A report that arrives after
    /// its run finished is dropped: a finished run must stay finished, since the page stops polling
    /// quickly on it.
    /// </summary>
    private void Update(ActiveRun run, Func<RunStatus, RunStatus> update)
    {
        lock (_lock)
        {
            if (_active == run)
            {
                _status = update(_status);
            }
        }
    }

    /// <summary>Publishes a run's final status and frees the slot for the next run.</summary>
    private RunStatus Finish(ActiveRun run, Func<RunStatus, RunStatus> finish)
    {
        lock (_lock)
        {
            _status = finish(_status);
            _active = null;
            run.Cancellation.Dispose();
            return _status;
        }
    }

    /// <summary>
    /// Returns the loaded library, re-reading the CSV when nothing is loaded, when the run is for a
    /// different file than the one in memory, or when that file has changed on disk since it was read.
    /// </summary>
    private async Task<List<OpenAudible>> EnsureBooksLoaded(string csvPath, CancellationToken cancellationToken)
    {
        string? loadedPath;
        (DateTime, long)? loadedStamp;
        List<OpenAudible> books;
        lock (_lock)
        {
            loadedPath = _booksCsvPath;
            loadedStamp = _booksCsvStamp;
            books = _books;
        }

        // OpenAudible exports over the top of the same file every time, and a container's CSV_PATH
        // never changes at all. Matching on the path alone meant that re-exporting your library and
        // pressing Start sorting quietly sorted whatever was read the first time — in a long-lived
        // container, potentially days earlier.
        var sameFile = books.Count > 0 && string.Equals(loadedPath, Path.GetFullPath(csvPath), StringComparison.OrdinalIgnoreCase);
        var unchanged = loadedStamp is not null && loadedStamp == ReadFileStamp(csvPath);

        return sameFile && unchanged
            ? books
            : (await LoadBooks(csvPath, cancellationToken)).Books;
    }

    private async Task<CsvParseResult> LoadBooks(string csvPath, CancellationToken cancellationToken)
    {
        var stamp = ReadFileStamp(csvPath);
        var result = await _csvParser.ParseAsync(csvPath, cancellationToken);

        lock (_lock)
        {
            _books = result.Books;
            _booksCsvPath = Path.GetFullPath(csvPath);
            _booksCsvStamp = stamp;
        }

        return result;
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

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A run in progress. Its token source is linked to the app stopping, so closing the app cancels
    /// the run instead of cutting it off mid-copy.
    /// </summary>
    private sealed class ActiveRun(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<RunStatus> Completion { get; set; } = null!;

        /// <summary>Cancelled because the desktop app is quitting; see <see cref="CancelSort"/>.</summary>
        public bool AppClosing { get; set; }
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
