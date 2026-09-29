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

    /// <summary>Why the last save failed, or null when it worked.</summary>
    private string? _saveProblem;

    public SettingsService(ServerConfig config, SettingsStore store, TimeProvider time)
    {
        _config = config;
        _store = store;
        _time = time;

        var state = store.Load();
        _saved = state.Settings;
        _schedule = state.Schedule;
    }

    /// <summary>Raised after a change is saved, so automatic sorting can pick up a new interval now.</summary>
    public event EventHandler? Changed;

    public ServerConfig Config => _config;

    /// <summary>
    /// Why the settings could not be saved last time, worded for the user, or null when they were.
    /// Automatic runs record their history without anyone watching, so this is how a read-only or
    /// full disk reaches the page.
    /// </summary>
    public string? SaveWarning
    {
        get
        {
            lock (_lock)
            {
                return _saveProblem is null
                    ? null
                    : $"The settings could not be saved, so changes will be forgotten when the app restarts: {_saveProblem}";
            }
        }
    }

    /// <summary>
    /// Everything the page should warn about the settings: the environment's, what happened to a
    /// settings file that could not be read at startup, and the last failed save.
    /// </summary>
    public IReadOnlyList<string> Warnings =>
        [.. _config.Warnings, .. new[] { _store.LoadWarning, SaveWarning }.OfType<string>()];

    /// <summary>
    /// The settings in force: the saved ones, with the paths and the interval replaced by the
    /// environment's wherever it sets them, and the environment's update check until one is picked.
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
            ComparisonMode = comparisonMode ?? settings.ComparisonMode!.Value,
            MaxParallelism = SortOptions.ParallelismFor(settings.CopySpeed, _config.NormalParallelism),

            // Paths set by the server are container mounts: a missing destination is a mount that was
            // forgotten or mistyped, and creating it would copy the library into the container, to be
            // lost when it is recreated (see the Dockerfile). Enforced here, not only on the page, so
            // no client can ask for it.
            CreateDestination = createDestination && !_config.PathsLocked
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

            var schedule = Overlay(_saved).ScheduleIntervalMinutes is null && Overlay(next).ScheduleIntervalMinutes is not null
                ? _schedule with { EnabledAtUtc = _time.GetUtcNow().UtcDateTime }
                : _schedule;

            // Kept only once it is on disk: a change that is reported as saved but gone after a
            // restart is worse than one that is refused with the reason.
            if (!TrySave(next, schedule))
            {
                error = new SettingsError($"Could not save the settings: {_saveProblem}", null);
                return false;
            }

            _saved = next;
            _schedule = schedule;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Records how an automatic run went. Kept in memory even when it cannot be saved, or the
    /// schedule would run the same slot again; <see cref="SaveWarning"/> tells the user.
    /// </summary>
    public void UpdateSchedule(Func<ScheduleState, ScheduleState> update)
    {
        lock (_lock)
        {
            _schedule = update(_schedule);
            TrySave(_saved, _schedule);
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

        var comparisonMode = _saved.ComparisonMode;
        if (patch.ComparisonMode is not null)
        {
            if (!SortOptions.TryParseComparisonMode(patch.ComparisonMode, out var picked))
            {
                return new SettingsError($"Unknown update check \"{patch.ComparisonMode}\". Use \"quick\" or \"full\".", "comparisonMode");
            }

            comparisonMode = picked;
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
    private SettingsError? CheckPaths(AppSettings current, AppSettings next)
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

        settings = settings with { ComparisonMode = saved.ComparisonMode ?? _config.DefaultComparisonMode };

        return _config.ScheduleLocked
            ? settings with { ScheduleIntervalMinutes = _config.ScheduleIntervalMinutes }
            : settings;
    }

    private bool TrySave(AppSettings settings, ScheduleState schedule)
    {
        var saved = _store.TrySave(new SavedState(settings, schedule), out var problem);
        _saveProblem = problem;
        return saved;
    }

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

    private SettingsError ToError(SortPathProblem problem) =>
        new(_config.Explain(problem).Message, RunErrors.Field(problem.Field), RunErrors.Code(problem));
}
