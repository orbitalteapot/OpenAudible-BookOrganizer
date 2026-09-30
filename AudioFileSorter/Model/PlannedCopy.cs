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

    /// <summary>
    /// Who the book is, whatever its title or file is called: its ASIN, Key or ProductID (the first
    /// the export gives, compared without regard to case), or else "file:" and the name of its audio
    /// file. What the <see cref="LibraryManifest"/> records it under. Null for a book missing from
    /// the source that has none of those.
    /// </summary>
    public string? BookId { get; init; }

    /// <summary>The audio is not in the source folder, so nothing of the book is copied, not even its PDF.</summary>
    public bool IsMissingFromSource { get; init; }

    /// <summary>The book's folder, to create before copying, or null when there is nothing to copy.</summary>
    public string? TargetDirectory { get; init; }

    public string? AudioSource { get; init; }
    public string? AudioDestination { get; init; }

    /// <summary>
    /// A copy of the audio already in the destination, to be moved to <see cref="AudioDestination"/>
    /// before the update check: the book's own file from the folder the manifest records, when the
    /// book has moved (its author, series, number or title changed), or one an older version left loose that
    /// is this book's (see <see cref="SortPlanner"/>). Null when there is none.
    /// </summary>
    public string? AudioMoveFrom { get; init; }

    public string? PdfSource { get; init; }
    public string? PdfDestination { get; init; }

    /// <summary>As <see cref="AudioMoveFrom"/>, for the PDF.</summary>
    public string? PdfMoveFrom { get; init; }

    /// <summary>
    /// Why this book cannot be written (when <see cref="HasWork"/> is false), or an issue worth
    /// surfacing about one that can. Null when all is well.
    /// </summary>
    public string? Warning { get; init; }

    public bool HasWork => TargetDirectory is not null && (AudioDestination is not null || PdfDestination is not null);
}
