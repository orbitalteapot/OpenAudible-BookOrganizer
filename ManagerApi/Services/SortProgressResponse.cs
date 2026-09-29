using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// What GET /api/sort/progress returns: a run's progress in the shape the Sort page reads.
/// The page still thinks in "copied" (written, new or replaced) and "skipped" (nothing written),
/// so the disjoint counts are folded back into those here, and only here.
/// </summary>
public sealed record SortProgressResponse
{
    public int CurrentBook { get; init; }
    public int TotalBooks { get; init; }

    /// <summary>Books for which a file was written, new or replaced.</summary>
    public int CopiedBooks { get; init; }

    /// <summary>Books where an out-of-date file was replaced. Part of <see cref="CopiedBooks"/>.</summary>
    public int UpdatedBooks { get; init; }

    /// <summary>Books needing no copy: up to date, including those moved from an older layout.</summary>
    public int SkippedBooks { get; init; }

    public int MissingBooks { get; init; }
    public int FailedBooks { get; init; }
    public int WarningCount { get; init; }
    public string? CurrentTitle { get; init; }
    public double Percentage { get; init; }
    public bool IsComplete { get; init; }
    public bool IsCanceled { get; init; }
    public string? Error { get; init; }

    public static SortProgressResponse From(SortProgressInfo progress)
    {
        return FromCounts(progress.Counts, progress.ProblemCount) with
        {
            CurrentBook = progress.CurrentBook,
            TotalBooks = progress.TotalBooks,
            CurrentTitle = progress.CurrentTitle,
            Percentage = progress.Percentage
        };
    }

    public static SortProgressResponse Completed(SortSummary summary)
    {
        return FromCounts(summary.Counts, summary.ProblemCount) with
        {
            CurrentBook = summary.TotalBooks,
            TotalBooks = summary.TotalBooks,
            Percentage = 100,
            IsComplete = true
        };
    }

    private static SortProgressResponse FromCounts(SortCounts counts, int problemCount)
    {
        return new SortProgressResponse
        {
            CopiedBooks = counts.New + counts.Updated,
            UpdatedBooks = counts.Updated,
            SkippedBooks = counts.UpToDate + counts.Moved,
            MissingBooks = counts.NotFound,
            FailedBooks = counts.Failed,
            WarningCount = problemCount
        };
    }
}
