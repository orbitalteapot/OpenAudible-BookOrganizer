using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class SortPathValidatorTests
{
    [Fact]
    public void Paths_that_exist_and_can_be_written_pass()
    {
        using var workspace = new TempWorkspace();
        var csv = WriteCsv(workspace);

        Assert.Null(SortPathValidator.Validate(csv, workspace.Source, workspace.Destination, createDestination: false));
    }

    [Fact]
    public void The_csv_is_not_checked_when_none_is_passed()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(SortPathValidator.Validate(null, workspace.Source, workspace.Destination, createDestination: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_csv_is_not_set(string csv)
    {
        using var workspace = new TempWorkspace();

        AssertProblem(
            SortPathField.Csv, SortPathProblemCode.NotSet,
            SortPathValidator.Validate(csv, workspace.Source, workspace.Destination, createDestination: false));
    }

    [Fact]
    public void A_missing_csv_is_not_found()
    {
        using var workspace = new TempWorkspace();

        AssertProblem(
            SortPathField.Csv, SortPathProblemCode.NotFound,
            SortPathValidator.Validate(
                Path.Combine(workspace.Root, "missing.csv"), workspace.Source, workspace.Destination, createDestination: false));
    }

    [Fact]
    public void The_csv_is_reported_before_the_folders()
    {
        AssertProblem(SortPathField.Csv, SortPathProblemCode.NotSet, SortPathValidator.Validate("", null, null, createDestination: false));
    }

    [Fact]
    public void A_missing_source_is_not_found()
    {
        using var workspace = new TempWorkspace();

        AssertProblem(
            SortPathField.Source, SortPathProblemCode.NotFound,
            SortPathValidator.Validate(null, Path.Combine(workspace.Root, "missing"), workspace.Destination, createDestination: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_blank_destination_is_not_set(string? destination)
    {
        using var workspace = new TempWorkspace();

        AssertProblem(
            SortPathField.Destination, SortPathProblemCode.NotSet,
            SortPathValidator.Validate(null, workspace.Source, destination, createDestination: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sorted")]
    public void A_destination_that_is_or_is_inside_the_source_is_refused(string subfolder)
    {
        using var workspace = new TempWorkspace();

        AssertProblem(
            SortPathField.Destination, SortPathProblemCode.DestinationInsideSource,
            SortPathValidator.Validate(null, workspace.Source, Path.Combine(workspace.Source, subfolder), createDestination: true));
    }

    [Fact]
    public void A_destination_anywhere_on_a_source_that_is_a_whole_drive_is_refused()
    {
        // "/" or "C:\" keeps its separator when trimmed, which a plain prefix test got wrong.
        using var workspace = new TempWorkspace();
        var driveRoot = Path.GetPathRoot(workspace.Destination)!;

        AssertProblem(
            SortPathField.Destination, SortPathProblemCode.DestinationInsideSource,
            SortPathValidator.Validate(null, driveRoot, workspace.Destination, createDestination: false));
    }

    [Fact]
    public void A_missing_destination_is_reported_and_left_alone()
    {
        using var workspace = new TempWorkspace();
        var destination = Path.Combine(workspace.Root, "unplugged");

        var problem = SortPathValidator.Validate(null, workspace.Source, destination, createDestination: false);

        AssertProblem(SortPathField.Destination, SortPathProblemCode.DestinationMissing, problem);
        Assert.Contains("Is the drive connected?", problem!.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void A_missing_destination_is_created_when_asked_to()
    {
        using var workspace = new TempWorkspace();
        var destination = Path.Combine(workspace.Root, "new", "library");

        Assert.Null(SortPathValidator.Validate(null, workspace.Source, destination, createDestination: true));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void The_write_check_leaves_nothing_behind()
    {
        using var workspace = new TempWorkspace();

        SortPathValidator.Validate(null, workspace.Source, workspace.Destination, createDestination: false);

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Destination));
    }

    [Fact]
    public void A_destination_that_cannot_be_written_is_reported()
    {
        // Permission bits do not stop root, and Windows has no Unix modes to set.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var workspace = new TempWorkspace();
        File.SetUnixFileMode(workspace.Destination, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var problem = SortPathValidator.Validate(null, workspace.Source, workspace.Destination, createDestination: false);

            AssertProblem(SortPathField.Destination, SortPathProblemCode.NotWritable, problem);
            Assert.StartsWith("Cannot write to the destination folder: ", problem!.Message);
        }
        finally
        {
            File.SetUnixFileMode(workspace.Destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void AssertProblem(SortPathField field, SortPathProblemCode code, SortPathProblem? problem)
    {
        Assert.NotNull(problem);
        Assert.Equal(field, problem.Field);
        Assert.Equal(code, problem.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
    }

    private static string WriteCsv(TempWorkspace workspace)
    {
        var path = Path.Combine(workspace.Root, "books.csv");
        File.WriteAllText(path, "Title,Author,File name\n");
        return path;
    }
}
