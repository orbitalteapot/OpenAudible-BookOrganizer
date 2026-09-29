using System.Text.Json;

namespace ManagerApi.Services;

/// <summary>
/// Saves the schedule to a JSON file, so it survives a restart. With no path it keeps nothing, and
/// a file that cannot be read or written is logged rather than allowed to stop the app.
/// </summary>
public sealed class SortScheduleStore(string? path, ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _path = string.IsNullOrWhiteSpace(path) ? null : path;

    public SortSchedule Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return new SortSchedule();
        }

        try
        {
            return JsonSerializer.Deserialize<SortSchedule>(File.ReadAllText(_path), JsonOptions) ?? new SortSchedule();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read the saved schedule at {Path}; automatic sorting is off", _path);
            return new SortSchedule();
        }
    }

    /// <summary>Written beside the target and renamed over it, so a crash never leaves half a file.</summary>
    public void Save(SortSchedule schedule)
    {
        if (_path is null)
        {
            return;
        }

        var partialPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(partialPath, JsonSerializer.Serialize(schedule, JsonOptions));
            File.Move(partialPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the schedule to {Path}; it will be forgotten on restart", _path);
        }
    }
}
