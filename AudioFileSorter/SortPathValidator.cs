using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// The one place the paths a sort needs are checked, so the Sort page, the schedule and the sorter
/// itself all reject the same things with the same words.
/// </summary>
public static class SortPathValidator
{
    private const string WriteProbePrefix = ".oabo-write-test-";

    /// <summary>
    /// Checks the paths in the order a person fixes them: CSV, source, destination. Returns the
    /// first problem, or null when a sort can go ahead.
    ///
    /// Nothing is created unless <paramref name="createDestination"/> is true. A destination that
    /// does not exist is most often an unplugged drive or an unmounted share, and quietly creating
    /// it would copy the whole library onto the internal disk instead.
    /// </summary>
    /// <param name="csvPath">
    /// The export to check, or null to skip the CSV entirely (the sorter is handed the books, not
    /// the file). An empty string is checked, and reported as not set.
    /// </param>
    /// <param name="createDestination">Create a missing destination instead of reporting it.</param>
    public static SortPathProblem? Validate(string? csvPath, string? sourcePath, string? destinationPath, bool createDestination)
    {
        return (csvPath is null ? null : ValidateCsv(csvPath))
               ?? ValidateSource(sourcePath)
               ?? ValidateDestination(sourcePath!, destinationPath, createDestination);
    }

    /// <summary>Checks the library export on its own: set, and a file that exists.</summary>
    public static SortPathProblem? ValidateCsv(string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return new SortPathProblem(SortPathField.Csv, SortPathProblemCode.NotSet, "No library export (CSV file) is set.");
        }

        return File.Exists(csvPath)
            ? null
            : new SortPathProblem(SortPathField.Csv, SortPathProblemCode.NotFound, $"The library export was not found: {csvPath}");
    }

    /// <summary>Checks the source folder on its own: set, and a folder that exists.</summary>
    public static SortPathProblem? ValidateSource(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new SortPathProblem(SortPathField.Source, SortPathProblemCode.NotSet, "No source folder is set.");
        }

        return Directory.Exists(sourcePath)
            ? null
            : new SortPathProblem(SortPathField.Source, SortPathProblemCode.NotFound, $"The source folder was not found: {sourcePath}");
    }

    /// <summary>
    /// The destination checks that only look: set, not the source or inside it, and present.
    /// Never creates or writes anything, so it is safe to run whenever a page asks for the status.
    /// Whether the folder can actually be written is only known by writing to it, which
    /// <see cref="Validate"/> does.
    /// </summary>
    /// <param name="sourcePath">The source folder, or null to skip the overlap check.</param>
    public static SortPathProblem? InspectDestination(string? sourcePath, string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return Destination(SortPathProblemCode.NotSet, "No destination folder is set.");
        }

        if (!string.IsNullOrWhiteSpace(sourcePath) && PathsOverlap(sourcePath, destinationPath))
        {
            return Destination(
                SortPathProblemCode.DestinationInsideSource,
                "The destination folder cannot be the source folder or a folder inside it.");
        }

        return Directory.Exists(destinationPath)
            ? null
            : Destination(
                SortPathProblemCode.DestinationMissing,
                "The destination folder does not exist. Is the drive connected?");
    }

    private static SortPathProblem? ValidateDestination(string sourcePath, string? destinationPath, bool createDestination)
    {
        var problem = InspectDestination(sourcePath, destinationPath);
        if (problem is { Code: SortPathProblemCode.DestinationMissing } && createDestination)
        {
            try
            {
                Directory.CreateDirectory(destinationPath!);
                problem = null;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                return NotWritable(ex);
            }
        }

        return problem ?? ProbeWritable(destinationPath!);
    }

    /// <summary>
    /// Proves the destination can be written by writing to it. Permission bits do not tell the
    /// whole story: a read-only NAS share or a full disk only shows up when a write is attempted,
    /// and finding out before the first book beats every book failing one by one.
    /// </summary>
    private static SortPathProblem? ProbeWritable(string destinationPath)
    {
        var probe = Path.Combine(destinationPath, WriteProbePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            // DeleteOnClose removes it even if this process dies before the stream is disposed.
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return NotWritable(ex);
        }
    }

    /// <summary>
    /// Copying a library into itself (or into a subfolder of itself) makes the source grow while it
    /// is being read, which never terminates cleanly.
    /// </summary>
    private static bool PathsOverlap(string sourcePath, string destinationPath)
    {
        try
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));

            // IsWithin, not a prefix test of its own: a source that is a drive root ("D:\", "/")
            // keeps its separator when trimmed, which a hand-made "source + separator" test misses.
            return string.Equals(source, destination, PathSanitizer.PathComparison) ||
                   PathSanitizer.IsWithin(source, destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static SortPathProblem Destination(SortPathProblemCode code, string message)
    {
        return new SortPathProblem(SortPathField.Destination, code, message);
    }

    private static SortPathProblem NotWritable(Exception ex)
    {
        return Destination(SortPathProblemCode.NotWritable, $"Cannot write to the destination folder: {ex.Message}");
    }

    private static bool IsFileSystemError(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    }
}
