namespace AudioFileSorter.Model;

/// <summary>
/// One book's share of the work: the exact files to read and the exact paths to write.
/// Produced by <see cref="SortPlanner"/> before any copying starts.
/// </summary>
public sealed record PlannedCopy
{
    public required OpenAudible Book { get; init; }

    /// <summary>Human readable description used for progress reporting.</summary>
    public required string Label { get; init; }

    /// <summary>Folder to create before copying, or null when there is nothing to copy.</summary>
    public string? TargetDirectory { get; init; }

    public string? AudioSource { get; init; }
    public string? AudioDestination { get; init; }
    public string? PdfSource { get; init; }
    public string? PdfDestination { get; init; }

    /// <summary>Why this book is being skipped, or an issue worth surfacing. Null when all is well.</summary>
    public string? Warning { get; init; }

    public bool HasWork => TargetDirectory is not null && (AudioDestination is not null || PdfDestination is not null);
}
