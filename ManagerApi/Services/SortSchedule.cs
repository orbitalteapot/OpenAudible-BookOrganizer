using System.Text.RegularExpressions;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>When to sort automatically, what to sort, and how the last automatic run went.</summary>
public sealed partial record SortSchedule
{
    /// <summary>
    /// Anything shorter only burns disk reads: a sort over an unchanged library copies nothing, and
    /// OpenAudible does not download books by the minute.
    /// </summary>
    public const int MinimumIntervalMinutes = 15;

    /// <summary>Minutes between runs. Null means automatic sorting is off.</summary>
    public int? IntervalMinutes { get; init; }

    public string? CsvPath { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public FileComparisonMode ComparisonMode { get; init; } = SortOptions.Default.ComparisonMode;

    public DateTime? LastRunUtc { get; init; }

    /// <summary>One line describing how the last automatic run ended.</summary>
    public string? LastResult { get; init; }

    public bool IsEnabled =>
        IntervalMinutes is not null &&
        !string.IsNullOrWhiteSpace(CsvPath) &&
        !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(DestinationPath);

    /// <summary>
    /// When the next run is due. A schedule that has never run, or whose run was missed while the
    /// app was closed, is due straight away, so opening the app catches up.
    /// </summary>
    public DateTime? NextRunUtc(DateTime nowUtc)
    {
        if (!IsEnabled)
        {
            return null;
        }

        return LastRunUtc is null ? nowUtc : LastRunUtc.Value.AddMinutes(IntervalMinutes!.Value);
    }

    /// <summary>
    /// Parses an interval such as "30m", "6h" or "1d"; a bare number is hours. Blank, "0" and "off"
    /// turn automatic sorting off. Anything else, or anything under
    /// <see cref="MinimumIntervalMinutes"/>, is rejected rather than guessed at.
    /// </summary>
    public static bool TryParseInterval(string? value, out int? minutes)
    {
        minutes = null;
        var trimmed = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(trimmed) || trimmed is "0" or "off" or "never" or "disabled")
        {
            return true;
        }

        var match = IntervalPattern().Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["amount"].Value, out var amount))
        {
            return false;
        }

        var total = match.Groups["unit"].Value switch
        {
            "m" => (long)amount,
            "d" => amount * 1440L,
            _ => amount * 60L
        };

        if (total < MinimumIntervalMinutes || total > int.MaxValue)
        {
            return false;
        }

        minutes = (int)total;
        return true;
    }

    [GeneratedRegex(@"^(?<amount>\d+)\s*(?<unit>[mhd]?)$")]
    private static partial Regex IntervalPattern();
}
