using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>Which settings the server's environment fixes, so the page shows them read-only.</summary>
public sealed record SettingsLocks(bool Paths, bool Schedule);

/// <summary>What GET and PUT /api/settings return: the settings in force, and what the page needs to explain them.</summary>
public sealed record SettingsResponse(
    string? CsvPath,
    string? SourcePath,
    string? DestinationPath,
    string ComparisonMode,
    string CopySpeed,
    int? ScheduleIntervalMinutes,
    bool KeepRunningInBackground,
    bool OpenAtLogin,
    SettingsLocks Locks,
    PathStatus PathStatus,
    IReadOnlyList<string> ServerWarnings)
{
    public static SettingsResponse From(SettingsService service)
    {
        var settings = service.Effective;
        var config = service.Config;

        return new SettingsResponse(
            settings.CsvPath,
            settings.SourcePath,
            settings.DestinationPath,
            SortOptions.ToWireValue(settings.ComparisonMode!.Value),
            SortOptions.ToWireValue(settings.CopySpeed),
            settings.ScheduleIntervalMinutes,
            settings.KeepRunningInBackground,
            settings.OpenAtLogin,
            new SettingsLocks(config.PathsLocked, config.ScheduleLocked),
            PathStatus.For(settings),
            service.Warnings);
    }
}
