using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class FileSorterTests
{
    [Fact]
    public async Task Sort_copies_a_book_into_the_author_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio-bytes");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(["An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { New = 1 }, summary.Counts);
        Assert.Equal("audio-bytes", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b")));
    }

    [Fact]
    public async Task Sort_copies_a_series_book_into_the_documented_folder_structure()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("sorcerers-stone.m4b");

        await Sort(workspace, TempWorkspace.Book(
            author: "J.K. Rowling",
            title: "Harry Potter and the Sorcerer's Stone",
            filename: "sorcerers-stone",
            seriesName: "Wizarding World",
            seriesSequence: "1"));

        Assert.Equal(
            ["J.K. Rowling/Wizarding World/Book 1/Harry Potter and the Sorcerer's Stone.m4b"],
            workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_copies_the_companion_pdf_next_to_the_audio_file()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteSourceFile("a-book.pdf", "pdf-bytes");

        await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(["An Author/A Book/A Book.m4b", "An Author/A Book/A Book.pdf"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_leaves_the_parsed_library_untouched()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var book = TempWorkspace.Book(author: "Jane Doe, John Roe - editor", title: "A Book: Subtitle", shortTitle: "A Book");

        await Sort(workspace, book);

        Assert.Equal("Jane Doe, John Roe - editor", book.Author);
        Assert.Equal("A Book: Subtitle", book.Title);
    }

    [Fact]
    public async Task Sort_is_idempotent_and_copies_nothing_on_a_second_run()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", new string('x', 20_000));

        var first = await Sort(workspace, TempWorkspace.Book());
        var second = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(SortCounts.Empty with { New = 1 }, first.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, second.Counts);
        Assert.Single(workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_replaces_a_destination_file_whose_contents_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "old-audio!");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(SortCounts.Empty with { Updated = 1 }, summary.Counts);
        Assert.Equal("new-audio", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b")));
    }

    [Fact]
    public async Task Sort_does_not_create_folders_for_books_with_no_files()
    {
        using var workspace = new TempWorkspace();

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Empty(workspace.DestinationDirectories());
        Assert.Equal(SortCounts.Empty with { NotFound = 1 }, summary.Counts);
        Assert.Equal(1, summary.ProblemCount);
    }

    [Fact]
    public async Task Sort_names_the_book_and_the_problem_in_plain_language()
    {
        using var workspace = new TempWorkspace();

        var summary = await Sort(workspace, TempWorkspace.Book(title: "We Are Legion (We Are Bob)", author: "Dennis E. Taylor"));

        Assert.Equal(
            new SortProblem(
                SortProblemKind.NotFound,
                "We Are Legion (We Are Bob) — Dennis E. Taylor",
                "No audio file for this book in the source folder"),
            Assert.Single(summary.Problems));
    }

    [Fact]
    public async Task Sort_counts_a_book_with_no_source_file_as_missing_not_as_up_to_date()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("present.m4b");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Present Book", filename: "present"),
            TempWorkspace.Book(title: "Absent Book", filename: "absent"));

        // The distinction is the whole point: reporting a book that was never downloaded as
        // "already up to date" tells someone their library is organised when it is not.
        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_separates_missing_books_from_books_that_are_genuinely_up_to_date()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("present.m4b");

        var books = new[]
        {
            TempWorkspace.Book(title: "Present Book", filename: "present"),
            TempWorkspace.Book(title: "Absent Book", filename: "absent")
        };

        await Sort(workspace, books);
        var second = await Sort(workspace, books);

        Assert.Equal(SortCounts.Empty with { UpToDate = 1, NotFound = 1 }, second.Counts);
    }

    [Fact]
    public async Task Progress_reports_missing_books_while_the_run_is_going()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("present.m4b");

        var reports = new List<SortProgressInfo>();
        var progress = new Progress<SortProgressInfo>(report => { lock (reports) { reports.Add(report); } });

        await Sort(
            workspace,
            progress,
            TempWorkspace.Book(title: "Present Book", filename: "present"),
            TempWorkspace.Book(title: "Absent Book", filename: "absent"));

        await WaitForAsync(() => { lock (reports) { return reports.Any(r => r.CurrentBook == 2); } });

        SortProgressInfo final;
        lock (reports)
        {
            final = reports.Single(r => r.CurrentBook == 2);
        }

        Assert.Equal(1, final.Counts.NotFound);
        Assert.Equal(0, final.Counts.UpToDate);
    }

    [Fact]
    public async Task Sort_keeps_the_only_copy_of_a_missing_book_that_shares_a_title_with_another()
    {
        // As main left two books called "Collected Works"; the first has since gone from the source.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(
            ["An Author/Collected Works (2)/Collected Works.m4b", "An Author/Collected Works.m4b"],
            workspace.DestinationFiles());
        Assert.Equal("first-book", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "Collected Works.m4b")));
        Assert.Equal(SortCounts.Empty with { Moved = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_moves_a_loose_book_whose_same_titled_twin_was_never_downloaded()
    {
        // main gave the plain name to the only "Collected Works" it had a file for.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "second-book");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(["An Author/Collected Works (2)/Collected Works.m4b"], workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { Moved = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_reports_a_book_whose_audio_is_missing_even_when_its_pdf_is_there()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.pdf", "pdf");

        var summary = await Sort(workspace, TempWorkspace.Book(pdf: "a-book.pdf"));

        // Copying the PDF alone would call the book sorted and give library tools a folder with nothing to play.
        Assert.Empty(workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { NotFound = 1 }, summary.Counts);
        Assert.Equal(SortProblemKind.NotFound, Assert.Single(summary.Problems).Kind);
    }

    [Fact]
    public async Task Sort_removes_the_folder_a_book_left_when_it_joined_a_series()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        // Sorted while it was still a standalone book.
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");

        var summary = await Sort(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "1"));

        Assert.Equal(1, summary.Counts.Moved);
        Assert.Equal(["An Author/The Series/Book 1/A Book.m4b"], workspace.DestinationFiles());
        Assert.False(Directory.Exists(Path.Combine(workspace.Destination, "An Author", "A Book")));
    }

    [Fact]
    public async Task Sort_keeps_a_vacated_folder_that_still_holds_something()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "cover.jpg"), "image");

        await Sort(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "1"));

        Assert.Contains("An Author/A Book/cover.jpg", workspace.DestinationFiles());
        Assert.Contains("An Author/The Series/Book 1/A Book.m4b", workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_moves_a_loose_book_from_an_older_version_into_its_own_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        workspace.WriteSourceFile("a-book.pdf", "pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.pdf"), "pdf");
        workspace.WriteSourceFile("series-book.m4b", "series");
        workspace.WriteDestinationFile(Path.Combine("An Author", "The Series", "Book 1", "Series Book.m4b"), "series");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(),
            TempWorkspace.Book(title: "Series Book", filename: "series-book", seriesName: "The Series", seriesSequence: "1"));

        Assert.Equal(
            ["An Author/A Book/A Book.m4b", "An Author/A Book/A Book.pdf", "An Author/The Series/Book 1/Series Book.m4b"],
            workspace.DestinationFiles());
        // The loose book was moved; the series book was already in place.
        Assert.Equal(SortCounts.Empty with { Moved = 1, UpToDate = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_upgrades_an_old_library_so_nothing_is_left_loose_beside_a_series()
    {
        // The layout main wrote for a standalone book named like its author's series, plus a book
        // this version filed standalone before it gained a series named after itself.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        workspace.WriteSourceFile("legion.m4b", "legion");
        workspace.WriteSourceFile("foo.m4b", "foo");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Bobiverse.m4b"), "standalone");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Bobiverse", "Book 1", "We Are Legion.m4b"), "legion");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.m4b"), "foo");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Bobiverse", filename: "standalone"),
            TempWorkspace.Book(title: "We Are Legion", filename: "legion", seriesName: "Bobiverse", seriesSequence: "1"),
            TempWorkspace.Book(title: "Foo", filename: "foo", seriesName: "Foo", seriesSequence: "1"));

        Assert.Equal(
            [
                "An Author/Bobiverse (2)/Bobiverse.m4b",
                "An Author/Bobiverse/Book 1/We Are Legion.m4b",
                "An Author/Foo/Book 1/Foo.m4b"
            ],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { Moved = 2, UpToDate = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_leaves_a_loose_file_that_differs_from_the_source_where_it_is_and_says_so()
    {
        // A stale copy of this book, or the only copy of a same-titled book that has left the
        // export: the export cannot tell which, so the file is kept and the book copied afresh.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "old-audio!");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(["An Author/A Book.m4b", "An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal("old-audio!", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book.m4b")));
        Assert.Equal(SortCounts.Empty with { New = 1 }, summary.Counts);
        var problem = Assert.Single(summary.Problems);
        Assert.Equal(SortProblemKind.Warning, problem.Kind);
        Assert.StartsWith($"Left \"{Path.Combine("An Author", "A Book.m4b")}\" where it is", problem.Message);
    }

    [Fact]
    public async Task Sort_updates_a_moved_loose_book_that_only_the_full_check_finds_changed()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", edited);
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var summary = await new FileSorter().SortAudioFiles(
            workspace.Source,
            workspace.Destination,
            [TempWorkspace.Book()],
            SortOptions.Default with { ComparisonMode = FileComparisonMode.Full });

        Assert.Equal(["An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal(edited, File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b")));
        // Moved and then replaced: counted once, as the stronger of the two.
        Assert.Equal(SortCounts.Empty with { Updated = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_never_overwrites_the_loose_file_of_a_same_titled_book_that_has_left_the_export()
    {
        // As main left two books called "Collected Works"; the first is no longer in the export.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(
            ["An Author/Collected Works.m4b", "An Author/Collected Works/Collected Works.m4b"],
            workspace.DestinationFiles());
        Assert.Equal("first-book", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "Collected Works.m4b")));
        // Its own "(2)" was moved; the plain name the export now gives it is still loose, and said so.
        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
        Assert.Equal(SortProblemKind.Warning, Assert.Single(summary.Problems).Kind);
    }

    [Fact]
    public async Task Sort_never_overwrites_a_missing_standalone_books_loose_file_with_a_series_book_of_the_same_title()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("dune-series.m4b", "series-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune.m4b"), "standalone-edition");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Dune", filename: "dune-standalone"),
            TempWorkspace.Book(title: "Dune", filename: "dune-series", seriesName: "Dune Chronicles", seriesSequence: "1"));

        Assert.Equal(
            ["An Author/Dune Chronicles/Book 1/Dune.m4b", "An Author/Dune.m4b"],
            workspace.DestinationFiles());
        Assert.Equal("standalone-edition", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "Dune.m4b")));
        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_keeps_the_downloaded_edition_when_an_undownloaded_one_has_the_same_series_number()
    {
        // main put the only downloaded edition in "Book 1"; the other edition is listed first.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("hp1-dale.m4b", "dale-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Harry Potter", "Book 1", "A Book.m4b"), "dale-edition");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(filename: "hp1-fry", seriesName: "Harry Potter", seriesSequence: "1"),
            TempWorkspace.Book(filename: "hp1-dale", seriesName: "Harry Potter", seriesSequence: "1"));

        Assert.Equal(["An Author/Harry Potter/Book 1/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { UpToDate = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_never_replaces_a_missing_books_only_copy_with_a_same_titled_book_listed_before_it()
    {
        using var workspace = new TempWorkspace();
        var first = TempWorkspace.Book(title: "Foo", filename: "a");
        workspace.WriteSourceFile("a.m4b", "a-edition");
        await Sort(workspace, first);
        // Deleted from OpenAudible's folder, so the organised copy is the only one; then a different
        // edition is bought, and listed first.
        File.Delete(Path.Combine(workspace.Source, "a.m4b"));
        workspace.WriteSourceFile("b.m4b", "b-edition");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "b"), first);

        Assert.Equal(["An Author/Foo (2)/Foo.m4b", "An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal("a-edition", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = 1 }, summary.Counts);
        Assert.Contains(
            summary.Problems,
            problem => problem.Kind == SortProblemKind.Warning &&
                       problem.Message.StartsWith($"Left \"{Path.Combine("An Author", "Foo")}\" alone"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sort_keeps_same_titled_books_in_their_own_folders_when_the_export_order_changes(bool firstLeftTheSource)
    {
        using var workspace = new TempWorkspace();
        var first = TempWorkspace.Book(title: "Foo", filename: "a");
        var second = TempWorkspace.Book(title: "Foo", filename: "b");
        workspace.WriteSourceFile("a.m4b", "a-edition");
        workspace.WriteSourceFile("b.m4b", "b-edition");
        await Sort(workspace, first, second);
        if (firstLeftTheSource)
        {
            File.Delete(Path.Combine(workspace.Source, "a.m4b"));
        }

        var summary = await Sort(workspace, second, first);

        Assert.Equal(["An Author/Foo (2)/Foo.m4b", "An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal("a-edition", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal("b-edition", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.m4b"));
        Assert.Equal(
            firstLeftTheSource ? SortCounts.Empty with { UpToDate = 1, NotFound = 1 } : SortCounts.Empty with { UpToDate = 2 },
            summary.Counts);
    }

    [Fact]
    public async Task Sort_keeps_books_with_the_same_series_number_in_their_own_folders_when_the_export_order_changes()
    {
        using var workspace = new TempWorkspace();
        var first = TempWorkspace.Book(title: "Xbook", filename: "x", seriesName: "Saga", seriesSequence: "2");
        var second = TempWorkspace.Book(title: "Ybook", filename: "y", seriesName: "Saga", seriesSequence: "2");
        workspace.WriteSourceFile("x.m4b", "x-edition");
        workspace.WriteSourceFile("y.m4b", "y-edition");
        await Sort(workspace, first, second);

        var summary = await Sort(workspace, second, first);

        // Swapping them would leave one "Book 2 (2)" holding both books, which a library tool reads as one.
        Assert.Equal(
            ["An Author/Saga/Book 2 (2)/Ybook.m4b", "An Author/Saga/Book 2/Xbook.m4b"],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { UpToDate = 2 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_never_replaces_the_only_copy_of_a_missing_book_whose_series_number_another_book_now_has()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("x.m4b", "x-edition");
        workspace.WriteSourceFile("y.m4b", "y-edition");
        await Sort(
            workspace,
            TempWorkspace.Book(title: "Same", filename: "x", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Same", filename: "y", seriesName: "Saga", seriesSequence: "3"));
        File.Delete(Path.Combine(workspace.Source, "y.m4b"));

        // The series is renumbered: X is now 3, and Y, gone from the source, is 4.
        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Same", filename: "x", seriesName: "Saga", seriesSequence: "3"),
            TempWorkspace.Book(title: "Same", filename: "y", seriesName: "Saga", seriesSequence: "4"));

        Assert.Equal("y-edition", ReadDestination(workspace, "An Author", "Saga", "Book 3", "Same.m4b"));
        Assert.Equal("x-edition", ReadDestination(workspace, "An Author", "Saga", "Book 3 (2)", "Same.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_moves_a_standalone_book_out_of_the_folder_a_new_series_named_like_it_takes()
    {
        using var workspace = new TempWorkspace();
        var standalone = TempWorkspace.Book(title: "Bobiverse", filename: "standalone");
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        workspace.WriteSourceFile("legion.m4b", "legion");
        await Sort(workspace, standalone);

        var summary = await Sort(
            workspace,
            standalone,
            TempWorkspace.Book(title: "We Are Legion", filename: "legion", seriesName: "Bobiverse", seriesSequence: "1"));

        // Left in "Bobiverse", its audio would lie loose beside "Book 1", which hides the series from
        // library tools, and it would have been copied a second time into "Bobiverse (2)".
        Assert.Equal(
            ["An Author/Bobiverse (2)/Bobiverse.m4b", "An Author/Bobiverse/Book 1/We Are Legion.m4b"],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { New = 1, Moved = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_never_replaces_a_good_copy_with_an_empty_source_file()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "complete-audio");
        await Sort(workspace, TempWorkspace.Book());
        // What OpenAudible leaves while it downloads or converts the book again, or when that failed.
        workspace.WriteSourceFile("a-book.m4b", "");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal("complete-audio", ReadDestination(workspace, "An Author", "A Book", "A Book.m4b"));
        Assert.Equal(SortCounts.Empty with { NotFound = 1 }, summary.Counts);
        Assert.StartsWith("The book's file in the source folder is empty", Assert.Single(summary.Problems).Message);
    }

    [Fact]
    public async Task Sort_keeps_going_when_one_book_cannot_be_written()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("good.m4b");
        workspace.WriteSourceFile("blocked.m4b");

        // A folder sitting where the file should go makes the copy fail for that book only.
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Blocked Book", "Blocked Book.m4b"));

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Good Book", filename: "good"),
            TempWorkspace.Book(title: "Blocked Book", filename: "blocked"));

        Assert.Equal(2, summary.TotalBooks);
        Assert.Equal(SortCounts.Empty with { New = 1, Failed = 1 }, summary.Counts);
        Assert.Contains("An Author/Good Book/Good Book.m4b", workspace.DestinationFiles());

        var problem = Assert.Single(summary.Problems);
        Assert.Equal(SortProblemKind.Failed, problem.Kind);
        Assert.Equal("Blocked Book — An Author", problem.Book);
        Assert.StartsWith("Could not copy the file: ", problem.Message);
    }

    [Fact]
    public async Task Sort_leaves_no_partial_file_behind_when_a_copy_fails()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("blocked.m4b");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Blocked Book", "Blocked Book.m4b"));

        await Sort(workspace, TempWorkspace.Book(title: "Blocked Book", filename: "blocked"));

        Assert.DoesNotContain(workspace.DestinationFiles(), file => file.Contains(".oabo-partial"));
    }

    [Fact]
    public async Task Sort_writes_no_partial_file_when_cancelled()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < 200; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b", new string('x', 200_000));
        }

        var books = Enumerable.Range(0, 200)
            .Select(i => TempWorkspace.Book(title: $"Book {i}", filename: $"book-{i}"))
            .ToList();

        // Cancel as soon as the first book is done. A timer raced the copies, and on a fast disk
        // the whole library could finish first.
        using var cancellation = new CancellationTokenSource();
        var sortTask = new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, books,
            progress: new CancelOnFirstReport(cancellation), cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sortTask);

        Assert.DoesNotContain(workspace.DestinationFiles(), file => file.Contains(".oabo-partial"));

        // Everything that did land must be complete, not truncated.
        foreach (var file in Directory.GetFiles(workspace.Destination, "*.m4b", SearchOption.AllDirectories))
        {
            Assert.Equal(200_000, new FileInfo(file).Length);
        }
    }

    [Fact]
    public async Task Sort_reports_completion_for_an_empty_library()
    {
        using var workspace = new TempWorkspace();
        var reports = new List<SortProgressInfo>();

        var summary = await new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, [], progress: new Progress<SortProgressInfo>(reports.Add));

        Assert.Equal(0, summary.TotalBooks);
        await WaitForAsync(() => reports.Any(r => r.Percentage == 100));
        Assert.All(reports, report => Assert.False(double.IsNaN(report.Percentage)));
    }

    [Fact]
    public async Task Sort_rejects_a_missing_source_folder_instead_of_silently_doing_nothing()
    {
        using var workspace = new TempWorkspace();

        var error = await Assert.ThrowsAsync<SortPathException>(() => new FileSorter().SortAudioFiles(
            Path.Combine(workspace.Root, "does-not-exist"), workspace.Destination, [TempWorkspace.Book()]));

        Assert.Equal(SortPathField.Source, error.Problem.Field);
        Assert.Equal(SortPathProblemCode.NotFound, error.Problem.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sort_rejects_a_missing_path(string? path)
    {
        using var workspace = new TempWorkspace();

        var missingSource = await Assert.ThrowsAsync<SortPathException>(() => new FileSorter().SortAudioFiles(
            path!, workspace.Destination, [TempWorkspace.Book()]));
        Assert.Equal(new { Field = SortPathField.Source, Code = SortPathProblemCode.NotSet },
            new { missingSource.Problem.Field, missingSource.Problem.Code });

        var missingDestination = await Assert.ThrowsAsync<SortPathException>(() => new FileSorter().SortAudioFiles(
            workspace.Source, path!, [TempWorkspace.Book()]));
        Assert.Equal(new { Field = SortPathField.Destination, Code = SortPathProblemCode.NotSet },
            new { missingDestination.Problem.Field, missingDestination.Problem.Code });
    }

    [Fact]
    public async Task Sort_creates_a_missing_destination_folder_only_when_asked_to()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var destination = Path.Combine(workspace.Root, "new-destination");

        await new FileSorter().SortAudioFiles(
            workspace.Source, destination, [TempWorkspace.Book()], new SortOptions { CreateDestination = true });

        Assert.True(File.Exists(Path.Combine(destination, "An Author", "A Book", "A Book.m4b")));
    }

    [Fact]
    public async Task Sort_refuses_a_missing_destination_folder_by_default()
    {
        // Usually an unplugged drive: creating the folder would fill the internal disk instead.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var destination = Path.Combine(workspace.Root, "unplugged-drive");

        var error = await Assert.ThrowsAsync<SortPathException>(() =>
            new FileSorter().SortAudioFiles(workspace.Source, destination, [TempWorkspace.Book()]));

        Assert.Equal(SortPathProblemCode.DestinationMissing, error.Problem.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Sort_never_writes_outside_the_destination_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var escapeTarget = Path.Combine(workspace.Root, "escaped.m4b");

        await Sort(workspace, TempWorkspace.Book(author: "..", title: "..\\..\\escaped", seriesName: ".."));

        Assert.False(File.Exists(escapeTarget));
        Assert.All(
            Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories),
            file => Assert.True(
                file.StartsWith(workspace.Source, StringComparison.Ordinal) ||
                file.StartsWith(workspace.Destination, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Sort_reports_progress_that_ends_at_one_hundred_percent()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var reports = new List<SortProgressInfo>();

        await Sort(workspace, new Progress<SortProgressInfo>(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        }), TempWorkspace.Book());

        await WaitForAsync(() =>
        {
            lock (reports)
            {
                return reports.Any(r => r.Percentage == 100);
            }
        });

        lock (reports)
        {
            Assert.All(reports, report => Assert.InRange(report.Percentage, 0, 100));
        }
    }

    [Fact]
    public async Task Every_progress_report_is_a_consistent_snapshot()
    {
        // Books finish concurrently; a report pairing one worker's count with another's total
        // would show numbers that do not add up.
        using var workspace = new TempWorkspace();
        var books = new List<OpenAudible>();
        for (var i = 0; i < 60; i++)
        {
            if (i % 3 != 0)
            {
                workspace.WriteSourceFile($"book-{i}.m4b", $"content-{i}");
            }

            books.Add(TempWorkspace.Book(title: $"Book {i}", filename: $"book-{i}"));
        }

        var reports = new List<SortProgressInfo>();
        await new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, books,
            new SortOptions { MaxParallelism = 8 }, new InlineProgress(reports.Add));

        Assert.All(reports, report => Assert.Equal(report.CurrentBook, report.Counts.Total));
        Assert.All(reports, report => Assert.Equal(report.Counts.NotFound, report.ProblemCount));
        Assert.Equal(Enumerable.Range(0, 61), reports.Select(report => report.CurrentBook));
    }

    [Fact]
    public async Task Sort_copies_a_book_listed_twice_once_and_does_not_call_the_repeat_missing()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var summary = await Sort(workspace, TempWorkspace.Book(), TempWorkspace.Book());

        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task Sort_files_a_book_listed_twice_under_two_titles_once_and_keeps_it_that_way()
    {
        // The same book from two accounts or regions, one listing's title spelled differently.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        OpenAudible[] books = [TempWorkspace.Book(title: "Alpha"), TempWorkspace.Book(title: "Beta")];

        var first = await Sort(workspace, books);
        var second = await Sort(workspace, books);

        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, first.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 2 }, second.Counts);
        Assert.Equal(["An Author/Alpha/Alpha.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_handles_a_large_library_across_many_authors_without_duplicating_folders()
    {
        using var workspace = new TempWorkspace();
        var books = new List<OpenAudible>();

        for (var i = 0; i < 120; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b", $"content-{i}");
            books.Add(TempWorkspace.Book(
                // Alternating spellings of the same three authors.
                author: (i % 3) switch
                {
                    0 => i % 2 == 0 ? "J.K. Rowling" : "JK Rowling",
                    1 => i % 2 == 0 ? "Brandon Sanderson" : "Brandon  Sanderson",
                    _ => "Terry Pratchett"
                },
                title: $"Book {i}",
                filename: $"book-{i}"));
        }

        var summary = await new FileSorter().SortAudioFiles(workspace.Source, workspace.Destination, books);

        Assert.Equal(SortCounts.Empty with { New = 120 }, summary.Counts);
        Assert.Equal(3, Directory.GetDirectories(workspace.Destination).Length);
        Assert.Equal(120, workspace.DestinationFiles().Length);
    }

    /// <summary>Cancels as soon as the first book is done (not at the report made when the run starts).</summary>
    private sealed class CancelOnFirstReport(CancellationTokenSource cancellation) : IProgress<SortProgressInfo>
    {
        public void Report(SortProgressInfo value)
        {
            if (value.CurrentBook > 0)
            {
                cancellation.Cancel();
            }
        }
    }

    /// <summary>Reports on the calling thread, in the order the sorter made the reports.</summary>
    private sealed class InlineProgress(Action<SortProgressInfo> handler) : IProgress<SortProgressInfo>
    {
        public void Report(SortProgressInfo value) => handler(value);
    }

    private static string ReadDestination(TempWorkspace workspace, params string[] parts)
    {
        return File.ReadAllText(Path.Combine([workspace.Destination, .. parts]));
    }

    private static Task<SortSummary> Sort(TempWorkspace workspace, params OpenAudible[] books)
    {
        return Sort(workspace, null, books);
    }

    private static Task<SortSummary> Sort(TempWorkspace workspace, IProgress<SortProgressInfo>? progress, params OpenAudible[] books)
    {
        return new FileSorter().SortAudioFiles(workspace.Source, workspace.Destination, [.. books], progress: progress);
    }

    /// <summary>
    /// <see cref="Progress{T}"/> dispatches on the thread pool, so the last report can arrive
    /// just after the sort returns.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for the expected progress report.");
    }
}
