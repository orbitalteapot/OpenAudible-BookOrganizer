using AudioFileSorter;

namespace ManagerApi.Services;

/// <summary>What GET /api/schedule returns.</summary>
/// <param name="IntervalMinutes">Minutes between automatic sorts, or null when they are off.</param>
/// <param name="Locked">Set by the server's SORT_INTERVAL, so the page may only show it.</param>
/// <param name="Retrying">The last attempt failed, so the next one is a retry rather than the regular run.</param>
/// <param name="BlockedReason">Why automatic sorting, although on, cannot run at the moment.</param>
public sealed record ScheduleStatus(
    int? IntervalMinutes,
    bool Locked,
    DateTime? NextRunUtc,
    RunRecord? LastRun,
    bool Retrying,
    string? BlockedReason);

/// <summary>
/// Sorts on a timer, with whatever the settings say when each run starts. Runs go through
/// <see cref="SortService"/> like the Sort page's, so a scheduled run and a manual one can never
/// overlap, and the page shows either with its progress and a Cancel button.
/// </summary>
public sealed class SortScheduler : BackgroundService
{
    /// <summary>
    /// The longest single wait. The timer does not count time a laptop spends asleep, so a long wait
    /// would run late by however long it slept; waking regularly and checking the wall clock again
    /// keeps a missed run to a few minutes late.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private readonly SortService _sortService;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SortScheduler> _logger;
    private readonly object _lock = new();

    /// <summary>Cancelled when the settings change, to cut the current wait short.</summary>
    private CancellationTokenSource _wake = new();

    public SortScheduler(SortService sortService, SettingsService settings, TimeProvider time, ILogger<SortScheduler> logger)
    {
        _sortService = sortService;
        _settings = settings;
        _time = time;
        _logger = logger;

        _settings.Changed += OnSettingsChanged;
    }

    public ScheduleStatus GetStatus()
    {
        var settings = _settings.Effective;
        var state = _settings.Schedule;
        var isOn = settings.ScheduleIntervalMinutes is not null;

        return new ScheduleStatus(
            settings.ScheduleIntervalMinutes,
            _settings.Config.ScheduleLocked,
            SortSchedule.NextRunUtc(settings.ScheduleIntervalMinutes, state, UtcNow()),
            state.LastRun,
            isOn && SortSchedule.LastAttemptFailed(state),
            isOn ? PathStatus.BlockedReason(settings, _settings.Config) ?? _settings.CheckUnattended(settings)?.Message : null);
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();

        lock (_lock)
        {
            _wake.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationTokenSource wake;
            lock (_lock)
            {
                wake = _wake;
            }

            var now = UtcNow();
            SaveClampedTimes(now);
            var dueUtc = SortSchedule.NextRunUtc(_settings.Effective.ScheduleIntervalMinutes, _settings.Schedule, now);
            if (dueUtc <= now)
            {
                await RunAsync();
                continue;
            }

            var untilDue = dueUtc is null ? MaxWait : dueUtc.Value - now;
            using (var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake.Token))
            {
                // A wake-up or a shutdown ends the wait early; either way the loop just looks again.
                // ForceYielding keeps the rest of the loop off the thread that cancelled the wait,
                // which may be a settings save holding its own lock.
                await Task.Delay(untilDue < MaxWait ? untilDue : MaxWait, _time, waitCancellation.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            }

            ReplaceWakeIfUsed();
        }
    }

    private async Task RunAsync()
    {
        var startedUtc = UtcNow();

        // Never creates the destination: nobody is there to notice it landing on the internal disk
        // because the drive it belongs on is unplugged.
        var options = _settings.SortOptionsFor(createDestination: false);

        RunRecord record;
        var joinedManualRun = false;
        try
        {
            if (!_sortService.TryStartSort(RunTrigger.Scheduled, options, out var run))
            {
                // A manual sort is doing the same job, so this slot is done when that sort is: its
                // outcome, not the fact that it was running, says whether the library got sorted.
                _logger.LogInformation("Scheduled sort waits for the sort already running");
                joinedManualRun = true;
            }

            record = RunRecord.From(await run);
        }
        catch (SortPathException ex)
        {
            var rejected = RunStatus.Rejected(RunTrigger.Scheduled, ex.Problem, startedUtc);
            _logger.LogWarning("{Summary}", RunSummary.Describe(rejected));
            record = RunRecord.From(rejected);
        }

        // A run that ended with an error (including one cut short by the app closing) did not sort
        // the library, so it is tried again soon; one a person cancelled is left until next time.
        // Cancelling the manual sort this slot waited for is not declining the slot, though: the
        // rest of the library would otherwise wait a whole interval.
        var succeeded = record.Error is null && !(joinedManualRun && record.IsCanceled);
        _settings.UpdateSchedule(state => state with
        {
            LastAttemptUtc = startedUtc,
            LastSuccessUtc = succeeded ? startedUtc : state.LastSuccessUtc,
            LastRun = record
        });
    }

    /// <summary>Keeps times the clock has since gone back past, as now (see <see cref="SortSchedule.ClampToNow"/>).</summary>
    private void SaveClampedTimes(DateTime now)
    {
        var state = _settings.Schedule;
        if (SortSchedule.ClampToNow(state, now) != state)
        {
            _settings.UpdateSchedule(current => SortSchedule.ClampToNow(current, now));
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _wake.Cancel();
        }
    }

    /// <summary>A cancelled wake-up source cannot be reset, so it is swapped for a fresh one.</summary>
    private void ReplaceWakeIfUsed()
    {
        CancellationTokenSource? used = null;
        lock (_lock)
        {
            if (_wake.IsCancellationRequested)
            {
                used = _wake;
                _wake = new CancellationTokenSource();
            }
        }

        used?.Dispose();
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;
}
