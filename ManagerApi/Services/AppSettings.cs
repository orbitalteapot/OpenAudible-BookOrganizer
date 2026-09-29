using System.Text.Json.Serialization;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// The choices a person makes in the app. The one copy of them: manual and automatic runs both read
/// these, so the Sort page and the schedule can never disagree about what a sort does.
/// </summary>
public sealed record AppSettings
{
    public string? CsvPath { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }

    /// <summary>
    /// The update check a person picked. Null until they pick one, so the server's COMPARISON_MODE
    /// keeps deciding (and can still be changed) until then; <see cref="SettingsService.Effective"/>
    /// always fills it in.
    /// </summary>
    public FileComparisonMode? ComparisonMode { get; init; }

    public CopySpeed CopySpeed { get; init; } = CopySpeed.Normal;

    /// <summary>Minutes between automatic sorts. Null means automatic sorting is off.</summary>
    public int? ScheduleIntervalMinutes { get; init; }

    /// <summary>Desktop only: stay in the tray when the window is closed, so automatic sorts keep happening.</summary>
    public bool KeepRunningInBackground { get; init; }

    /// <summary>Desktop only: start hidden in the tray when the user signs in.</summary>
    public bool OpenAtLogin { get; init; }
}

/// <summary>
/// A change to some of the settings, as sent by PUT /api/settings. A property left out of the body
/// is left alone. For the paths an empty string clears them; null means "not sent".
/// </summary>
public sealed record AppSettingsPatch
{
    private readonly int? _scheduleIntervalMinutes;

    public string? CsvPath { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }

    /// <summary>"quick" or "full".</summary>
    public string? ComparisonMode { get; init; }

    /// <summary>"normal" or "gentle".</summary>
    public string? CopySpeed { get; init; }

    /// <summary>
    /// Null is a real value here (automatic sorting off), so whether the property was sent at all is
    /// tracked separately: the serializer only calls this setter for properties present in the body.
    /// </summary>
    public int? ScheduleIntervalMinutes
    {
        get => _scheduleIntervalMinutes;
        init
        {
            _scheduleIntervalMinutes = value;
            HasScheduleIntervalMinutes = true;
        }
    }

    [JsonIgnore]
    public bool HasScheduleIntervalMinutes { get; private init; }

    public bool? KeepRunningInBackground { get; init; }
    public bool? OpenAtLogin { get; init; }
}

/// <summary>Why a settings change was refused, worded for the user.</summary>
/// <param name="Field">The setting at fault, as named on the wire ("csvPath"), or null when it is not one field.</param>
public sealed record SettingsError(string Message, string? Field);

/// <summary>The history automatic sorting keeps. Its timing lives in <see cref="AppSettings"/>.</summary>
public sealed record ScheduleState
{
    /// <summary>When automatic sorting was last turned on. A run is due at once if none was tried since.</summary>
    public DateTime? EnabledAtUtc { get; init; }

    /// <summary>When the last automatic run was tried, whether or not it worked.</summary>
    public DateTime? LastAttemptUtc { get; init; }

    /// <summary>When the last automatic run that sorted the library started.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>How the last automatic run went.</summary>
    public RunRecord? LastRun { get; init; }
}

/// <summary>
/// A finished run, kept as numbers rather than a sentence so the page words it the same way it
/// words a run it watched.
/// </summary>
public sealed record RunRecord(
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    RunTrigger? Trigger,
    SortCounts Counts,
    int ProblemCount,
    bool IsCanceled,
    string? Error,
    string? ErrorCode)
{
    public static RunRecord From(RunStatus status)
    {
        return new RunRecord(
            status.StartedUtc,
            status.FinishedUtc,
            status.Trigger,
            status.Counts,
            status.ProblemCount,
            status.IsCanceled,
            status.Error,
            status.ErrorCode);
    }
}
