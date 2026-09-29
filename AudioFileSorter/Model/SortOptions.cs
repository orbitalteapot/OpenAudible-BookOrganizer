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

/// <summary>How hard a sort may work the disks it copies between.</summary>
public enum CopySpeed
{
    /// <summary>Several books at once; how many is up to the host (see <see cref="SortOptions.ParallelismFor"/>).</summary>
    Normal = 0,

    /// <summary>
    /// One book at a time. Network shares and USB disks slow down when several copies compete for
    /// them, and a NAS that is also serving other people stays usable.
    /// </summary>
    Gentle = 1
}

/// <summary>Per-run settings for a sort.</summary>
public sealed record SortOptions
{
    // Declared before Default: static fields initialise in textual order.

    /// <summary>
    /// Books copied at once by default. Copying is bound by the slower of the two volumes, and on a
    /// network share or a spinning disk more concurrency makes throughput worse, not better.
    /// </summary>
    public static readonly int DefaultParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 8);

    /// <summary>The settings used when a caller does not supply any.</summary>
    public static readonly SortOptions Default = new();

    private readonly int _maxParallelism = DefaultParallelism;

    /// <summary>How to decide whether an existing destination file is out of date.</summary>
    public FileComparisonMode ComparisonMode { get; init; } = FileComparisonMode.Quick;

    /// <summary>How many books are copied at once. At least 1.</summary>
    public int MaxParallelism
    {
        get => _maxParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxParallelism = value;
        }
    }

    /// <summary>
    /// Create the destination folder when it does not exist. Off by default, and only ever turned
    /// on when a person asked for it: a missing destination usually means an unplugged drive or an
    /// unmounted share, and creating it would quietly fill the internal disk instead.
    /// </summary>
    public bool CreateDestination { get; init; }

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

    /// <summary>
    /// Parses a copy speed from a request body or the settings file. Blank means the default;
    /// anything unrecognised is rejected, for the same reason as <see cref="TryParseComparisonMode"/>.
    /// </summary>
    public static bool TryParseCopySpeed(string? value, out CopySpeed speed)
    {
        speed = CopySpeed.Normal;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "normal":
                return true;

            case "gentle":
                speed = CopySpeed.Gentle;
                return true;

            default:
                return false;
        }
    }

    /// <summary>The canonical spelling of a copy speed, as used by the API and the UI.</summary>
    public static string ToWireValue(CopySpeed speed)
    {
        return speed == CopySpeed.Gentle ? "gentle" : "normal";
    }

    /// <summary>Books copied at once for <paramref name="speed"/>, given what Normal means on this server.</summary>
    public static int ParallelismFor(CopySpeed speed, int normalParallelism)
    {
        return speed == CopySpeed.Gentle ? 1 : Math.Max(1, normalParallelism);
    }
}
