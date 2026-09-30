namespace ManagerApi.Services;

/// <summary>
/// The one place a run is put into words on the server, for the log. The page has its own
/// formatter for what users read; this one is for whoever reads <c>docker logs</c>.
/// </summary>
public static class RunSummary
{
    /// <summary>"Scheduled sort finished: 3 new, 120 up to date, 2 not found (2 problems)."</summary>
    public static string Describe(RunStatus status)
    {
        var run = status.Trigger switch
        {
            RunTrigger.Scheduled => "Scheduled sort",
            RunTrigger.Manual => "Manual sort",
            _ => "Sort"
        };

        if (status.IsCanceled)
        {
            var reason = status.Error is null ? "" : $" ({status.Error.TrimEnd('.')})";
            return $"{run} canceled after {status.CurrentBook} of {status.TotalBooks} books{reason}: {DescribeCounts(status)}";
        }

        return status.Error is not null
            ? $"{run} failed: {status.Error}"
            : $"{run} finished: {DescribeCounts(status)}";
    }

    /// <summary>Only the numbers that are not zero, then the problem count.</summary>
    private static string DescribeCounts(RunStatus status)
    {
        var counts = status.Counts;
        var parts = new (int Count, string Label)[]
            {
                (counts.New, "new"),
                (counts.Updated, "updated"),
                (counts.Moved, "moved"),
                (counts.UpToDate, "up to date"),
                (counts.NotFound, "not found"),
                (counts.Failed, "failed")
            }
            .Where(part => part.Count > 0)
            .Select(part => $"{part.Count} {part.Label}");

        var text = string.Join(", ", parts);
        if (text.Length == 0)
        {
            text = "no books processed";
        }

        return status.ProblemCount switch
        {
            0 => $"{text}.",
            1 => $"{text} (1 problem).",
            var count => $"{text} ({count} problems)."
        };
    }
}
