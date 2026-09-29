using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>A sort was started with paths that <see cref="SortPathValidator"/> rejects.</summary>
public sealed class SortPathException(SortPathProblem problem) : Exception(problem.Message)
{
    public SortPathProblem Problem { get; } = problem;
}
