using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Owns the settings and the schedule history: what is saved, and what is in force once the
/// server's environment has had its say.
/// </summary>
public sealed class SettingsService
{
    private readonly ServerConfig _config;
    private readonly SettingsStore _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    /// <summary>What the user chose. Never holds a value that came from the environment.</summary>
    private AppSettings _saved;

    private ScheduleState _schedule;

    public SettingsService(ServerConfig config, SettingsStore store, TimeProvider time)
    {
        _config = config;
        _store = store;
        _time = time;

        var state = store.Load(new AppSettings { ComparisonMode = config.DefaultComparisonMode });
        _saved = state.Settings;
        _schedule = state.Schedule;
    }

    /// <summary>Raised after a change is saved, so automatic sorting can pick up a new interval now.</summary>
    public event EventHandler? Changed;

    public ServerConfig Config => _config;

    /// <summary>
    /// The settings in force: the saved ones, with the paths and the interval replaced by the
    /// environment's wherever it sets them.
    /// </summary>
    public AppSettings Effective
    {
        get
        {
            lock (_lock)
            {
                return Overlay(_saved);
            }
        }
    }

    public ScheduleState Schedule
    {
        get
        {
            lock (_lock)
            {
                return _schedule;
            }
        }
    }

    /// <summary>
    /// The options a run uses under the current settings. Shared by manual and automatic runs, so a
    /// run behaves the same whichever way it was started.
    /// </summary>
    /// <param name="comparisonMode">An update check for this run only, or null for the saved one.</param>
    public SortOptions SortOptionsFor(bool createDestination, FileComparisonMode? comparisonMode = null)
    {
        var settings = Effective;
        return new SortOptions
        {
            ComparisonMode = comparisonMode ?? settings.ComparisonMode,
            MaxParallelism = SortOptions.ParallelismFor(settings.CopySpeed, _config.NormalParallelism),
            CreateDestination = createDestination
        };
    }

