using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class SortPlannerTests
{
    [Fact]
    public void Plan_places_a_standalone_book_under_the_author()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b"), planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_places_a_series_book_under_author_series_and_book_number()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "Book 2"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "The Series", "Book 2", "A Book.m4b"),
            planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_reuses_an_existing_author_folder_that_is_spelled_differently()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "JK Rowling"));
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(author: "J.K. Rowling"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "JK Rowling", "A Book", "A Book.m4b"),
            planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_files_differently_spelled_authors_into_one_folder_within_a_run()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(author: "J.K. Rowling", title: "Book One", filename: "book-one"),
            TempWorkspace.Book(author: "JK Rowling", title: "Book Two", filename: "book-two"));

        Assert.Equal(
            Path.GetDirectoryName(Path.GetDirectoryName(planned[0].AudioDestination)),
            Path.GetDirectoryName(Path.GetDirectoryName(planned[1].AudioDestination)));
    }

    [Fact]
    public void Plan_reuses_an_existing_series_folder_that_is_spelled_differently()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Wheel of Time"));
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "The Wheel of Time Series"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Wheel of Time", "A Book", "A Book.m4b"),
            planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_gives_two_different_books_with_the_same_name_separate_folders()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b");
        workspace.WriteSourceFile("second.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.NotEqual(planned[0].AudioDestination, planned[1].AudioDestination);
        // A folder is one book to a library tool, so sharing one would merge them.
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works", "Collected Works.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works (2)", "Collected Works.m4b"),
            planned[1].AudioDestination);
    }

    [Fact]
    public void Plan_copies_a_duplicated_row_only_once()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(), TempWorkspace.Book());

        Assert.NotNull(planned[0].AudioDestination);
        Assert.Null(planned[1].AudioDestination);
    }

    [Fact]
    public void Plan_reuses_a_destination_file_that_already_exists_under_an_equivalent_name()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        // Written by an earlier version that punctuated the name differently. The fixture name
        // has to be legal on every platform: NTFS reads ':' as an alternate data stream.
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book - The Sequel", "A Book - The Sequel.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book(title: "A Book: The Sequel"));

        // Sanitising gives "A Book The Sequel", but the existing file means the same thing, so it
        // is reused rather than duplicated alongside it.
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "A Book - The Sequel", "A Book - The Sequel.m4b"),
            planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_gives_a_series_book_without_a_number_its_own_folder_inside_the_series()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "The Series"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "The Series", "A Book", "A Book.m4b"),
            planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_never_leaves_a_file_loose_beside_series_folders()
    {
        // The reported case: a standalone novel and a series by the same author. A loose file in
        // the author folder makes Audiobookshelf treat that folder as one book and stop looking
        // inside it, so the whole series disappears from the library.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("singularity.m4b");
        workspace.WriteSourceFile("bobiverse-1.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(author: "Dennis E. Taylor", title: "The Singularity Trap", filename: "singularity"),
            TempWorkspace.Book(
                author: "Dennis E. Taylor", title: "We Are Legion", filename: "bobiverse-1",
                seriesName: "Bobiverse", seriesSequence: "1"));

        var authorFolder = Path.Combine(workspace.Destination, "Dennis E. Taylor");
        Assert.Equal(
            Path.Combine(authorFolder, "The Singularity Trap", "The Singularity Trap.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(
            Path.Combine(authorFolder, "Bobiverse", "Book 1", "We Are Legion.m4b"),
            planned[1].AudioDestination);
        Assert.All(planned, p => Assert.NotEqual(authorFolder, Path.GetDirectoryName(p.AudioDestination)));
    }

    [Fact]
    public void Plan_never_files_a_standalone_book_inside_a_series_folder_of_the_same_name()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b");
        workspace.WriteSourceFile("series-1.m4b");

        // The standalone book comes first, so only reserving series folders up front prevents it.
        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Bobiverse", filename: "standalone"),
            TempWorkspace.Book(title: "We Are Legion", filename: "series-1", seriesName: "Bobiverse", seriesSequence: "1"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Bobiverse (2)", "Bobiverse.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Bobiverse", "Book 1", "We Are Legion.m4b"),
            planned[1].AudioDestination);
    }

    [Fact]
    public void Plan_moves_a_book_an_older_version_left_loose_in_the_author_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteSourceFile("a-book.pdf", "pdf");
        var looseAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        var loosePdf = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.pdf"), "pdf");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(looseAudio, planned[0].AudioLegacyPath);
        Assert.Equal(loosePdf, planned[0].PdfLegacyPath);
    }

    [Fact]
    public void Plan_does_not_move_a_loose_file_when_the_book_folder_already_has_one()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Null(planned[0].AudioLegacyPath);
    }

    [Fact]
    public void Plan_matches_loose_same_name_files_to_the_folders_they_belong_in()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b");
        workspace.WriteSourceFile("second.m4b");
        var firstLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "audio");
        var secondLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(firstLoose, planned[0].AudioLegacyPath);
        Assert.Equal(secondLoose, planned[1].AudioLegacyPath);
    }

    [Fact]
    public void Plan_never_moves_files_for_a_numbered_series_book()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "1"));

        Assert.Null(planned[0].AudioLegacyPath);
    }

    [Fact]
    public void Plan_keeps_a_traversal_attempt_inside_the_destination()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(author: "../../etc", title: "../../passwd"));

        Assert.NotNull(planned[0].AudioDestination);
        Assert.True(PathSanitizer.IsWithin(workspace.Destination, planned[0].AudioDestination!));
    }

    [Fact]
    public void Plan_reports_a_book_whose_files_are_missing_instead_of_failing()
    {
        using var workspace = new TempWorkspace();

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.False(planned[0].HasWork);
        Assert.NotNull(planned[0].Warning);
        Assert.Null(planned[0].TargetDirectory);
    }

    [Fact]
    public void Plan_finds_a_pdf_companion_named_after_the_book()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteSourceFile("a-book.pdf", "pdf");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.pdf"), planned[0].PdfDestination);
    }

    [Fact]
    public void Plan_finds_an_audio_file_even_when_the_format_columns_are_empty()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.mp3");

        var planned = Plan(workspace, TempWorkspace.Book(m4b: null, mp3: null));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.mp3"), planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_finds_an_audio_file_listed_with_a_windows_style_path()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(m4b: null, filename: "not-this-one", filePaths: @"C:\OpenAudible\books\a-book.m4b"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b"), planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_falls_back_to_the_source_folder_when_a_recorded_absolute_path_is_stale()
    {
        // Exports record where the file was, which is routinely not where it is now: another
        // machine, another drive, a restored backup.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var stalePath = Path.Combine(Path.GetTempPath(), "oabo-not-here", "a-book.m4b");
        Assert.True(Path.IsPathRooted(stalePath));

        var planned = Plan(
            workspace,
            TempWorkspace.Book(m4b: null, filename: "not-this-one", filePaths: stalePath));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b"), planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_prefers_a_recorded_absolute_path_that_still_exists()
    {
        using var workspace = new TempWorkspace();
        var elsewhere = Path.Combine(workspace.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var recordedPath = Path.Combine(elsewhere, "a-book.m4b");
        File.WriteAllText(recordedPath, "recorded");
        workspace.WriteSourceFile("a-book.m4b", "in-source");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(m4b: null, filename: "not-this-one", filePaths: recordedPath));

        Assert.Equal(recordedPath, planned[0].AudioSource);
    }

    [Fact]
    public void Plan_is_deterministic_for_the_same_input()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < 20; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b");
        }

        var books = Enumerable.Range(0, 20)
            .Select(i => TempWorkspace.Book(title: "Same Title", author: i % 2 == 0 ? "A. Author" : "A Author", filename: $"book-{i}"))
            .ToList();

        var first = new SortPlanner().Plan(books, workspace.Source, workspace.Destination);
        var second = new SortPlanner().Plan(books, workspace.Source, workspace.Destination);

        Assert.Equal(
            first.Select(p => p.AudioDestination),
            second.Select(p => p.AudioDestination));
    }

    private static List<PlannedCopy> Plan(TempWorkspace workspace, params OpenAudible[] books)
    {
        return new SortPlanner().Plan(books, workspace.Source, workspace.Destination);
    }
}
