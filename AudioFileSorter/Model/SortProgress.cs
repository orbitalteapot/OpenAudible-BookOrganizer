namespace AudioFileSorter.Model;

/// <summary>
/// A snapshot of a sort in progress, taken as one book finishes. Every field comes from the same
/// moment, so the counts always add up to <see cref="CurrentBook"/>.
/// </summary>
/// <param name="TotalBooks">Books in the run.</param>
/// <param name="CurrentBook">Books finished so far.</param>
/// <param name="CurrentTitle">The book that just finished, as a person would name it. Null before the first.</param>
/// <param name="Counts">Where the finished books ended up.</param>
/// <param name="Problems">
/// The problems recorded so far, oldest first, capped at <see cref="SortSummary.MaxReportedProblems"/>.
/// Carried in every snapshot so a run that is cancelled part way still has them to show.
/// </param>
/// <param name="RecentProblems">The latest <see cref="RecentProblemLimit"/> problems, oldest first, for a page following the run.</param>
/// <param name="ProblemCount">All problems recorded so far, which may be more than <paramref name="Problems"/> holds.</param>
public sealed record SortProgressInfo(
    int TotalBooks,
    int CurrentBook,
    string? CurrentTitle,
    SortCounts Counts,
    IReadOnlyList<SortProblem> Problems,
    IReadOnlyList<SortProblem> RecentProblems,
    int ProblemCount)
{
    /// <summary>
    /// Problems in <see cref="RecentProblems"/>. A page polls several times a second, and a run with an
    /// unplugged source makes every book a problem, so only the latest few are sent while it runs.
    /// </summary>
    public const int RecentProblemLimit = 20;

    /// <summary>Share of the books finished, 0 to 100. An empty run is complete from the start.</summary>
    public double Percentage => TotalBooks <= 0
        ? 100
        : Math.Round(Math.Clamp((double)CurrentBook / TotalBooks, 0, 1) * 100, 2);
}
