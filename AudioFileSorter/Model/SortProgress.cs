namespace AudioFileSorter.Model;

public class SortProgressInfo
{
    public int CurrentBook { get; set; }
    public int TotalBooks { get; set; }
    public int CopiedBooks { get; set; }
    public string? CurrentTitle { get; set; }
    public double Percentage { get; set; }
    public bool IsComplete { get; set; }
    public string? Error { get; set; }
}
