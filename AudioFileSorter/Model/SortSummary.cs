namespace AudioFileSorter.Model;

/// <summary>Outcome of a completed (or cancelled) sort run.</summary>
public sealed class SortSummary
{
    public int TotalBooks { get; init; }

    /// <summary>Books for which at least one file was written, whether new or replaced.</summary>
    public int CopiedBooks { get; init; }

    /// <summary>
    /// Books where a file already at the destination was found to be out of date and replaced.
    /// A subset of <see cref="CopiedBooks"/>.
    /// </summary>
    public int UpdatedBooks { get; init; }

    /// <summary>Books already present and up to date at the destination.</summary>
    public int SkippedBooks { get; init; }

    /// <summary>Books listed in the export with no matching file in the source folder.</summary>
    public int MissingBooks { get; init; }

    /// <summary>Books that could not be processed because of an error.</summary>
    public int FailedBooks { get; init; }

    public bool IsCanceled { get; init; }

    /// <summary>A capped sample of the problems encountered, in the order they occurred.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Total number of warnings, which may be larger than <see cref="Warnings"/>.</summary>
    public int WarningCount { get; init; }
}
