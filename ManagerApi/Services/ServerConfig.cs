using System.Net;
using System.Text.RegularExpressions;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Everything the server takes from its environment, read once at startup. Nothing else reads
/// environment variables, so what the startup log says is what the server does.
/// </summary>
public sealed record ServerConfig
{
    public const string CsvPathVariable = "CSV_PATH";
    public const string SourcePathVariable = "SOURCE_PATH";
    public const string DestinationPathVariable = "DESTINATION_PATH";
    public const string ScheduleVariable = "SORT_INTERVAL";

    /// <summary>
    /// Loopback by default: the desktop app's backend is an unauthenticated file API and must not be
    /// reachable from the rest of the network. The container sets 0.0.0.0 explicitly.
    /// </summary>
    public const string DefaultBindUrl = "http://127.0.0.1:5123";

    /// <summary>CSV_PATH, or null when unset.</summary>
    public string? CsvPath { get; init; }

    /// <summary>SOURCE_PATH, or null when unset.</summary>
    public string? SourcePath { get; init; }

    /// <summary>DESTINATION_PATH, or null when unset.</summary>
    public string? DestinationPath { get; init; }

    /// <summary>COMPARISON_MODE: the update check used until one is saved from the Sort page.</summary>
    public FileComparisonMode DefaultComparisonMode { get; init; } = SortOptions.Default.ComparisonMode;

    /// <summary>OABO_MAX_PARALLELISM: books copied at once when the copy speed is Normal.</summary>
    public int NormalParallelism { get; init; } = SortOptions.DefaultParallelism;

    /// <summary>SORT_INTERVAL in minutes, or null when it is unset, "off" or unreadable.</summary>
    public int? ScheduleIntervalMinutes { get; init; }

    /// <summary>OABO_SETTINGS_PATH, or null to keep settings in memory only.</summary>
    public string? SettingsPath { get; init; }

    /// <summary>ASPNETCORE_URLS, or <see cref="DefaultBindUrl"/>.</summary>
    public string BindUrl { get; init; } = DefaultBindUrl;

    /// <summary>
    /// OABO_PARENT_PID: the desktop app that started this backend, which stops when that app is gone
    /// (see <see cref="ParentProcessWatch"/>). Null for a backend nobody started, such as the container.
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>Environment values that were ignored, worded for the person who set them.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Whether a folder lies on a volume mapped into the container rather than on the image's own
    /// disk (see <see cref="IsOnMappedVolume"/>). Replaced in tests, which do not run in a container.
    /// </summary>
    internal Func<string, bool> IsMappedFolder { get; init; } = IsOnMappedVolume;

    /// <summary>
    /// Setting any of the three paths fixes all of them: a container mounts its volumes where its
    /// variables say, and a path picked in the page would point somewhere the container cannot see.
    /// </summary>
    public bool PathsLocked => CsvPath is not null || SourcePath is not null || DestinationPath is not null;

    /// <summary>
    /// Only a readable interval fixes the schedule. "off" or a blank value leaves it to the Sort page,
    /// as does a value that could not be read — the warning says so rather than silently locking it.
    /// </summary>
    public bool ScheduleLocked => ScheduleIntervalMinutes is not null;

