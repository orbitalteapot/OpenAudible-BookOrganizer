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
/// newer version's value) does not throw away the rest.
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

    /// <param name="defaults">The settings used for anything the file does not hold, or holds wrongly.</param>
    public SavedState Load(AppSettings defaults)
    {
        var empty = new SavedState(defaults, new ScheduleState());
        if (_path is null || !File.Exists(_path))
        {
            return empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            return new SavedState(
                root.TryGetProperty("settings", out var settings) ? ReadSettings(settings, defaults) : defaults,
                root.TryGetProperty("schedule", out var schedule) ? ReadSchedule(schedule) : new ScheduleState());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the settings file at {Path}; starting with the default settings", _path);
            return empty;
        }
    }

    /// <summary>
    /// Written beside the target, flushed to the disk and renamed over it, so a crash or a power cut
    /// leaves either the old file or the new one, never half of one.
    /// </summary>
    public void Save(SavedState state)
    {
        if (_path is null)
        {
            return;
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the settings to {Path}; changes will be forgotten on restart", _path);
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

        var comparisonMode = defaults.ComparisonMode;
        if (stored.ComparisonMode is not null && !SortOptions.TryParseComparisonMode(stored.ComparisonMode, out comparisonMode))
        {
            Ignored("comparisonMode", stored.ComparisonMode);
            comparisonMode = defaults.ComparisonMode;
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
            KeepRunningInBackground = stored.KeepRunningInBackground,
            OpenAtLogin = stored.OpenAtLogin
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
    /// API applies instead of failing the whole file.
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
                SortOptions.ToWireValue(settings.ComparisonMode),
                SortOptions.ToWireValue(settings.CopySpeed),
                settings.ScheduleIntervalMinutes,
                settings.KeepRunningInBackground,
                settings.OpenAtLogin);
        }
    }
}
