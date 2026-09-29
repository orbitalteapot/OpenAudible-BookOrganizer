using System.Text.RegularExpressions;

namespace ManagerApi.Services;

/// <summary>The rules for when automatic sorting runs. Pure functions of the settings and the run history.</summary>
public static partial class SortSchedule
{
    /// <summary>
    /// Anything shorter only burns disk reads: a sort over an unchanged library copies nothing, and
    /// OpenAudible does not download books by the minute.
    /// </summary>
    public const int MinimumIntervalMinutes = 15;

    /// <summary>
    /// How soon a failed attempt is tried again. An unplugged drive or a NAS that is still waking up
    /// is usually back within minutes, and waiting a whole day to find out wastes the day.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    public static bool IsValidInterval(int minutes) => minutes >= MinimumIntervalMinutes;

    /// <summary>
    /// When the next automatic run is due, or null when automatic sorting is off.
    ///
    /// Due straight away when it has not been tried since it was turned on, so turning it on and
    /// opening the app after a missed run both sort now. After a success, one interval after that
    /// run started. After a failure, <see cref="RetryDelay"/> after it, but never later than a regular
    /// run still due after that failure.
    ///
    /// A time in the future counts as now (see <see cref="ClampToNow"/>).
    /// </summary>
    public static DateTime? NextRunUtc(int? intervalMinutes, ScheduleState state, DateTime nowUtc)
    {
        if (intervalMinutes is null)
        {
            return null;
        }

        state = ClampToNow(state, nowUtc);
        var lastAttempt = state.LastAttemptUtc;
        if (lastAttempt is null || lastAttempt < state.EnabledAtUtc)
        {
            return nowUtc;
        }

        var normalDue = (state.LastSuccessUtc ?? lastAttempt.Value).AddMinutes(intervalMinutes.Value);
        if (!LastAttemptFailed(state))
        {
            return normalDue;
        }

        // The regular slot only brings a retry forward while it is still ahead of the failed attempt.
        // Once that attempt was the regular run (or came later), the slot is used up; taking it would
        // make the retry due at once, again and again, for as long as the drive stays unplugged.
        var retryDue = lastAttempt.Value + RetryDelay;
        return normalDue > lastAttempt.Value && normalDue < retryDue ? normalDue : retryDue;
    }

    /// <summary>
    /// <paramref name="state"/> with every time later than <paramref name="nowUtc"/> brought back to it.
    ///
    /// A time in the future can only come from a clock that has since been set back. Taken at face
    /// value it would hold the schedule off until then. Counted as "now" afresh on every look it is
    /// no better: the due time slides along with the clock, and an EnabledAtUtc that stays ahead of
    /// every run starts one sort after another. So the scheduler saves the clamped times once, and
    /// the next run is worked out from a fixed point.
    /// </summary>
    public static ScheduleState ClampToNow(ScheduleState state, DateTime nowUtc)
    {
        return state with
        {
            EnabledAtUtc = NotAfter(state.EnabledAtUtc, nowUtc),
            LastAttemptUtc = NotAfter(state.LastAttemptUtc, nowUtc),
            LastSuccessUtc = NotAfter(state.LastSuccessUtc, nowUtc)
        };
    }

    /// <summary>The last automatic attempt did not sort the library, so the next one is a retry.</summary>
    public static bool LastAttemptFailed(ScheduleState state)
    {
        return state.LastAttemptUtc is not null && state.LastAttemptUtc != state.LastSuccessUtc;
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

        if (total > int.MaxValue || !IsValidInterval((int)total))
        {
            return false;
        }

        minutes = (int)total;
        return true;
    }

    private static DateTime? NotAfter(DateTime? value, DateTime nowUtc) => value > nowUtc ? nowUtc : value;

    [GeneratedRegex(@"^(?<amount>\d+)\s*(?<unit>[mhd]?)$")]
    private static partial Regex IntervalPattern();
}
