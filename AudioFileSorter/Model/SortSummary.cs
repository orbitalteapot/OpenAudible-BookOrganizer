namespace AudioFileSorter.Model;

/// <summary>Outcome of a completed sort run.</summary>
/// <param name="Problems">The problems encountered, oldest first, capped at <see cref="MaxReportedProblems"/>.</param>
/// <param name="ProblemCount">All problems encountered, which may be more than <paramref name="Problems"/> holds.</param>
public sealed record SortSummary(int TotalBooks, SortCounts Counts, IReadOnlyList<SortProblem> Problems, int ProblemCount)
{
    /// <summary>
    /// Problems kept per run. A library whose source folder is unplugged makes every book a
    /// problem; nobody reads thousands of identical lines, and holding them all costs memory and
    /// payload on every progress poll.
    /// </summary>
    public const int MaxReportedProblems = 500;
}
