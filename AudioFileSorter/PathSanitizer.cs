using System.Text;

namespace AudioFileSorter;

/// <summary>
/// Turns free-form book metadata into path segments that are safe on every filesystem the
/// organiser writes to. Sanitising against the strictest rule set (Windows) on every platform
/// keeps a library portable: a destination folder is very often an SMB share or an external
/// drive that is later read from a different operating system.
/// </summary>
public static class PathSanitizer
{
    /// <summary>Maximum number of characters allowed in a single path segment.</summary>
    public const int MaxSegmentLength = 200;

    /// <summary>Maximum number of UTF-8 bytes allowed in a single path segment.</summary>
    public const int MaxSegmentBytes = 200;

    private static readonly string[] PlaceholderValues = ["unknown", "n/a", "na", "none", "null"];
    private static readonly string[] LeadingArticles = ["the ", "a ", "an "];
    private static readonly string[] SeriesDecorators = [" series", " saga", " cycle"];

    // Invalid on Windows even when the app is running on Linux or macOS.
    private static readonly char[] CrossPlatformInvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly char[] PlatformInvalidChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Converts a metadata value into a single safe path segment, or <c>null</c> when the value
    /// carries no usable information (empty, a placeholder such as "Unknown", or nothing but
    /// characters that had to be stripped).
    /// </summary>
    public static string? SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                // Control characters (including embedded newlines from multi-line CSV cells)
                // become a space so words do not get glued together.
                builder.Append(' ');
                continue;
            }

            if (Array.IndexOf(CrossPlatformInvalidChars, ch) >= 0 ||
                Array.IndexOf(PlatformInvalidChars, ch) >= 0)
            {
                continue;
            }

            builder.Append(ch);
        }

        var normalized = CollapseWhitespace(builder.ToString());
        normalized = TrimSegment(normalized);

        if (normalized.Length == 0 || normalized is "." or ".." || IsPlaceholder(normalized))
        {
            return null;
        }

        normalized = TrimSegment(Truncate(normalized, MaxSegmentLength, MaxSegmentBytes));
        if (normalized.Length == 0)
        {
            return null;
        }

        // CON, LPT1 and friends cannot be used as a file or folder name on Windows, with or
        // without an extension, and fail with a confusing "path not supported" error.
        return IsReservedDeviceName(normalized) ? $"_{normalized}" : normalized;
    }

    /// <summary>
    /// Sanitises a value, falling back to the first usable alternative and finally to
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string SanitizeSegmentOrFallback(string fallback, params string?[] values)
    {
        foreach (var value in values)
        {
            var sanitized = SanitizeSegment(value);
            if (sanitized is not null)
            {
                return sanitized;
            }
        }

        return fallback;
    }

    public static bool IsPlaceholder(string value)
    {
        return Array.IndexOf(PlaceholderValues, value.Trim().ToLowerInvariant()) >= 0;
    }

    /// <summary>
    /// Key used to decide whether two author (or file) names refer to the same thing, so that
    /// "J.K. Rowling", "JK Rowling" and "J K Rowling" all land in one folder.
    /// </summary>
    public static string NormalizeComparisonKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return new string(normalized.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Comparison key for series names, ignoring a leading article and a trailing decorator so
    /// that "The Wheel of Time" and "Wheel of Time Series" resolve to the same folder.
    /// </summary>
    public static string NormalizeSeriesKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();

        foreach (var article in LeadingArticles)
        {
            if (normalized.StartsWith(article, StringComparison.Ordinal))
            {
                normalized = normalized[article.Length..];
                break;
            }
        }

        foreach (var decorator in SeriesDecorators)
        {
            if (normalized.EndsWith(decorator, StringComparison.Ordinal))
            {
                normalized = normalized[..^decorator.Length];
                break;
            }
        }

        return new string(normalized.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> resolves to a location strictly inside
    /// <paramref name="root"/>. The last line of defence against a metadata value escaping the
    /// destination folder.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string fullRoot;
        string fullCandidate;
        try
        {
            fullRoot = Path.GetFullPath(root);
            fullCandidate = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (fullRoot.Length == 0)
        {
            return false;
        }

        if (fullRoot[^1] != Path.DirectorySeparatorChar)
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        return fullCandidate.Length > fullRoot.Length &&
               fullCandidate.StartsWith(fullRoot, PathComparison);
    }

    /// <summary>
    /// Shortens a value so it fits within both the character and UTF-8 byte budget of a path
    /// segment, without splitting a surrogate pair.
    /// </summary>
    public static string Truncate(string value, int maxChars, int maxBytes)
    {
        var result = value.Length > maxChars ? value[..maxChars] : value;

        if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
        {
            result = result[..^1];
        }

        while (result.Length > 0 && Encoding.UTF8.GetByteCount(result) > maxBytes)
        {
            result = result[..^1];
            if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
            {
                result = result[..^1];
            }
        }

        return result;
    }

    private static string TrimSegment(string value)
    {
        // Windows silently drops trailing dots and spaces, which turns "Vol. 2 ." into a name
        // that never matches on a later run.
        return value.Trim().TrimEnd('.', ' ').Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsReservedDeviceName(string value)
    {
        var stem = value;
        var dotIndex = stem.IndexOf('.');
        if (dotIndex > 0)
        {
            stem = stem[..dotIndex];
        }

        return ReservedDeviceNames.Contains(stem.TrimEnd(' '));
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++)
        {
            names.Add($"COM{i}");
            names.Add($"LPT{i}");
        }

        return names;
    }
}
