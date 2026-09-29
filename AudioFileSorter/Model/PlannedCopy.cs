namespace AudioFileSorter.Model;

/// <summary>
/// One book's share of the work: the exact files to read and the exact paths to write.
/// Produced by <see cref="SortPlanner"/> before any copying starts.
/// </summary>
public sealed record PlannedCopy
{
    public required OpenAudible Book { get; init; }

    /// <summary>The book as a person would name it, for progress and problem reports.</summary>
    public required string Title { get; init; }

    /// <summary>The audio is not in the source folder, so nothing of the book is copied, not even its PDF.</summary>
    public bool IsMissingFromSource { get; init; }

    /// <summary>Folder to create before copying, or null when there is nothing to copy.</summary>
    public string? TargetDirectory { get; init; }

    public string? AudioSource { get; init; }
    public string? AudioDestination { get; init; }

    /// <summary>
    /// A copy of the audio an older version left somewhere else in the destination (loose in the
    /// author or series folder, or filed under the book's old series layout), to be moved to
    /// <see cref="AudioDestination"/> before the update check. Null when there is none.
    /// </summary>
    public string? AudioLegacyPath { get; init; }

    public string? PdfSource { get; init; }
    public string? PdfDestination { get; init; }

    /// <summary>As <see cref="AudioLegacyPath"/>, for the PDF.</summary>
    public string? PdfLegacyPath { get; init; }

    /// <summary>
    /// Why this book cannot be written (when <see cref="HasWork"/> is false), or an issue worth
    /// surfacing about one that can. Null when all is well.
    /// </summary>
    public string? Warning { get; init; }

    public bool HasWork => TargetDirectory is not null && (AudioDestination is not null || PdfDestination is not null);
}
