namespace AudioFileSorter.Model;

/// <summary>
/// The sanitised names a single book will be filed under. Built once per book and never mutated,
/// so planning can be reasoned about (and tested) without touching the filesystem.
/// </summary>
public sealed record BookSortPlan
{
    /// <summary>Author folder name. Never null or empty.</summary>
    public required string Author { get; init; }

    /// <summary>Series folder name, or null when the book is not part of a series.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Sequence within the series, or null. Only set when <see cref="SeriesName"/> is set.</summary>
    public string? SeriesSequence { get; init; }

    /// <summary>File name without extension. Never null or empty.</summary>
    public required string FileStem { get; init; }
}
