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

            // "null", "[]" or a bare string parse fine, but have no settings to look up one at a
            // time; nor does a "settings" entry that is not an object.
            var settings = default(JsonElement);
            var hasSettings = root.ValueKind == JsonValueKind.Object && TryGetValue(root, "settings", out settings);
            if (root.ValueKind != JsonValueKind.Object || (hasSettings && settings.ValueKind != JsonValueKind.Object))
            {
                logger.LogWarning(
                    "The settings file at {Path} does not hold settings ({Kind}); starting with the default settings",
                    _path, hasSettings ? settings.ValueKind : root.ValueKind);
                SetAsideUnreadableFile();
                return empty;
            }

            return new SavedState(
                hasSettings ? ReadSettings(settings, defaults) : defaults,
                TryGetValue(root, "schedule", out var schedule) ? ReadSchedule(schedule) : new ScheduleState());
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

            // The system's reason names the file; the person also needs to know which folder to fix.
            error = $"{ex.Message.TrimEnd('.')}. Check that the folder {Path.GetDirectoryName(Path.GetFullPath(_path))} can be written.";
            return false;
        }
    }

    /// <summary>
    /// Reads each setting on its own, so one of the wrong kind (a switch saved as "true", an interval
    /// saved as 1440.0) falls back to its default without taking the paths and every other choice
    /// with it.
    /// </summary>
    private AppSettings ReadSettings(JsonElement element, AppSettings defaults)
    {
        FileComparisonMode? comparisonMode = null;
        var comparisonText = ReadString(element, "comparisonMode");
        if (comparisonText is not null)
        {
            if (SortOptions.TryParseComparisonMode(comparisonText, out var parsed))
            {
                comparisonMode = parsed;
            }
            else
            {
                Ignored("comparisonMode", comparisonText);
            }
        }

        var copySpeedText = ReadString(element, "copySpeed");
        if (!SortOptions.TryParseCopySpeed(copySpeedText, out var copySpeed))
        {
            Ignored("copySpeed", copySpeedText);
        }

        var interval = ReadInt(element, "scheduleIntervalMinutes");
        if (interval is not null && !SortSchedule.IsValidInterval(interval.Value))
        {
            Ignored("scheduleIntervalMinutes", interval.ToString());
            interval = null;
        }

        return new AppSettings
        {
            CsvPath = ReadString(element, "csvPath"),
            SourcePath = ReadString(element, "sourcePath"),
            DestinationPath = ReadString(element, "destinationPath"),
            ComparisonMode = comparisonMode,
            CopySpeed = copySpeed,
            ScheduleIntervalMinutes = interval,
            KeepRunningInBackground = ReadBool(element, "keepRunningInBackground") ?? defaults.KeepRunningInBackground,
            OpenAtLogin = ReadBool(element, "openAtLogin") ?? defaults.OpenAtLogin
        };
    }

    /// <summary>
    /// Reads each part of the history on its own: a last run this version cannot read (a newer
    /// version's trigger, say) is dropped, but the times are kept, since losing them would start an
    /// automatic sort at once.
    /// </summary>
    private ScheduleState ReadSchedule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Ignored("schedule", element.GetRawText());
            return new ScheduleState();
        }

        return new ScheduleState
        {
            EnabledAtUtc = ReadTime(element, "enabledAtUtc"),
            LastAttemptUtc = ReadTime(element, "lastAttemptUtc"),
            LastSuccessUtc = ReadTime(element, "lastSuccessUtc"),
            LastRun = ReadLastRun(element),
            MarkedDestinationPath = ReadString(element, "markedDestinationPath")
        };
    }

    private RunRecord? ReadLastRun(JsonElement schedule)
    {
        if (!TryGetValue(schedule, "lastRun", out var value))
        {
            return null;
        }

        try
        {
            return value.Deserialize<RunRecord>(JsonOptions);
        }
        catch (JsonException)
        {
            Ignored("lastRun", value.GetRawText());
            return null;
        }
    }

    private string? ReadString(JsonElement parent, string name)
    {
        return Read(parent, name, value => value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private bool? ReadBool(JsonElement parent, string name)
    {
        return Read(parent, name, value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : (bool?)null);
    }

    private int? ReadInt(JsonElement parent, string name)
    {
        return Read(parent, name, value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : (int?)null);
    }

    private DateTime? ReadTime(JsonElement parent, string name)
    {
        return Read(parent, name, value => value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var time) ? time : (DateTime?)null);
    }

    /// <summary>
    /// The value of one saved property, or null when it is missing or null. A value
    /// <paramref name="convert"/> cannot use (another kind, or out of range) is logged and read as
    /// null, so it falls back to its default.
    /// </summary>
    private T? Read<T>(JsonElement parent, string name, Func<JsonElement, T?> convert)
    {
        if (!TryGetValue(parent, name, out var value))
        {
            return default;
        }

        var result = convert(value);
        if (result is null)
        {
            Ignored(name, value.GetRawText());
        }

        return result;
    }

    /// <summary>
    /// Finds a property whatever its case, as the serializer's web defaults do, so a hand edit that
    /// writes "SourcePath" still counts. Null counts as missing.
    /// </summary>
    private static bool TryGetValue(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind != JsonValueKind.Null)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void Ignored(string setting, string? value)
    {
        logger.LogWarning("Ignoring the saved {Setting} \"{Value}\" in {Path}; using the default", setting, value, _path);
    }

    /// <summary>
    /// The settings as written to the file. The choices are kept as their wire spelling ("full",
    /// "gentle") and parsed on the way back in (see <see cref="ReadSettings"/>), so an unknown value
    /// is caught by the same rules the API applies.
    /// </summary>
    private sealed record StoredSettings(
        string? CsvPath,
        string? SourcePath,
        string? DestinationPath,
        string? ComparisonMode,
        string? CopySpeed,
        int? ScheduleIntervalMinutes,
        bool KeepRunningInBackground,
        bool OpenAtLogin)
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
