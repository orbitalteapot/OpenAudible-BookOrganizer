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

    [Theory]
    [InlineData(1440)]
    [InlineData(3 * 1440)]
    public void A_failed_regular_run_is_retried_fifteen_minutes_later_not_at_once(int minutesSinceSuccess)
    {
        var state = Succeeded(Now.AddMinutes(-minutesSinceSuccess)) with { LastAttemptUtc = Now };

        Assert.Equal(Now.AddMinutes(15), SortSchedule.NextRunUtc(1440, state, Now));
    }

    [Fact]
    public void A_last_run_in_the_future_after_the_clock_was_set_back_counts_as_now()
    {
        var state = Succeeded(Now.AddDays(2));

        Assert.Equal(Now.AddHours(6), SortSchedule.NextRunUtc(360, state, Now));
    }

    [Fact]
    public void A_failed_run_stays_failed_when_the_clock_goes_back_past_it_and_the_last_success()
    {
        var state = Succeeded(Now.AddDays(2)) with { LastAttemptUtc = Now.AddDays(3) };

        var clamped = SortSchedule.ClampToNow(state, Now);

        // Both clamped to now, the two times matched and the failure read as a success a day away.
        Assert.True(SortSchedule.LastAttemptFailed(clamped));
        Assert.Equal(Now, clamped.LastAttemptUtc);
        Assert.Equal(Now.AddMinutes(15), SortSchedule.NextRunUtc(1440, state, Now));
        Assert.Equal(clamped, SortSchedule.ClampToNow(clamped, Now));
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
        Assert.True(File.Exists(Path.Combine(workspace.Destination, SortPathValidator.MarkerFileName)));
        Assert.Equal(workspace.Destination, state.MarkedDestinationPath);
    }

    [Fact]
    public async Task An_automatic_run_leaves_an_empty_stand_in_for_an_unmounted_destination_alone()
    {
        // Docker recreated the unmounted drive behind /destination as an empty folder on the system disk.
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(20), 1440, time: time);
        backend.Settings.UpdateSchedule(_ => Succeeded(Now.AddDays(-1)) with { MarkedDestinationPath = workspace.Destination });
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the regular run");
        await scheduler.StopAsync(CancellationToken.None);

        var status = scheduler.GetStatus();
        Assert.Equal("destinationUnmounted", status.LastRun!.ErrorCode);
        Assert.True(status.Retrying);
        Assert.Contains(SortPathValidator.MarkerFileName, status.BlockedReason);
        Assert.Empty(workspace.DestinationFiles());

        // A person who emptied it on purpose sorts once by hand, which marks it again.
        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var manual));
        Assert.Null((await manual).Error);
        Assert.Equal(20, workspace.DestinationFiles().Length);
        Assert.Null(scheduler.GetStatus().BlockedReason);
    }

    [Fact]
    public async Task An_automatic_run_sorts_into_a_destination_chosen_since_the_last_one_was_marked()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 1440, time: time);
        backend.Settings.UpdateSchedule(_ => Succeeded(Now.AddDays(-1)) with { MarkedDestinationPath = workspace.Root });
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the regular run");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Null(backend.Settings.Schedule.LastRun!.Error);
        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
        Assert.Equal(workspace.Destination, backend.Settings.Schedule.MarkedDestinationPath);
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
    public async Task A_failed_regular_run_is_tried_once_until_fifteen_minutes_pass()
    {
        using var workspace = new TempWorkspace();
        var unplugged = Path.Combine(workspace.Root, "unplugged");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(
            workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 1440, destination: unplugged, time: time);
        backend.Settings.UpdateSchedule(_ => Succeeded(Now.AddDays(-1)));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the regular run");

        // Lets the scheduler start its wait first: a wait worked out before the jump but started after
        // it would end up to five minutes late.
        await Task.Delay(200);

        // Due again at once, the failed run was repeated in a tight loop, so the clock moving on
        // moved the last attempt with it.
        await AdvanceAndSettle(time, TimeSpan.FromMinutes(14));
        Assert.Equal(Now, backend.Settings.Schedule.LastAttemptUtc);
        Assert.Equal(Now.AddMinutes(15), scheduler.GetStatus().NextRunUtc);

        await AdvanceUntil(time, () => backend.Settings.Schedule.LastAttemptUtc > Now, "the retry");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.InRange(backend.Settings.Schedule.LastAttemptUtc!.Value, Now.AddMinutes(15), Now.AddMinutes(17));
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
    public async Task Stopping_a_scheduled_run_to_quit_the_desktop_app_is_tried_again()
    {
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200), 1440, time: time);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out _));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the run to start");

        // "Stop sorting and quit": the app asks before it closes, so the backend is not stopping yet.
        Assert.True(backend.Sort.CancelSort(appClosing: true));
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastRun is not null, "the run to be recorded");
        await scheduler.StopAsync(CancellationToken.None);

        // Counted as done, the rest of the library would wait a whole interval.
        var state = backend.Settings.Schedule;
        Assert.Equal("Canceled because the app closed.", state.LastRun!.Error);
        Assert.True(SortSchedule.LastAttemptFailed(state));
        Assert.Null(state.LastSuccessUtc);
    }

    [Fact]
    public async Task A_run_due_during_a_manual_sort_counts_as_done_when_that_sort_finishes()
    {
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(20), 1440, time: time);
        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var manual));
        using var scheduler = backend.CreateScheduler();

        // Due at once, the scheduler finds the manual sort running and waits for it.
        await scheduler.StartAsync(CancellationToken.None);
        await manual;
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastRun is not null, "the slot to be recorded");
        await scheduler.StopAsync(CancellationToken.None);

        var state = backend.Settings.Schedule;
        Assert.Equal(RunTrigger.Manual, state.LastRun!.Trigger);
        Assert.Equal(Now, state.LastSuccessUtc);
        Assert.False(SortSchedule.LastAttemptFailed(state));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_due_during_a_manual_sort_that_is_cut_short_is_tried_again(bool appClosing)
    {
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200), 1440, time: time);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out _));
        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out _));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the manual run to start");
        Assert.True(backend.Sort.CancelSort(appClosing));
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastRun is not null, "the slot to be recorded");
        await scheduler.StopAsync(CancellationToken.None);

        // Counted as done, the rest of the library would wait a whole interval.
        var state = backend.Settings.Schedule;
        Assert.True(state.LastRun!.IsCanceled);
        Assert.True(SortSchedule.LastAttemptFailed(state));
        Assert.Null(state.LastSuccessUtc);
        Assert.Equal(Now.AddMinutes(15), scheduler.GetStatus().NextRunUtc);
    }

    [Fact]
    public async Task A_schedule_turned_on_at_a_time_the_clock_has_since_gone_back_past_runs_once()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 1440, time: time);
        backend.Settings.UpdateSchedule(_ => new ScheduleState
        {
            EnabledAtUtc = Now.AddHours(1),
            LastAttemptUtc = Now.AddHours(-1),
            LastSuccessUtc = Now.AddHours(-1)
        });
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastAttemptUtc == Now, "the first run");

        // Clamped afresh on every look, "turned on" stayed ahead of every run and started one sort after another.
        for (var minute = 0; minute < 4; minute++)
        {
            await AdvanceAndSettle(time, TimeSpan.FromMinutes(1));
        }

        await scheduler.StopAsync(CancellationToken.None);
        Assert.Equal(Now, backend.Settings.Schedule.LastAttemptUtc);
        Assert.Equal(Now, backend.Settings.Schedule.EnabledAtUtc);
        Assert.Equal(Now.AddDays(1), scheduler.GetStatus().NextRunUtc);
    }

    [Fact]
    public async Task After_the_clock_is_set_back_the_next_run_still_comes_one_interval_later()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var time = new FakeTimeProvider(Now);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), 60, time: time);
        backend.Settings.UpdateSchedule(_ => Succeeded(Now.AddDays(2)));
        using var scheduler = backend.CreateScheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await TestBackend.WaitUntil(() => backend.Settings.Schedule.LastSuccessUtc == Now, "the future time to be brought back");

        // Counted as "now" afresh on every wake, the due time slid along with the clock and never came.
        await AdvanceAndSettle(time, TimeSpan.FromMinutes(30));
        Assert.Equal(Now.AddMinutes(60), scheduler.GetStatus().NextRunUtc);
        await AdvanceAndSettle(time, TimeSpan.FromMinutes(29));

        await AdvanceUntil(time, () => backend.Settings.Schedule.LastRun is not null, "the run an interval later");
        await scheduler.StopAsync(CancellationToken.None);

        Assert.InRange(backend.Settings.Schedule.LastAttemptUtc!.Value, Now.AddMinutes(60), Now.AddMinutes(62));
    }

    [Fact]
    public void Clamping_brings_only_the_times_in_the_future_back_to_now()
    {
        var state = new ScheduleState { EnabledAtUtc = Now.AddDays(-1), LastAttemptUtc = Now.AddHours(3), LastSuccessUtc = null };

        var clamped = SortSchedule.ClampToNow(state, Now);

        Assert.Equal(Now.AddDays(-1), clamped.EnabledAtUtc);
        Assert.Equal(Now, clamped.LastAttemptUtc);
        Assert.Null(clamped.LastSuccessUtc);
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

    [Fact]
    public void A_missing_mount_is_explained_in_container_terms_not_as_an_unplugged_drive()
    {
        using var workspace = new TempWorkspace();
        var destination = Path.Combine(workspace.Root, "destination-not-mounted", "Audiobooks");
        var config = new ServerConfig
        {
            CsvPath = workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"),
            SourcePath = workspace.Source,
            DestinationPath = destination,
            ScheduleIntervalMinutes = 360
        };
        using var backend = new TestBackend(config);
        using var scheduler = backend.CreateScheduler();

        Assert.Equal(
            $"The destination folder {destination} was not found inside the container. Check the volume mapping for DESTINATION_PATH.",
            scheduler.GetStatus().BlockedReason);
    }

    [Fact]
    public void A_missing_subfolder_of_a_working_mount_is_not_blamed_on_the_mapping()
    {
        using var workspace = new TempWorkspace();
        var destination = Path.Combine(workspace.Destination, "Audiobooks");
        var config = new ServerConfig
        {
            CsvPath = workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"),
            SourcePath = workspace.Source,
            DestinationPath = destination,
            ScheduleIntervalMinutes = 360
        };
        using var backend = new TestBackend(config);
        using var scheduler = backend.CreateScheduler();

        Assert.Equal(
            $"The folder Audiobooks does not exist inside {workspace.Destination}. Create it on the host (in the folder mapped to {workspace.Destination}), then try again.",
            scheduler.GetStatus().BlockedReason);
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
