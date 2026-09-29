using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>What the settings file holds.</summary>
public sealed record SavedState(AppSettings Settings, ScheduleState Schedule);

/// <summary>
/// Keeps the settings and the schedule history in one JSON file, so they survive a restart:
/// <c>{ "version": 1, "settings": {...}, "schedule": {...} }</c>. With no path it keeps nothing.
///
/// A file that cannot be read or written is logged rather than allowed to stop the app, and a value
/// in it that makes no sense falls back to its default on its own, so one bad entry (a hand edit, a
/// newer version's value) does not throw away the rest. A file that cannot be read at all is moved
/// aside before anything saves the defaults over it (see <see cref="SetAsideUnreadableFile"/>).
/// </summary>
public sealed class SettingsStore(string? path, ILogger<SettingsStore> logger)
{
    private const int FileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string? _path = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>The file could not be read and could not be moved aside, so it must not be saved over.</summary>
    private bool _keepUnreadableFile;

    /// <summary>
    /// What happened to a settings file that could not be read at startup, worded for the user, or
    /// null when it was read (or there was none).
    /// </summary>
    public string? LoadWarning { get; private set; }

    /// <summary>The saved state, with the defaults for anything the file does not hold, or holds wrongly.</summary>
    public SavedState Load()
    {
        var defaults = new AppSettings();
        var empty = new SavedState(defaults, new ScheduleState());
        if (_path is null || !File.Exists(_path))
        {
            return empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            // "null", "[]" or a bare string parse fine, but have no properties to look up.
            if (root.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning(
                    "The settings file at {Path} does not hold settings ({Kind}); starting with the default settings",
                    _path, root.ValueKind);
                SetAsideUnreadableFile();
                return empty;
            }

            return new SavedState(
                root.TryGetProperty("settings", out var settings) ? ReadSettings(settings, defaults) : defaults,
                root.TryGetProperty("schedule", out var schedule) ? ReadSchedule(schedule) : new ScheduleState());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the settings file at {Path}; starting with the default settings", _path);
            SetAsideUnreadableFile();
            return empty;
        }
    }

    /// <summary>
    /// Keeps a settings file that could not be read out of the way of the next save, which would
    /// otherwise write the defaults over every path and choice in it, lost to one hand-edited comma
    /// or a disk that was busy at startup. When it cannot even be moved, it is left where it is and
    /// saving is refused instead.
    /// </summary>
    private void SetAsideUnreadableFile()
    {
        var keptPath = $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(_path!, keptPath);
            logger.LogWarning("Kept the unreadable settings file as {KeptPath}", keptPath);
            LoadWarning =
                $"The saved settings could not be read, so the app started with the default settings. The old file was kept as {keptPath}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not move the unreadable settings file at {Path} aside; it will not be saved over", _path);
            _keepUnreadableFile = true;
            LoadWarning =
                $"The saved settings in {_path} could not be read, so the app is using the default settings and will not save over that file. Fix or remove it, then restart the app.";
        }
    }

    /// <summary>
    /// Written beside the target, flushed to the disk and renamed over it, so a crash or a power cut
    /// leaves either the old file or the new one, never half of one.
    /// </summary>
    /// <param name="error">Why it could not be saved (the system's reason, which names the file); null when it was.</param>
    public bool TrySave(SavedState state, out string? error)
    {
        error = null;
        if (_path is null)
        {
            return true;
        }

        if (_keepUnreadableFile)
        {
            error = $"{_path} could not be read when the app started, and is left as it is so nothing in it is lost";
            return false;
        }

        var partialPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            var file = new { version = FileVersion, settings = StoredSettings.From(state.Settings), schedule = state.Schedule };
            using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the settings to {Path}", _path);
            error = ex.Message;
            return false;
        }
    }

    private AppSettings ReadSettings(JsonElement element, AppSettings defaults)
    {
        StoredSettings? stored;
        try
        {
            stored = element.Deserialize<StoredSettings>(JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The saved settings in {Path} could not be read; using the defaults", _path);
            return defaults;
        }

        if (stored is null)
        {
            return defaults;
        }

        FileComparisonMode? comparisonMode = null;
        if (stored.ComparisonMode is not null)
        {
            if (SortOptions.TryParseComparisonMode(stored.ComparisonMode, out var parsed))
            {
                comparisonMode = parsed;
            }
            else
            {
                Ignored("comparisonMode", stored.ComparisonMode);
            }
        }

        if (!SortOptions.TryParseCopySpeed(stored.CopySpeed, out var copySpeed))
        {
            Ignored("copySpeed", stored.CopySpeed);
        }

        var interval = stored.ScheduleIntervalMinutes;
        if (interval is not null && !SortSchedule.IsValidInterval(interval.Value))
        {
            Ignored("scheduleIntervalMinutes", interval.ToString());
            interval = null;
        }

        return new AppSettings
        {
            CsvPath = stored.CsvPath,
            SourcePath = stored.SourcePath,
            DestinationPath = stored.DestinationPath,
            ComparisonMode = comparisonMode,
            CopySpeed = copySpeed,
            ScheduleIntervalMinutes = interval,
            KeepRunningInBackground = stored.KeepRunningInBackground ?? defaults.KeepRunningInBackground,
            OpenAtLogin = stored.OpenAtLogin ?? defaults.OpenAtLogin
        };
    }

    private ScheduleState ReadSchedule(JsonElement element)
    {
        try
        {
            return element.Deserialize<ScheduleState>(JsonOptions) ?? new ScheduleState();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The automatic sorting history in {Path} could not be read; starting it afresh", _path);
            return new ScheduleState();
        }
    }

    private void Ignored(string setting, string? value)
    {
        logger.LogWarning("Ignoring the saved {Setting} \"{Value}\" in {Path}; using the default", setting, value, _path);
    }

    /// <summary>
    /// The settings as written to the file. The choices are kept as their wire spelling ("full",
    /// "gentle") and parsed on the way back in, so an unknown value is caught by the same rules the
    /// API applies instead of failing the whole file. The switches are nullable for the same reason:
    /// a <c>null</c> there falls back to off instead of failing the whole file.
    /// </summary>
    private sealed record StoredSettings(
        string? CsvPath,
        string? SourcePath,
        string? DestinationPath,
        string? ComparisonMode,
        string? CopySpeed,
        int? ScheduleIntervalMinutes,
        bool? KeepRunningInBackground,
        bool? OpenAtLogin)
    {
        public static StoredSettings From(AppSettings settings)
        {
            return new StoredSettings(
                settings.CsvPath,
                settings.SourcePath,
                settings.DestinationPath,
                settings.ComparisonMode is { } mode ? SortOptions.ToWireValue(mode) : null,
                SortOptions.ToWireValue(settings.CopySpeed),
                settings.ScheduleIntervalMinutes,
                settings.KeepRunningInBackground,
                settings.OpenAtLogin);
        }
    }
}
