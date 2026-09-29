using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

/// <summary>
/// Whether each path can be seen right now: "ok", "notSet" or "notFound". Only looks, never writes,
/// so a page can ask as often as it likes; whether the destination can be written is found out when
/// a sort starts or automatic sorting is turned on.
/// </summary>
public sealed record PathStatus(string Csv, string Source, string Destination)
{
    public static PathStatus For(AppSettings settings)
    {
        return new PathStatus(
            Describe(SortPathValidator.ValidateCsv(settings.CsvPath)),
            Describe(SortPathValidator.ValidateSource(settings.SourcePath)),
            // No overlap check here: that is a problem with the pair, not with this folder, and saving
            // or starting refuses it with its own message.
            Describe(SortPathValidator.InspectDestination(null, settings.DestinationPath)));
    }

    /// <summary>
    /// Why a sort with these settings could not start, without writing anything, or null when
    /// nothing visible is wrong. A path fixed by the environment is named by its variable, since
    /// that is where it has to be fixed.
    /// </summary>
    public static string? BlockedReason(AppSettings settings, ServerConfig config)
    {
        var problem =
            SortPathValidator.ValidateCsv(settings.CsvPath) ??
            SortPathValidator.ValidateSource(settings.SourcePath) ??
            SortPathValidator.InspectDestination(settings.SourcePath, settings.DestinationPath);

        return problem switch
        {
            null => null,
            { Code: SortPathProblemCode.NotSet } when config.PathsLocked => $"{ServerConfig.VariableFor(problem.Field)} is not set.",
            _ => problem.Message
        };
    }

    private static string Describe(SortPathProblem? problem) => problem?.Code switch
    {
        null => "ok",
        SortPathProblemCode.NotSet => "notSet",
        _ => "notFound"
    };
}
