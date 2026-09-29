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
    /// <summary>Task.Delay rejects anything beyond ~24 days; longer waits are taken in steps.</summary>
    private static readonly TimeSpan MaxSingleDelay = TimeSpan.FromDays(1);

    private readonly SortService _sortService;
    private readonly SortScheduleStore _store;
    private readonly ILogger<SortScheduler> _logger;
    private readonly object _lock = new();

    private SortSchedule _schedule;
    private CancellationTokenSource _wake = new();

    /// <param name="serverSchedule">A schedule fixed by the environment, or null to let the page set it.</param>
    public SortScheduler(SortService sortService, SortScheduleStore store, SortSchedule? serverSchedule, ILogger<SortScheduler> logger)
    {
        _sortService = sortService;
        _store = store;
        _logger = logger;

        var saved = store.Load();
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

    /// <summary>
    /// Replaces the schedule, keeping the record of the last run. Turning automatic sorting on with
    /// paths that cannot work is refused now, while the user is looking, rather than failing
    /// silently at three in the morning.
    /// </summary>
    /// <param name="error">Why the schedule was refused, for the user.</param>
    public bool TryUpdate(SortSchedule schedule, out string? error)
    {
        error = Validate(schedule);
        if (error is not null)
        {
            return false;
        }

        CancellationTokenSource wake;
        lock (_lock)
        {
            _schedule = schedule with { LastRunUtc = _schedule.LastRunUtc, LastResult = _schedule.LastResult };
            _store.Save(_schedule);

            wake = _wake;
            _wake = new CancellationTokenSource();
        }

        // Interrupts the current wait so the new interval applies now, not after the old one ends.
        wake.Cancel();
        return true;
    }

    private string? Validate(SortSchedule schedule)
    {
        if (IsManagedByServer)
        {
            return "Automatic sorting is set by the server's SORT_INTERVAL setting.";
        }

        if (schedule.IntervalMinutes is null)
        {
            return null;
        }

        if (!SortSchedule.IsValidInterval(schedule.IntervalMinutes.Value))
        {
            return $"Sort at most every {SortSchedule.MinimumIntervalMinutes} minutes.";
        }

        return schedule.IsEnabled
            ? SortService.ValidatePaths(schedule.CsvPath!, schedule.SourcePath!, schedule.DestinationPath!)
            : "Choose all three paths before turning on automatic sorting.";
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
            _store.Save(_schedule);
        }
    }

    /// <summary>"Done: 3 copied, 120 up to date." Only the numbers that are not zero.</summary>
    private static string Describe(SortProgressInfo progress)
    {
        if (progress.Error is not null)
        {
            return $"Failed: {progress.Error}";
        }

        var counts = new (int Count, string Label)[]
        {
            (progress.CopiedBooks, "copied"),
            (progress.UpdatedBooks, "of them updated"),
            (progress.SkippedBooks, "up to date"),
            (progress.MissingBooks, "not found"),
            (progress.FailedBooks, "failed")
        };

        var details = string.Join(", ", counts.Where(c => c.Count > 0).Select(c => $"{c.Count} {c.Label}"));
        var outcome = progress.IsCanceled ? "Canceled" : "Done";
        return details.Length == 0 ? $"{outcome}: no books in the export." : $"{outcome}: {details}.";
    }
}
