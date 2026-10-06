using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFileSorter;
using AudioFileSorter.Model;

namespace ManagerApi.Services;

public enum RunState
{
    /// <summary>No sort has run since the server started.</summary>
    Idle,
    Running,
    Finished
}

/// <summary>Who started a run: a person pressing Start sorting, or the schedule.</summary>
public enum RunTrigger
{
    Manual,
    Scheduled
}

/// <summary>
/// The current or most recent sort, as GET /api/sort/progress returns it. Whichever way the run was
/// started, this is where it shows up, so the page can follow and cancel a run it did not start.
/// </summary>
public sealed record RunStatus
{
    public static readonly RunStatus Idle = new();

    public RunState State { get; init; } = RunState.Idle;
    public RunTrigger? Trigger { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public int TotalBooks { get; init; }
    public int CurrentBook { get; init; }
    public double Percentage { get; init; }
    public string? CurrentTitle { get; init; }
    public SortCounts Counts { get; init; } = SortCounts.Empty;
    public IReadOnlyList<SortProblem> Problems { get; init; } = [];

    /// <summary>The latest problems, which is what a page following a run gets (see <see cref="ForPolling"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<SortProblem> RecentProblems { get; init; } = [];

    public int ProblemCount { get; init; }
    public bool IsCanceled { get; init; }

    /// <summary>
    /// The run is reading the export and looking through the folders, before its first book. On a
    /// large library on a network drive that takes minutes, and a bare 0% for that long looks hung.
    /// </summary>
    public bool Preparing => State == RunState.Running && !Planned;

    /// <summary>The sorter has reported its plan: every progress report comes after it (see <see cref="With"/>).</summary>
    [JsonIgnore]
    public bool Planned { get; init; }

    /// <summary>Why the run stopped early, for the user. Null when it ran to the end or was canceled by a person.</summary>
    public string? Error { get; init; }

    /// <summary>What kind of failure stopped the run before its first book, for pages that react to it (see <see cref="RunErrors"/>).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The setting at fault, as named on the wire ("destinationPath").</summary>
    public string? ErrorField { get; init; }

    /// <summary>A run that has just been started, before its first book.</summary>
    public static RunStatus Starting(RunTrigger trigger, DateTime startedUtc)
    {
        return new RunStatus { State = RunState.Running, Trigger = trigger, StartedUtc = startedUtc };
    }

    /// <summary>A run that was refused before it started, because of its paths.</summary>
    public static RunStatus Rejected(RunTrigger trigger, SortPathProblem problem, DateTime nowUtc)
    {
        return Starting(trigger, nowUtc).Failed(problem.Message, RunErrors.Code(problem), RunErrors.Field(problem.Field), nowUtc);
    }

    /// <summary>This run, updated with a progress report from the sorter.</summary>
    public RunStatus With(SortProgressInfo progress)
    {
        return this with
        {
            Planned = true,
            TotalBooks = progress.TotalBooks,
            CurrentBook = progress.CurrentBook,
            Percentage = progress.Percentage,
            CurrentTitle = progress.CurrentTitle,
            Counts = progress.Counts,
            Problems = progress.Problems,
            RecentProblems = progress.RecentProblems,
            ProblemCount = progress.ProblemCount
        };
    }

    /// <summary>This run, completed with every book processed.</summary>
    public RunStatus Completed(SortSummary summary, DateTime nowUtc)
    {
        return this with
        {
            State = RunState.Finished,
            FinishedUtc = nowUtc,
            TotalBooks = summary.TotalBooks,
            CurrentBook = summary.TotalBooks,
            Percentage = 100,
            CurrentTitle = null,
            Counts = summary.Counts,
            Problems = summary.Problems,
            ProblemCount = summary.ProblemCount
        };
    }

    /// <summary>This run, stopped before the end. What it got through so far is kept.</summary>
    public RunStatus Canceled(string? reason, DateTime nowUtc)
    {
        return this with { State = RunState.Finished, FinishedUtc = nowUtc, IsCanceled = true, CurrentTitle = null, Error = reason };
    }

    public RunStatus Failed(string error, string? errorCode, string? errorField, DateTime nowUtc)
    {
        return this with
        {
            State = RunState.Finished,
            FinishedUtc = nowUtc,
            CurrentTitle = null,
            Error = error,
            ErrorCode = errorCode,
            ErrorField = errorField
        };
    }

    /// <summary>
    /// This status as sent to a page that polls it: only the latest problems while the run is going
    /// (the capped <see cref="Problems"/> stop at the oldest 500), and all of them once it is finished.
    /// </summary>
    public RunStatus ForPolling()
    {
        return State == RunState.Running ? this with { Problems = RecentProblems } : this;
    }
}

/// <summary>The codes and field names the API uses for errors, so every endpoint spells them the same way.</summary>
public static class RunErrors
{
    public const string AlreadyRunning = "alreadyRunning";
    public const string CsvNotFound = "csvNotFound";
    public const string CsvInvalid = "csvInvalid";
    public const string InvalidComparisonMode = "invalidComparisonMode";

    public const string CsvPathField = "csvPath";
    public const string SourcePathField = "sourcePath";
    public const string DestinationPathField = "destinationPath";

    /// <summary>The problem's <see cref="SortPathProblemCode"/> in camelCase: "destinationMissing", "notWritable", ...</summary>
    public static string Code(SortPathProblem problem) => JsonNamingPolicy.CamelCase.ConvertName(problem.Code.ToString());

    /// <summary>The settings property that holds <paramref name="field"/>.</summary>
    public static string Field(SortPathField field) => field switch
    {
        SortPathField.Csv => CsvPathField,
        SortPathField.Source => SourcePathField,
        _ => DestinationPathField
    };

    /// <summary>The body of a 400 for a rejected path: { error, code, field }.</summary>
    public static object Body(SortPathException ex)
    {
        return new { error = ex.Message, code = Code(ex.Problem), field = Field(ex.Problem.Field) };
    }
}
