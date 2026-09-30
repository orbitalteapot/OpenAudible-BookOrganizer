using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Whether each path can be seen right now: "ok", "notSet" or "notFound", or "unmounted" for a
/// destination that no longer holds the marker earlier sorts left in it. Only looks, never writes,
/// so a page can ask as often as it likes; whether the destination can be written is found out when
/// a sort starts or automatic sorting is turned on.
/// </summary>
public sealed record PathStatus(string Csv, string Source, string Destination)
{
    public static PathStatus From(PathProblems problems)
    {
        return new PathStatus(Describe(problems.Csv), Describe(problems.Source), Describe(problems.Destination));
    }

    /// <summary>
    /// Why a sort with these settings could not start, without writing anything, or null when
    /// nothing visible is wrong. A path fixed by the environment is explained in its terms (see
    /// <see cref="ServerConfig.Explain"/>), since that is where it has to be fixed.
    /// </summary>
    public static string? BlockedReason(AppSettings settings, ServerConfig config)
    {
        var problem =
            SortPathValidator.ValidateCsv(settings.CsvPath) ??
            SortPathValidator.ValidateSource(settings.SourcePath) ??
            SortPathValidator.InspectDestination(settings.SourcePath, settings.DestinationPath);

        return problem is null ? null : config.Explain(problem).Message;
    }

    private static string Describe(SortPathProblem? problem) => problem?.Code switch
    {
        null => "ok",
        SortPathProblemCode.NotSet => "notSet",
        SortPathProblemCode.DestinationUnmounted => "unmounted",
        _ => "notFound"
    };
}

/// <summary>
/// What is wrong with each path right now, looking only (see <see cref="PathStatus"/>). Looked at
/// once per answer and shared by everything in it: on a sleeping network share each look can take
/// a while.
/// </summary>
public sealed record PathProblems(SortPathProblem? Csv, SortPathProblem? Source, SortPathProblem? Destination)
{
    public static PathProblems For(AppSettings settings, SettingsService service)
    {
        return new PathProblems(
            SortPathValidator.ValidateCsv(settings.CsvPath),
            SortPathValidator.ValidateSource(settings.SourcePath),
            // No overlap check here: that is a problem with the pair, not with this folder, and saving
            // or starting refuses it with its own message. The marker is, whether or not automatic
            // sorting is on: a person about to press Start sorting needs the warning as much.
            SortPathValidator.InspectDestination(null, settings.DestinationPath) ?? service.CheckUnattended(settings));
    }
}

/// <summary>
/// Why each path the server's environment sets cannot be used, in the words of
/// <see cref="ServerConfig.Explain"/>, so the page shows a missing mount or subfolder the way a
/// refused start and the Automatic sorting card do. Null for a path that is fine, and for every path
/// when the environment sets none: the desktop's short "Folder not found" needs no more.
/// </summary>
public sealed record PathMessages(string? Csv, string? Source, string? Destination)
{
    public static PathMessages From(PathProblems problems, ServerConfig config)
    {
        return new PathMessages(Message(problems.Csv), Message(problems.Source), Message(problems.Destination));

        string? Message(SortPathProblem? problem) =>
            problem is null || !config.PathsLocked ? null : config.Explain(problem).Message;
    }
}