    /// <summary>Applies <paramref name="patch"/> and saves it, or refuses it with a reason for the user.</summary>
    public bool TryUpdate(AppSettingsPatch patch, out SettingsError? error)
    {
        lock (_lock)
        {
            error = Check(patch, out var next);
            if (error is not null)
            {
                return false;
            }

            if (Overlay(_saved).ScheduleIntervalMinutes is null && Overlay(next).ScheduleIntervalMinutes is not null)
            {
                _schedule = _schedule with { EnabledAtUtc = _time.GetUtcNow().UtcDateTime };
            }

            _saved = next;
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Records how an automatic run went.</summary>
    public void UpdateSchedule(Func<ScheduleState, ScheduleState> update)
    {
        lock (_lock)
        {
            _schedule = update(_schedule);
            Save();
        }
    }

    private SettingsError? Check(AppSettingsPatch patch, out AppSettings next)
    {
        var current = Overlay(_saved);
        next = _saved;

        var lockedPath =
            LockedPathChange(SortPathField.Csv, patch.CsvPath, current.CsvPath) ??
            LockedPathChange(SortPathField.Source, patch.SourcePath, current.SourcePath) ??
            LockedPathChange(SortPathField.Destination, patch.DestinationPath, current.DestinationPath);
        if (lockedPath is not null)
        {
            return lockedPath;
        }

        if (patch.HasScheduleIntervalMinutes && patch.ScheduleIntervalMinutes != current.ScheduleIntervalMinutes)
        {
            if (_config.ScheduleLocked)
            {
                return new SettingsError(
                    $"Automatic sorting is set by the server's {ServerConfig.ScheduleVariable} setting and cannot be changed here.",
                    "scheduleIntervalMinutes");
            }

            if (patch.ScheduleIntervalMinutes is { } minutes && !SortSchedule.IsValidInterval(minutes))
            {
                return new SettingsError(
                    $"Sort at most every {SortSchedule.MinimumIntervalMinutes} minutes.", "scheduleIntervalMinutes");
            }
        }

        var comparisonMode = current.ComparisonMode;
        if (patch.ComparisonMode is not null && !SortOptions.TryParseComparisonMode(patch.ComparisonMode, out comparisonMode))
        {
            return new SettingsError($"Unknown update check \"{patch.ComparisonMode}\". Use \"quick\" or \"full\".", "comparisonMode");
        }

        var copySpeed = current.CopySpeed;
        if (patch.CopySpeed is not null && !SortOptions.TryParseCopySpeed(patch.CopySpeed, out copySpeed))
        {
            return new SettingsError($"Unknown copy speed \"{patch.CopySpeed}\". Use \"normal\" or \"gentle\".", "copySpeed");
        }

        next = _saved with
        {
            // A locked path can only get here unchanged; the saved value is kept for when the lock goes.
            CsvPath = _config.PathsLocked ? _saved.CsvPath : Cleared(patch.CsvPath, _saved.CsvPath),
            SourcePath = _config.PathsLocked ? _saved.SourcePath : Cleared(patch.SourcePath, _saved.SourcePath),
            DestinationPath = _config.PathsLocked ? _saved.DestinationPath : Cleared(patch.DestinationPath, _saved.DestinationPath),
            ComparisonMode = comparisonMode,
            CopySpeed = copySpeed,
            ScheduleIntervalMinutes = patch.HasScheduleIntervalMinutes && !_config.ScheduleLocked
                ? patch.ScheduleIntervalMinutes
                : _saved.ScheduleIntervalMinutes,
            KeepRunningInBackground = patch.KeepRunningInBackground ?? _saved.KeepRunningInBackground,
            OpenAtLogin = patch.OpenAtLogin ?? _saved.OpenAtLogin
        };

        return CheckPaths(current, Overlay(next));
    }

    /// <summary>
    /// Copying a library into itself never ends, so that is refused whatever else is going on.
    ///
    /// While automatic sorting is on, the paths must also work right now — including a destination
    /// that exists and can be written, because an unattended run never creates it. That is only
    /// checked when the paths or the interval change: an unplugged drive should not stop someone
    /// changing the copy speed.
    /// </summary>
    private static SettingsError? CheckPaths(AppSettings current, AppSettings next)
    {
        if (SortPathValidator.InspectDestination(next.SourcePath, next.DestinationPath) is
            { Code: SortPathProblemCode.DestinationInsideSource } overlap)
        {
            return ToError(overlap);
        }

        var pathsOrIntervalChanged =
            !SamePath(current.CsvPath, next.CsvPath) ||
            !SamePath(current.SourcePath, next.SourcePath) ||
            !SamePath(current.DestinationPath, next.DestinationPath) ||
            current.ScheduleIntervalMinutes != next.ScheduleIntervalMinutes;

        if (next.ScheduleIntervalMinutes is null || !pathsOrIntervalChanged)
        {
            return null;
        }

        var problem = SortPathValidator.Validate(next.CsvPath ?? "", next.SourcePath, next.DestinationPath, createDestination: false);
        return problem is null ? null : ToError(problem);
    }

    private SettingsError? LockedPathChange(SortPathField field, string? requested, string? current)
    {
        if (!_config.PathsLocked || requested is null || SamePath(requested, current))
        {
            return null;
        }

        return new SettingsError(
            $"This path is set by the server's {ServerConfig.VariableFor(field)} setting and cannot be changed here.",
            RunErrors.Field(field));
    }

    private AppSettings Overlay(AppSettings saved)
    {
        var settings = saved;
        if (_config.PathsLocked)
        {
            settings = settings with
            {
                CsvPath = _config.CsvPath,
                SourcePath = _config.SourcePath,
                DestinationPath = _config.DestinationPath
            };
        }

        return _config.ScheduleLocked
            ? settings with { ScheduleIntervalMinutes = _config.ScheduleIntervalMinutes }
            : settings;
    }

    private void Save() => _store.Save(new SavedState(_saved, _schedule));

    /// <summary>A path from a patch: null leaves it as it was, blank clears it.</summary>
    private static string? Cleared(string? requested, string? saved)
    {
        if (requested is null)
        {
            return saved;
        }

        return string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
    }

    private static bool SamePath(string? a, string? b)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.Ordinal);
    }

    private static SettingsError ToError(SortPathProblem problem) => new(problem.Message, RunErrors.Field(problem.Field));
}
