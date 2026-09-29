using AudioFileSorter.Model;
using ManagerApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioFileSorter.Tests;

public class SortScheduleTests
{
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
    public void A_schedule_without_all_three_paths_never_runs()
    {
        var schedule = new SortSchedule { IntervalMinutes = 60, CsvPath = "books.csv", SourcePath = "source" };

        Assert.Null(schedule.NextRunUtc(DateTime.UtcNow));
    }

    [Fact]
    public void A_schedule_that_has_never_run_is_due_now()
    {
        var now = DateTime.UtcNow;

        Assert.Equal(now, Complete(lastRunUtc: null).NextRunUtc(now));
    }

    [Fact]
    public void A_schedule_is_due_one_interval_after_its_last_run()
    {
        var lastRun = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(lastRun.AddHours(6), Complete(lastRun).NextRunUtc(lastRun.AddMinutes(1)));
    }

    [Fact]
    public void Scheduler_saves_the_schedule_and_reads_it_back_after_a_restart()
    {
        using var workspace = new TempWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        var schedule = Runnable(workspace);

        Assert.True(CreateScheduler(settingsPath).TryUpdate(schedule, out var error), error);
        var restarted = CreateScheduler(settingsPath);

        Assert.Equal(schedule.IntervalMinutes, restarted.Current.IntervalMinutes);
        Assert.Equal(schedule.CsvPath, restarted.Current.CsvPath);
    }

    [Fact]
    public void Scheduler_refuses_changes_to_a_schedule_the_server_sets()
    {
        var scheduler = CreateScheduler(null, serverSchedule: Complete(null));

        Assert.True(scheduler.IsManagedByServer);
        Assert.False(scheduler.TryUpdate(new SortSchedule(), out var error));
        Assert.Contains("SORT_INTERVAL", error);
    }

    [Fact]
    public void Scheduler_refuses_to_turn_on_with_paths_that_cannot_work()
    {
        using var workspace = new TempWorkspace();

        Assert.False(CreateScheduler(null).TryUpdate(Runnable(workspace) with { CsvPath = "missing.csv" }, out var error));
        Assert.Contains("library export was not found", error);
    }

    [Fact]
    public void Scheduler_refuses_an_interval_below_the_minimum()
    {
        using var workspace = new TempWorkspace();

        Assert.False(CreateScheduler(null).TryUpdate(Runnable(workspace) with { IntervalMinutes = 5 }, out _));
    }

    [Fact]
    public void Scheduler_can_always_be_turned_off()
    {
        Assert.True(CreateScheduler(null).TryUpdate(new SortSchedule(), out var error), error);
    }

    [Fact]
    public async Task Scheduler_sorts_when_due_and_records_the_result()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        using var scheduler = CreateScheduler(null, serverSchedule: Runnable(workspace));

        await scheduler.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (scheduler.Current.LastRunUtc is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
        Assert.StartsWith("Done: 1 copied", scheduler.Current.LastResult);
    }

    private static SortScheduler CreateScheduler(string? settingsPath, SortSchedule? serverSchedule = null)
    {
        var logger = NullLogger<SortScheduler>.Instance;
        return new SortScheduler(
            new SortService(), new SortScheduleStore(settingsPath, logger), serverSchedule, logger, SortOptions.DefaultParallelism);
    }

    /// <summary>A schedule whose paths exist, with one book in the export.</summary>
    private static SortSchedule Runnable(TempWorkspace workspace)
    {
        var csvPath = Path.Combine(workspace.Root, "books.csv");
        File.WriteAllText(csvPath, "Title,Author,File name\nThe Hobbit,Tolkien,the-hobbit\n");

        return new SortSchedule
        {
            IntervalMinutes = 60,
            CsvPath = csvPath,
            SourcePath = workspace.Source,
            DestinationPath = workspace.Destination
        };
    }

    private static SortSchedule Complete(DateTime? lastRunUtc) => new()
    {
        IntervalMinutes = 360,
        CsvPath = "books.csv",
        SourcePath = "source",
        DestinationPath = "destination",
        LastRunUtc = lastRunUtc
    };
}
