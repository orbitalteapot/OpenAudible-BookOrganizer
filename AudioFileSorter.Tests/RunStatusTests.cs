using System.Text.Json;
using AudioFileSorter.Model;
using ManagerApi.Services;

namespace AudioFileSorter.Tests;

public class RunStatusTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_run_is_preparing_until_the_sorter_reports_its_plan()
    {
        var starting = RunStatus.Starting(RunTrigger.Manual, Now);
        var planned = starting.With(new SortProgressInfo(0, 0, null, SortCounts.Empty, [], [], 0));

        Assert.True(starting.Preparing);
        // An empty export plans to nothing at all, which is still a plan: 0 books is not "preparing".
        Assert.False(planned.Preparing);
        Assert.False(starting.Failed("The export could not be read", RunErrors.CsvInvalid, RunErrors.CsvPathField, Now).Preparing);
        Assert.False(starting.Canceled(null, Now).Preparing);
    }

    [Fact]
    public void The_log_calls_moved_books_moved_whatever_moved_them()
    {
        // Books are moved for a new author, series, number or title too, not only from an older layout.
        var summary = new SortSummary(3, SortCounts.Empty with { Moved = 2, UpToDate = 1 }, [], 0);

        var text = RunSummary.Describe(RunStatus.Starting(RunTrigger.Manual, Now).Completed(summary, Now));

        Assert.StartsWith("Manual sort finished: 2 moved, 1 up to date", text);
    }

    [Fact]
    public void The_page_is_told_that_a_run_is_preparing()
    {
        var json = JsonSerializer.SerializeToElement(RunStatus.Starting(RunTrigger.Scheduled, Now), JsonSerializerOptions.Web);

        Assert.True(json.GetProperty("preparing").GetBoolean());
        Assert.False(json.TryGetProperty("planned", out _));
    }
}
