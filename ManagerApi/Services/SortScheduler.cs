using System.Text.Json;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Runs a sort on a timer. It starts runs through <see cref="SortService"/> like the Sort page
/// does, so a scheduled run and a manual one can never overlap, and the progress of either shows up
/// in the same place.
///
/// The schedule comes from one of two places. In a container, SORT_INTERVAL sets it and it cannot be
/// changed from the page, in keeping with the paths being fixed by the container too. Otherwise the
/// page sets it and it is saved to the settings file, so it survives a restart and a run that was
/// missed while the app was closed happens on the next start.
/// </summary>
public sealed class SortScheduler : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Task.Delay rejects anything beyond ~24 days; longer waits are taken in steps.</summary>
    private static readonly TimeSpan MaxSingleDelay = TimeSpan.FromDays(1);

    private readonly SortService _sortService;
    private readonly string? _settingsPath;
    private readonly ILogger<SortScheduler> _logger;
    private readonly object _lock = new();

    private SortSchedule _schedule;
    private CancellationTokenSource _wake = new();

    /// <param name="settingsPath">Where to save the schedule. Null keeps it in memory only.</param>
    /// <param name="serverSchedule">A schedule fixed by the environment, or null to let the page set it.</param>
    public SortScheduler(SortService sortService, string? settingsPath, SortSchedule? serverSchedule, ILogger<SortScheduler> logger)
    {
        _sortService = sortService;
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath;
        _logger = logger;

        var saved = Load();
        IsManagedByServer = serverSchedule is not null;

        // A container restart should not trigger a fresh sort of a library it sorted an hour ago,
        // so the last-run time is kept even when the schedule itself comes from the environment.
        _schedule = serverSchedule is null
            ? saved
            : serverSchedule with { LastRunUtc = saved.LastRunUtc, LastResult = saved.LastResult };
    }

    /// <summary>True when the environment sets the schedule and the page may only display it.</summary>
    public bool IsManagedByServer { get; }

    public SortSchedule Current
    {
        get
        {
            lock (_lock)
            {
                return _schedule;
            }
        }
    }

    /// <summary>Replaces the schedule, keeping the record of the last run.</summary>
    /// <exception cref="InvalidOperationException">The environment sets the schedule.</exception>
    public SortSchedule Update(SortSchedule schedule)
    {
        if (IsManagedByServer)
        {
            throw new InvalidOperationException("Automatic sorting is set by the server's SORT_INTERVAL setting.");
        }

        CancellationTokenSource wake;
        lock (_lock)
        {
            _schedule = schedule with { LastRunUtc = _schedule.LastRunUtc, LastResult = _schedule.LastResult };
            Save(_schedule);

            wake = _wake;
            _wake = new CancellationTokenSource();
        }

        // Interrupts the current wait so the new interval applies now, not after the old one ends.
        wake.Cancel();
        return Current;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SortSchedule schedule;
            CancellationToken wakeToken;
            lock (_lock)
            {
                schedule = _schedule;
                wakeToken = _wake.Token;
            }

            var dueUtc = schedule.NextRunUtc(DateTime.UtcNow);
            var wait = dueUtc is null ? MaxSingleDelay : dueUtc.Value - DateTime.UtcNow;

            if (wait > TimeSpan.Zero)
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wakeToken);
                try
                {
                    await Task.Delay(wait < MaxSingleDelay ? wait : MaxSingleDelay, waitCancellation.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The schedule changed; start over with the new one.
                }

                continue;
            }

            await RunAsync(schedule);
        }
    }

    private async Task RunAsync(SortSchedule schedule)
    {
        var startedUtc = DateTime.UtcNow;
        _logger.LogInformation("Starting scheduled sort of {CsvPath}", schedule.CsvPath);

        var result = SortService.ValidatePaths(schedule.CsvPath!, schedule.SourcePath!, schedule.DestinationPath!);
        if (result is null)
        {
            var options = new SortOptions { ComparisonMode = schedule.ComparisonMode };
            if (_sortService.TryStartSort(schedule.CsvPath!, schedule.SourcePath!, schedule.DestinationPath!, options, out var sortTask))
            {
                await sortTask;
                result = Describe(_sortService.GetProgress());
            }
            else
            {
                // A manual sort is doing the same job, so this slot counts as done.
                result = "Skipped: a sort was already running.";
            }
        }

        _logger.LogInformation("Scheduled sort finished: {Result}", result);

        lock (_lock)
        {
            _schedule = _schedule with { LastRunUtc = startedUtc, LastResult = result };
            Save(_schedule);
        }
    }

    private static string Describe(SortProgressInfo progress)
    {
        if (progress.Error is not null)
        {
            return $"Failed: {progress.Error}";
        }

        var outcome = progress.IsCanceled ? "Canceled" : "Done";
        return $"{outcome}: {progress.CopiedBooks} copied ({progress.UpdatedBooks} updated), " +
               $"{progress.SkippedBooks} already up to date, {progress.MissingBooks} not found, {progress.FailedBooks} failed.";
    }

    private SortSchedule Load()
    {
        if (_settingsPath is null || !File.Exists(_settingsPath))
        {
            return new SortSchedule();
        }

        try
        {
            return JsonSerializer.Deserialize<SortSchedule>(File.ReadAllText(_settingsPath), JsonOptions) ?? new SortSchedule();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the saved schedule at {Path}; automatic sorting is off", _settingsPath);
            return new SortSchedule();
        }
    }

    /// <summary>Written beside the target and renamed over it, so a crash never leaves half a file.</summary>
    private void Save(SortSchedule schedule)
    {
        if (_settingsPath is null)
        {
            return;
        }

        var partialPath = _settingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!);
            File.WriteAllText(partialPath, JsonSerializer.Serialize(schedule, JsonOptions));
            File.Move(partialPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the schedule to {Path}; it will be forgotten on restart", _settingsPath);
        }
    }
}
