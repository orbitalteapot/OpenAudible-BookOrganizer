namespace AudioFileSorter.Model;

public class SortProgressInfo
{
    public int CurrentBook { get; set; }
    public int TotalBooks { get; set; }
    public int CopiedBooks { get; set; }

    /// <summary>Books where an out-of-date file at the destination was replaced. Part of <see cref="CopiedBooks"/>.</summary>
    public int UpdatedBooks { get; set; }

    /// <summary>Books that needed no work: already up to date, or with no matching source file.</summary>
    public int SkippedBooks { get; set; }

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
