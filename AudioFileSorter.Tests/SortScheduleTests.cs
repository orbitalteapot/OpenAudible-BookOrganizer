using AudioFileSorter.Model;
using ManagerApi.Services;
using Microsoft.Extensions.Time.Testing;

namespace AudioFileSorter.Tests;

public class SortScheduleTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("off", null)]
    [InlineData("0", null)]
    [InlineData("6", 360)]
    [InlineData("6h", 360)]
    [InlineData(" 12H ", 720)]
    [InlineData("30m", 30)]
    [InlineData("1d", 1440)]
    public void Interval_parses_the_spellings_documented_for_sort_interval(string? value, int? expected)
    {
        Assert.True(SortSchedule.TryParseInterval(value, out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData("5m")]
    [InlineData("soon")]
    [InlineData("6 weeks")]
    [InlineData("-1h")]
    [InlineData("99999999999d")]
    public void Interval_rejects_what_it_cannot_read_or_would_run_too_often(string value)
    {
        Assert.False(SortSchedule.TryParseInterval(value, out _));
    }

    [Fact]
    public void Automatic_sorting_that_is_off_is_never_due()
    {
        Assert.Null(SortSchedule.NextRunUtc(null, new ScheduleState(), Now));
    }

    [Fact]
    public void A_schedule_that_has_never_run_is_due_now()
    {
        Assert.Equal(Now, SortSchedule.NextRunUtc(360, new ScheduleState(), Now));
    }

    [Fact]
    public void A_schedule_turned_on_since_its_last_attempt_is_due_now()
    {
        var state = Succeeded(Now.AddHours(-1)) with { EnabledAtUtc = Now.AddMinutes(-1) };

        Assert.Equal(Now, SortSchedule.NextRunUtc(360, state, Now));
    }

    [Fact]
    public void After_a_success_the_next_run_is_one_interval_after_it_started()
    {
        var lastRun = Now.AddHours(-1);

        Assert.Equal(lastRun.AddHours(6), SortSchedule.NextRunUtc(360, Succeeded(lastRun), Now));
    }

    [Fact]
    public void After_a_failure_the_next_run_is_a_retry_fifteen_minutes_later()
    {
        var state = Succeeded(Now.AddHours(-3)) with { LastAttemptUtc = Now.AddMinutes(-1) };

        Assert.True(SortSchedule.LastAttemptFailed(state));
        Assert.Equal(Now.AddMinutes(14), SortSchedule.NextRunUtc(360, state, Now));
    }

    [Fact]
    public void A_retry_never_comes_later_than_the_regular_run_would_have()
    {
        var state = Succeeded(Now.AddMinutes(-355)) with { LastAttemptUtc = Now };

        Assert.Equal(Now.AddMinutes(5), SortSchedule.NextRunUtc(360, state, Now));
    }

    [Fact]
    public void A_last_run_in_the_future_after_the_clock_was_set_back_counts_as_now()
    {
        var state = Succeeded(Now.AddDays(2));

        Assert.Equal(Now.AddHours(6), SortSchedule.NextRunUtc(360, state, Now));
    }

    [Fact]
    public async Task The_first_run_starts_as_soon_as_the_scheduler_does()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 60, time: time);
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastRun is not null, "the first run");
        await scheduler.StopAsync(CancellationToken.None);

        var state = backend.Settings.Schedule;
        Assert.Equal(Now, state.LastAttemptUtc);
        Assert.Equal(Now, state.LastSuccessUtc);
        Assert.Equal(RunTrigger.Scheduled, state.LastRun!.Trigger);
        Assert.Equal(1, state.LastRun.Counts.New);
        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task The_next_run_comes_one_interval_after_the_last()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 60, time: time);
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the first run");

        await AdvanceAndSettle(time, TimeSpan.FromMinutes(59));
        Assert.Equal(Now, backend.Settings.Schedule.LastAttemptUtc);

        await AdvanceUntil(time, () => backend.Settings.Schedule.LastAttemptUtc > Now, "the second run");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.InRange(backend.Settings.Schedule.LastAttemptUtc!.Value, Now.AddMinutes(60), Now.AddMinutes(62));
        Assert.Equal(1, backend.Settings.Schedule.LastRun!.Counts.UpToDate);
    }

    [Fact]
    public async Task A_failed_run_is_retried_after_fifteen_minutes_and_never_creates_the_destination()
    {
        using var workspace = new TempWorkspace();
        var unplugged = Path.Combine(workspace.Root, "unplugged");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(
            workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 360, destination: unplugged, time: time);
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the first attempt");

        var status = scheduler.GetStatus();
        Assert.Equal("destinationMissing", status.LastRun!.ErrorCode);
        Assert.True(status.Retrying);
        Assert.Equal(Now.AddMinutes(15), status.NextRunUtc);
        Assert.Null(backend.Settings.Schedule.LastSuccessUtc);
        Assert.False(Directory.Exists(unplugged));

        await AdvanceAndSettle(time, TimeSpan.FromMinutes(14));
        Assert.Equal(Now, backend.Settings.Schedule.LastAttemptUtc);

        await AdvanceUntil(time, () => backend.Settings.Schedule.LastAttemptUtc > Now, "the retry");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.InRange(backend.Settings.Schedule.LastAttemptUtc!.Value, Now.AddMinutes(15), Now.AddMinutes(17));
        Assert.False(Directory.Exists(unplugged));
    }

    [Fact]
    public async Task Turning_automatic_sorting_on_wakes_the_scheduler_and_sorts_at_once()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = new TestBackend(new ServerConfig(), time);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch
        {
            CsvPath = workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"),
            SourcePath = workspace.Source,
            DestinationPath = workspace.Destination
        }, out _));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Null(backend.Settings.Schedule.LastAttemptUtc);

        // No time passes on the fake clock: only the change can end the scheduler's wait.
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = 1440 }, out var error), error?.Message);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastRun is not null, "the run the change starts");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Now, backend.Settings.Schedule.EnabledAtUtc);
        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Closing_the_app_cancels_a_scheduled_run_and_it_is_tried_again()
    {
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200), 1440, time: time);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out _));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the run to start");

        backend.Lifetime.StopApplication();
        await scheduler.StopAsync(CancellationToken.None);

        var state = backend.Settings.Schedule;
        Assert.True(state.LastRun!.IsCanceled);
        Assert.Equal("Canceled because the app closed.", state.LastRun.Error);
        Assert.True(SortSchedule.LastAttemptFailed(state));
        Assert.False(backend.Sort.IsSorting);
    }

    [Fact]
    public void The_schedule_says_why_it_cannot_run_naming_the_server_setting()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig { SourcePath = workspace.Source, DestinationPath = workspace.Destination, ScheduleIntervalMinutes = 360 };
        using var backend = new TestBackend(config);
        using var scheduler = backend.CreateScheduler();

        var status = scheduler.GetStatus();

        Assert.True(status.Locked);
        Assert.Equal(360, status.IntervalMinutes);
        Assert.Equal("CSV_PATH is not set.", status.BlockedReason);
    }

    private static ScheduleState Succeeded(DateTime startedUtc) => new()
    {
        LastAttemptUtc = startedUtc,
        LastSuccessUtc = startedUtc
    };

    /// <summary>Moves the fake clock on and gives the scheduler a moment to react to it.</summary>
    private static async Task AdvanceAndSettle(FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        await Task.Delay(200);
    }

    /// <summary>
    /// Moves the fake clock a minute at a time until <paramref name="condition"/> holds. Stepping
    /// rather than jumping keeps the test independent of whether the scheduler had started its
    /// next wait before the clock moved.
    /// </summary>
    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> condition, string what)
    {
        for (var step = 0; step < 10 && !condition(); step++)
        {
            await AdvanceAndSettle(time, TimeSpan.FromMinutes(1));
        }

        await TestBackend.WaitUntil(condition, what);
    }
}
