namespace AudioFileSorter.Model;

public class SortProgressInfo
{
    public int CurrentBook { get; set; }
    public int TotalBooks { get; set; }
    public int CopiedBooks { get; set; }

    /// <summary>Books where an out-of-date file at the destination was replaced. Part of <see cref="CopiedBooks"/>.</summary>
    public int UpdatedBooks { get; set; }

    /// <summary>Books already present and up to date at the destination, so nothing was written.</summary>
    public int SkippedBooks { get; set; }

    /// <summary>
    /// Books listed in the export with no matching file in the source folder — usually books that
    /// have not been downloaded. Counted apart from <see cref="SkippedBooks"/>, because "not here"
    /// and "already organised" mean very different things to whoever is reading the number.
    /// </summary>
    public int MissingBooks { get; set; }

    /// <summary>Books that could not be processed because of an error.</summary>
    public int FailedBooks { get; set; }

    /// <summary>Number of problems recorded so far. Details are written to the backend log.</summary>
    public int WarningCount { get; set; }

    public string? CurrentTitle { get; set; }
    public double Percentage { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCanceled { get; set; }
    public string? Error { get; set; }
}
