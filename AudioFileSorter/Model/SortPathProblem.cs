namespace AudioFileSorter.Model;

/// <summary>Which of the paths a sort needs is the problem.</summary>
public enum SortPathField
{
    Csv,
    Source,
    Destination
}

/// <summary>What is wrong with the path, for callers that react differently to each.</summary>
public enum SortPathProblemCode
{
    /// <summary>No path was given.</summary>
    NotSet,

    /// <summary>The CSV file or source folder does not exist.</summary>
    NotFound,

    /// <summary>
    /// The destination folder does not exist. Kept apart from <see cref="NotFound"/> because the
    /// answer is different: a person can choose to create it, an unattended run never does.
    /// </summary>
    DestinationMissing,

    /// <summary>The destination is the source folder or inside it.</summary>
    DestinationInsideSource,

    /// <summary>The destination folder exists (or was just created) but cannot be written to.</summary>
    NotWritable
}

/// <summary>Why a sort cannot use the paths it was given, worded for the person who chose them.</summary>
public sealed record SortPathProblem(SortPathField Field, SortPathProblemCode Code, string Message);
