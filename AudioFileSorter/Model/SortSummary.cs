namespace AudioFileSorter.Model;

/// <summary>Outcome of a completed (or cancelled) sort run.</summary>
public sealed class SortSummary
{
    public int TotalBooks { get; init; }

    /// <summary>Books for which at least one file was written.</summary>
    public int CopiedBooks { get; init; }

    /// <summary>Books that were already up to date, or had no file to copy.</summary>
    public int SkippedBooks { get; init; }

    /// <summary>Books that could not be processed because of an error.</summary>
    public int FailedBooks { get; init; }

    public bool IsCanceled { get; init; }

    /// <summary>A capped sample of the problems encountered, in the order they occurred.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Total number of warnings, which may be larger than <see cref="Warnings"/>.</summary>
    public int WarningCount { get; init; }
}