    /// <summary>
    /// Every address it listens on is this computer's own: the desktop app's backend. Only that one
    /// can tell which Host names and origins are its own (see <see cref="LocalRequestGuard"/>); the
    /// container is reached by whatever name the network gives it.
    /// </summary>
    public bool IsLoopbackOnly => BindUrl
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.IsLoopback || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));

    /// <summary>
    /// <paramref name="problem"/>, worded for where it has to be fixed. A path the environment sets is
    /// a container mount, so "Is the drive connected?" would send its admin looking for a USB drive
    /// when the fix is the variable or the volume mapping. The one wording for a server-set path that
    /// is missing, wherever the problem is shown: a refused start or save, a failed automatic run, why
    /// automatic sorting cannot run, or the path's own status on the page (<see cref="PathMessages"/>).
    /// </summary>
    public SortPathProblem Explain(SortPathProblem problem)
    {
        if (!PathsLocked)
        {
            return problem;
        }

        var variable = VariableFor(problem.Field);
        var message = (problem.Code, problem.Field) switch
        {
            (SortPathProblemCode.NotSet, _) => $"{variable} is not set.",
            (SortPathProblemCode.NotFound, SortPathField.Csv) =>
                $"The library export {CsvPath} was not found inside the container. Check that the folder holding it is mapped, and that {variable} names the file.",
            // An empty source folder made on the host would not help: every book would be "Not found".
            (SortPathProblemCode.NotFound or SortPathProblemCode.DestinationMissing, SortPathField.Source) =>
                MissingMount("source", SourcePath!, variable),
            (SortPathProblemCode.NotFound or SortPathProblemCode.DestinationMissing, SortPathField.Destination) =>
                MissingFolder("destination", DestinationPath!, variable),
            _ => problem.Message
        };

        return problem with { Message = message };
    }

    /// <summary>
    /// A server-set folder that is not there. When the folder it sits in is on a mapped volume, the
    /// mapping works and only the folder itself is missing, such as an "Audiobooks" subfolder of the
    /// mount nobody has made yet. Sending that admin to check a mapping that is fine leaves them
    /// stuck, with no Create folder button to press. A parent that merely exists proves nothing: the
    /// image already has /mnt, /media, /srv, /opt and /home, and nothing is mapped to them.
    /// </summary>
    private string MissingFolder(string noun, string path, string variable)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var parentIsMounted = !string.IsNullOrEmpty(parent) && Directory.Exists(parent) && IsMappedFolder(parent);

        return parentIsMounted
            ? $"The folder {Path.GetFileName(Path.TrimEndingDirectorySeparator(path))} does not exist inside {parent}. Create it on the host (in the folder mapped to {parent}), then try again."
            : MissingMount(noun, path, variable);
    }

    private static string MissingMount(string noun, string path, string variable)
    {
        return $"The {noun} folder {path} was not found inside the container. Check the volume mapping for {variable}.";
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is on a mount other than the container's root file system:
    /// a volume or bind mount, as listed in /proc/self/mountinfo. False where that cannot be read,
    /// which keeps the advice to check the mapping.
    /// </summary>
    private static bool IsOnMappedVolume(string folder)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return lines
            .Select(line => line.Split(' '))
            .Where(fields => fields.Length > 4)
            // The mount point, with spaces and other awkward characters written as octal escapes.
            .Select(fields => Regex.Replace(
                fields[4], @"\\([0-7]{3})", match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString()))
            .Any(mountPoint => mountPoint != "/" &&
                               (full == mountPoint || full.StartsWith(mountPoint + "/", StringComparison.Ordinal)));
    }

    /// <summary>The variable that fixes <paramref name="field"/>, for messages that tell the user where to change it.</summary>
    public static string VariableFor(SortPathField field) => field switch
    {
        SortPathField.Csv => CsvPathVariable,
        SortPathField.Source => SourcePathVariable,
        _ => DestinationPathVariable
    };

    public static ServerConfig FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <param name="read">Looks up one variable; tests pass a dictionary instead of the real environment.</param>
    public static ServerConfig FromEnvironment(Func<string, string?> read)
    {
        var warnings = new List<string>();

        var comparisonValue = read("COMPARISON_MODE");
        if (!SortOptions.TryParseComparisonMode(comparisonValue, out var comparisonMode))
        {
            warnings.Add($"COMPARISON_MODE=\"{comparisonValue}\" was ignored — use \"quick\" or \"full\".");
        }

        var parallelismValue = read("OABO_MAX_PARALLELISM");
        var parallelism = SortOptions.DefaultParallelism;
        if (!string.IsNullOrWhiteSpace(parallelismValue))
        {
            if (int.TryParse(parallelismValue, out var requested) && requested > 0)
            {
                parallelism = requested;
            }
            else
            {
                warnings.Add($"OABO_MAX_PARALLELISM=\"{parallelismValue}\" was ignored — use a whole number of 1 or more.");
            }
        }

        var intervalValue = read(ScheduleVariable);
        if (!SortSchedule.TryParseInterval(intervalValue, out var intervalMinutes))
        {
            warnings.Add(
                $"{ScheduleVariable}=\"{intervalValue}\" was ignored — use a value like 6h, 12h or 1d " +
                $"(at least {SortSchedule.MinimumIntervalMinutes} minutes), or \"off\".");
        }

        return new ServerConfig
        {
            CsvPath = SettingText.Normalize(read(CsvPathVariable)),
            SourcePath = SettingText.Normalize(read(SourcePathVariable)),
            DestinationPath = SettingText.Normalize(read(DestinationPathVariable)),
            DefaultComparisonMode = comparisonMode,
            NormalParallelism = parallelism,
            ScheduleIntervalMinutes = intervalMinutes,
            SettingsPath = SettingText.Normalize(read("OABO_SETTINGS_PATH")),
            BindUrl = SettingText.Normalize(read("ASPNETCORE_URLS")) ?? DefaultBindUrl,
            ParentProcessId = int.TryParse(read("OABO_PARENT_PID"), out var parentId) && parentId > 0 ? parentId : null,
            Warnings = warnings
        };
    }

    /// <summary>
    /// One startup summary, so "which paths is it using, and can it see them?" is answered by the
    /// first lines of <c>docker logs</c>.
    /// </summary>
    public void Log(ILogger logger)
    {
        logger.LogInformation("Listening on {BindUrl}", BindUrl);
        LogPath(logger, CsvPathVariable, CsvPath, File.Exists);
        LogPath(logger, SourcePathVariable, SourcePath, Directory.Exists);
        LogPath(logger, DestinationPathVariable, DestinationPath, Directory.Exists);
        logger.LogInformation(
            "Update check default: {ComparisonMode}; normal copy speed: {Parallelism} books at once",
            SortOptions.ToWireValue(DefaultComparisonMode), NormalParallelism);
        logger.LogInformation(
            "Automatic sorting: {Schedule}",
            ScheduleIntervalMinutes is { } minutes
                ? $"every {minutes} minutes, set by {ScheduleVariable}"
                : "set from the Sort page");
        logger.LogInformation("Settings file: {SettingsPath}", SettingsPath ?? "none (settings are kept in memory only)");

        foreach (var warning in Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }
    }

    private static void LogPath(ILogger logger, string variable, string? path, Func<string, bool> exists)
    {
        if (path is null)
        {
            logger.LogInformation("{Variable}: not set", variable);
            return;
        }

        logger.LogInformation("{Variable}: {Path} ({State})", variable, path, exists(path) ? "found" : "missing");
    }
}
