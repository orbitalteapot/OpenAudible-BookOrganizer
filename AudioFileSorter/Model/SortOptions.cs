namespace AudioFileSorter.Model;

/// <summary>
/// How an already-present destination file is checked against its source before the organiser
/// decides whether to replace it.
/// </summary>
public enum FileComparisonMode
{
    /// <summary>
    /// Size plus three sampled 4 KB chunks (start, middle, end). Fast enough to run over a large
    /// library on every sort, and it catches any re-release that changed the file's length — which
    /// is nearly all of them. It can miss an edit that kept the exact same size and left those
    /// three windows untouched.
    /// </summary>
    Quick = 0,

    /// <summary>
    /// Byte-for-byte comparison of the whole file. Replaces the destination whenever the source
    /// differs at all, at the cost of reading both files in full. The right choice when authors
    /// re-issue books and you would rather pay the I/O than keep a stale copy.
    /// </summary>
    Full = 1
}

/// <summary>Per-run settings for a sort.</summary>
public sealed record SortOptions
{
    /// <summary>The settings used when a caller does not supply any.</summary>
    public static readonly SortOptions Default = new();

    /// <summary>How to decide whether an existing destination file is out of date.</summary>
    public FileComparisonMode ComparisonMode { get; init; } = FileComparisonMode.Quick;

    /// <summary>
    /// Parses a mode from configuration or a request body. A missing or blank value means "use the
    /// default", so callers can treat null as valid; anything unrecognised is rejected rather than
    /// silently downgraded, since quietly running the wrong mode is exactly what this setting
    /// exists to prevent.
    /// </summary>
    public static bool TryParseComparisonMode(string? value, out FileComparisonMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = Default.ComparisonMode;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "quick":
            case "fast":
            case "sampled":
                mode = FileComparisonMode.Quick;
                return true;

            case "full":
            case "verify":
            case "exact":
            case "content":
                mode = FileComparisonMode.Full;
                return true;

            default:
                mode = Default.ComparisonMode;
                return false;
        }
    }

    /// <summary>The canonical spelling of a mode, as used by the API and the UI.</summary>
    public static string ToWireValue(FileComparisonMode mode)
    {
        return mode == FileComparisonMode.Full ? "full" : "quick";
    }
}
