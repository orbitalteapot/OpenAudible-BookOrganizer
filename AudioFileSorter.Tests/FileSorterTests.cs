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
    public async Task A_second_sort_changes_nothing_and_the_manifest_records_every_book()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        workspace.WriteSourceFile("standalone.pdf", "pdf");
        workspace.WriteSourceFile("numbered.m4b", "numbered");
        workspace.WriteSourceFile("unnumbered.m4b", "unnumbered");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Standalone", filename: "standalone", pdf: "standalone.pdf", asin: "B01"),
            TempWorkspace.Book(title: "Numbered", filename: "numbered", seriesName: "Saga", seriesSequence: "1", asin: "B02"),
            TempWorkspace.Book(title: "Unnumbered", filename: "unnumbered", seriesName: "Saga")
        ];

        await Sort(workspace, books);
        var files = workspace.DestinationFiles();
        var second = await Sort(workspace, books);

        Assert.Equal(SortCounts.Empty with { UpToDate = 3 }, second.Counts);
        Assert.Empty(second.Problems);
        Assert.Equal(files, workspace.DestinationFiles());

        var manifest = LibraryManifest.Load(workspace.Destination);
        Assert.Equal(["b01", "b02", "file:unnumbered.m4b"], manifest.Books.Keys.Order(StringComparer.Ordinal));
        var standalone = manifest.Get("b01")!;
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Standalone"), standalone.Folder);
        Assert.Equal(["Standalone.m4b", "Standalone.pdf"], standalone.Files);
        Assert.Equal("Standalone — An Author", standalone.Title);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Saga", "Book 1"), manifest.Get("b02")!.Folder);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Saga", "Unnumbered"), manifest.Get("file:unnumbered.m4b")!.Folder);
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
            ["An Author/Collected Works.m4b", "An Author/Collected Works/Collected Works.m4b"],
            workspace.DestinationFiles());
        Assert.Equal("first-book", ReadDestination(workspace, "An Author", "Collected Works.m4b"));
        Assert.Equal("second-book", ReadDestination(workspace, "An Author", "Collected Works", "Collected Works.m4b"));
        Assert.Equal(SortCounts.Empty with { Moved = 1, NotFound = 1 }, summary.Counts);
        Assert.Single(summary.Problems, problem => problem.Kind == SortProblemKind.Warning);
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

        Assert.Equal(["An Author/Collected Works/Collected Works.m4b"], workspace.DestinationFiles());
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
    public async Task Sort_moves_a_book_that_joined_a_series_and_removes_the_folder_it_left()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        workspace.WriteSourceFile("a-book.pdf", "pdf");
        await Sort(workspace, TempWorkspace.Book(pdf: "a-book.pdf"));

        var summary = await Sort(workspace, TempWorkspace.Book(pdf: "a-book.pdf", seriesName: "The Series", seriesSequence: "1"));

        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
        Assert.Equal(["An Author/The Series/Book 1/A Book.m4b", "An Author/The Series/Book 1/A Book.pdf"], workspace.DestinationFiles());
        Assert.False(Directory.Exists(Path.Combine(workspace.Destination, "An Author", "A Book")));
    }

    [Fact]
    public async Task Sort_moves_a_series_book_that_gained_a_number()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book(seriesName: "The Series"));

        var summary = await Sort(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "2"));

        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
        Assert.Equal(["An Author/The Series/Book 2/A Book.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_moves_and_renames_a_book_whose_title_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book(title: "Old Name", asin: "B01"));

        var summary = await Sort(workspace, TempWorkspace.Book(title: "New Name", asin: "B01"));
        var again = await Sort(workspace, TempWorkspace.Book(title: "New Name", asin: "B01"));

        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, again.Counts);
        Assert.Equal(["An Author/New Name/New Name.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task A_re_release_with_a_new_title_replaces_the_one_copy_and_counts_as_updated()
    {
        // As the README's "Keeping books up to date" says: the copy on record moves, is renamed, then replaced.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "first-edition");
        await Sort(workspace, TempWorkspace.Book(title: "The Martian", asin: "B004"));
        workspace.WriteSourceFile("a-book.m4b", "classroom-edition!");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "The Martian (Classroom Edition)", asin: "B004"));

        Assert.Equal(SortCounts.Empty with { Updated = 1 }, summary.Counts);
        Assert.Equal(["An Author/The Martian (Classroom Edition)/The Martian (Classroom Edition).m4b"], workspace.DestinationFiles());
        Assert.Equal("classroom-edition!", ReadDestination(workspace, "An Author", "The Martian (Classroom Edition)", "The Martian (Classroom Edition).m4b"));
        Assert.False(Directory.Exists(Path.Combine(workspace.Destination, "An Author", "The Martian")));
    }

    [Fact]
    public async Task Sort_keeps_a_book_where_it_is_when_its_author_is_spelled_differently()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book(author: "J.K. Rowling"));

        var summary = await Sort(workspace, TempWorkspace.Book(author: "JK Rowling"));

        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, summary.Counts);
        Assert.Equal(["J.K. Rowling/A Book/A Book.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_keeps_a_vacated_folder_that_still_holds_something()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book());
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "cover.jpg"), "image");

        await Sort(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "1"));

        Assert.Equal(["An Author/A Book/cover.jpg", "An Author/The Series/Book 1/A Book.m4b"], workspace.DestinationFiles());
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
    public async Task Sort_moves_a_loose_file_only_one_book_could_have_left_and_updates_it()
    {
        // No other book in the export has this title, so the old file is this book's older download.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "old-audio!");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(["An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal("new-audio", ReadDestination(workspace, "An Author", "A Book", "A Book.m4b"));
        Assert.Equal(SortCounts.Empty with { Updated = 1 }, summary.Counts);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task Upgrade_tidies_up_the_layout_an_older_version_left()
    {
        // What main wrote: standalone books loose in the author folder, series books without a
        // number loose in the series folder, numbered ones in "Book N", and same-named files told
        // apart by " (2)" in list order, which has changed since. "(3)" is a book no longer listed.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        workspace.WriteSourceFile("standalone.pdf", "standalone-pdf");
        workspace.WriteSourceFile("novella.m4b", "novella");
        workspace.WriteSourceFile("one.m4b", "one");
        workspace.WriteSourceFile("works-a.m4b", "works-a");
        workspace.WriteSourceFile("works-b.m4b", "works-b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Standalone.m4b"), "standalone");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Standalone.pdf"), "standalone-pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Novella.m4b"), "novella");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 1", "One.m4b"), "one");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "works-b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "works-a");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (3).m4b"), "works-gone");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Standalone", filename: "standalone", pdf: "standalone.pdf"),
            TempWorkspace.Book(title: "Novella", filename: "novella", seriesName: "Saga"),
            TempWorkspace.Book(title: "One", filename: "one", seriesName: "Saga", seriesSequence: "1"),
            TempWorkspace.Book(title: "Collected Works", filename: "works-a"),
            TempWorkspace.Book(title: "Collected Works", filename: "works-b")
        ];

        var summary = await Sort(workspace, books);

        Assert.Equal(
            [
                "An Author/Collected Works (2)/Collected Works.m4b",
                "An Author/Collected Works (3).m4b",
                "An Author/Collected Works/Collected Works.m4b",
                "An Author/Saga/Book 1/One.m4b",
                "An Author/Saga/Novella/Novella.m4b",
                "An Author/Standalone/Standalone.m4b",
                "An Author/Standalone/Standalone.pdf"
            ],
            workspace.DestinationFiles());
        Assert.Equal("works-a", ReadDestination(workspace, "An Author", "Collected Works", "Collected Works.m4b"));
        Assert.Equal("works-b", ReadDestination(workspace, "An Author", "Collected Works (2)", "Collected Works.m4b"));
        Assert.Equal(SortCounts.Empty with { Moved = 4, UpToDate = 1 }, summary.Counts);
        var warning = Assert.Single(summary.Problems);
        Assert.Equal(SortProblemKind.Warning, warning.Kind);
        Assert.Equal(
            $"Left \"{Path.Combine("An Author", "Collected Works (3).m4b")}\" where it was: it matches none of the books in the export. " +
            "If it is an old copy, delete it.",
            warning.Message);

        var again = await Sort(workspace, books);
        Assert.Equal(SortCounts.Empty with { UpToDate = 5 }, again.Counts);
        Assert.Equal(7, workspace.DestinationFiles().Length);
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
    }

    [Fact]
    public async Task A_new_book_never_takes_the_folder_of_a_same_titled_book_that_has_left_the_export()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-edition");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "a", asin: "A"));
        workspace.WriteSourceFile("b.m4b", "b-edition");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "b", asin: "B"));

        Assert.Equal(["An Author/Foo (2)/Foo.m4b", "An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal("a-edition", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1 }, summary.Counts);
        Assert.Empty(summary.Problems);
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

        Assert.Equal(["An Author/Saga/Book 3 (2)/Same.m4b", "An Author/Saga/Book 3/Same.m4b"], workspace.DestinationFiles());
        Assert.Equal("y-edition", ReadDestination(workspace, "An Author", "Saga", "Book 3", "Same.m4b"));
        Assert.Equal("x-edition", ReadDestination(workspace, "An Author", "Saga", "Book 3 (2)", "Same.m4b"));
        Assert.Equal(SortCounts.Empty with { Moved = 1, NotFound = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Upgrade_never_writes_a_book_over_the_missing_same_titled_book_it_shared_a_book_folder_with()
    {
        // main's layout: both narrations in "Book 2", the second as "Alpha (2)". The first is not in
        // the source, and the export now lists the second first.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("n2.m4b", "narration-2");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 2", "Alpha.m4b"), "narration-1");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 2", "Alpha (2).m4b"), "narration-2");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "n2", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Alpha", filename: "n1", seriesName: "Saga", seriesSequence: "2"));

        Assert.Equal(["An Author/Saga/Book 2 (2)/Alpha.m4b", "An Author/Saga/Book 2/Alpha.m4b"], workspace.DestinationFiles());
        Assert.Equal("narration-1", ReadDestination(workspace, "An Author", "Saga", "Book 2", "Alpha.m4b"));
        Assert.Equal("narration-2", ReadDestination(workspace, "An Author", "Saga", "Book 2 (2)", "Alpha.m4b"));
        Assert.Equal(SortCounts.Empty with { Moved = 1, NotFound = 1 }, summary.Counts);
        Assert.Contains(Path.Combine("An Author", "Saga", "Book 2", "Alpha.m4b"), Assert.Single(summary.Problems, p => p.Kind == SortProblemKind.Warning).Message);

        var second = await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "n2", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Alpha", filename: "n1", seriesName: "Saga", seriesSequence: "2"));
        Assert.Equal(SortCounts.Empty with { UpToDate = 1, NotFound = 1 }, second.Counts);
    }

    [Fact]
    public async Task Upgrade_never_writes_a_book_over_an_updated_same_titled_book_it_shared_a_book_folder_with()
    {
        // The first narration was downloaded again, so it cannot tell "Book 2" is its and goes elsewhere;
        // the second must not take the plain name it leaves behind, and hold "Book 2" twice.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("n1.m4b", "narration-1-redownloaded");
        workspace.WriteSourceFile("n2.m4b", "narration-2");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 2", "Alpha.m4b"), "narration-1");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 2", "Alpha (2).m4b"), "narration-2");

        await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "n1", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Alpha", filename: "n2", seriesName: "Saga", seriesSequence: "2"));

        Assert.Equal("narration-1", ReadDestination(workspace, "An Author", "Saga", "Book 2", "Alpha.m4b"));
        Assert.Equal(
            ["narration-1", "narration-1-redownloaded", "narration-2"],
            workspace.DestinationFiles().Select(file => File.ReadAllText(Path.Combine(workspace.Destination, file))).Order());

        var second = await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "n1", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Alpha", filename: "n2", seriesName: "Saga", seriesSequence: "2"));
        Assert.Equal(SortCounts.Empty with { UpToDate = 2 }, second.Counts);
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
    public async Task Sort_never_files_a_book_loose_in_the_folder_of_a_series_that_has_left_the_export()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "first");
        workspace.WriteSourceFile("second.m4b", "second");
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        await Sort(
            workspace,
            TempWorkspace.Book(title: "First", filename: "first", seriesName: "Witcher", seriesSequence: "1"),
            TempWorkspace.Book(title: "Second", filename: "second", seriesName: "Witcher", seriesSequence: "2"));

        // The series is gone from the export, and a book titled like it is new.
        var summary = await Sort(workspace, TempWorkspace.Book(title: "Witcher", filename: "standalone"));
        var again = await Sort(workspace, TempWorkspace.Book(title: "Witcher", filename: "standalone"));

        // Loose beside "Book 1" and "Book 2", Audiobookshelf would read the whole series as this book.
        Assert.Equal(
            ["An Author/Witcher (2)/Witcher.m4b", "An Author/Witcher/Book 1/First.m4b", "An Author/Witcher/Book 2/Second.m4b"],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { New = 1 }, summary.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, again.Counts);
    }

    [Fact]
    public async Task Sort_moves_a_book_out_of_a_series_folder_it_was_filed_loose_in()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b", "standalone");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Witcher", "Book 1", "First.m4b"), "first");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Witcher", "Witcher.m4b"), "standalone");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Witcher", filename: "standalone"));

        Assert.Equal(
            ["An Author/Witcher (2)/Witcher.m4b", "An Author/Witcher/Book 1/First.m4b"],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
    }

    [Fact]
    public async Task Sort_never_files_a_series_in_the_folder_of_a_book_that_has_left_the_export()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("blood-of-elves.m4b", "elves");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Witcher", "Witcher.m4b"), "standalone");

        await Sort(
            workspace,
            TempWorkspace.Book(title: "Blood of Elves", filename: "blood-of-elves", seriesName: "The Witcher", seriesSequence: "1"));

        Assert.Equal(
            ["An Author/The Witcher/Book 1/Blood of Elves.m4b", "An Author/Witcher/Witcher.m4b"],
            workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_copies_two_books_whose_files_differ_only_in_case_on_a_case_sensitive_disk()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("It.m4b", "king");
        workspace.WriteSourceFile("IT.m4b", "other");
        if (File.ReadAllText(Path.Combine(workspace.Source, "It.m4b")) != "king")
        {
            // A case-insensitive disk (Windows, macOS) cannot hold both files.
            return;
        }

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "It", author: "Stephen King", filename: "It"),
            TempWorkspace.Book(title: "IT: Sisters", author: "Someone Else", filename: "IT"));

        Assert.Equal(SortCounts.Empty with { New = 2 }, summary.Counts);
        Assert.Equal("king", ReadDestination(workspace, "Stephen King", "It", "It.m4b"));
        Assert.Equal("other", ReadDestination(workspace, "Someone Else", "IT Sisters", "IT Sisters.m4b"));
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

    [Fact]
    public async Task Sort_copies_rows_with_the_same_asin_once()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "audio");
        workspace.WriteSourceFile("second.m4b", "audio");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Alpha", filename: "first", asin: "B01"),
            TempWorkspace.Book(title: "Beta", filename: "second", asin: "B01")
        ];

        var first = await Sort(workspace, books);
        var second = await Sort(workspace, books);

        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, first.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 2 }, second.Counts);
        Assert.Equal(["An Author/Alpha/Alpha.m4b"], workspace.DestinationFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deleted_manifest_is_rebuilt_from_the_books_in_their_folders(bool orderChanged)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-edition");
        workspace.WriteSourceFile("b.m4b", "b-edition");
        workspace.WriteSourceFile("x.m4b", "x-edition");
        workspace.WriteSourceFile("y.m4b", "y-edition");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Foo", filename: "a"),
            TempWorkspace.Book(title: "Foo", filename: "b"),
            TempWorkspace.Book(title: "Xbook", filename: "x", seriesName: "Saga", seriesSequence: "2"),
            TempWorkspace.Book(title: "Ybook", filename: "y", seriesName: "Saga", seriesSequence: "2")
        ];
        await Sort(workspace, books);
        var files = workspace.DestinationFiles();
        var recorded = LibraryManifest.Load(workspace.Destination).Books.ToDictionary(pair => pair.Key, pair => pair.Value.Folder);
        File.Delete(Path.Combine(workspace.Destination, LibraryManifest.FileName));

        var summary = await Sort(workspace, orderChanged ? books.Reverse().ToArray() : books);

        Assert.Equal(SortCounts.Empty with { UpToDate = 4 }, summary.Counts);
        Assert.Equal(files, workspace.DestinationFiles());
        Assert.Equal(recorded, LibraryManifest.Load(workspace.Destination).Books.ToDictionary(pair => pair.Key, pair => pair.Value.Folder));
    }

    [Fact]
    public async Task A_deleted_manifest_never_costs_a_book_that_has_left_the_export_its_copy()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-edition");
        workspace.WriteSourceFile("b.m4b", "b-edition");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "a"), TempWorkspace.Book(title: "Foo", filename: "b"));
        File.Delete(Path.Combine(workspace.Destination, LibraryManifest.FileName));

        // The first has been returned, so only the second is listed; "Foo" holds the first one's copy.
        var summary = await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "b"));

        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, summary.Counts);
        Assert.Equal(["An Author/Foo (2)/Foo.m4b", "An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal("a-edition", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
    }

    [Theory]
    [InlineData("OpenAudible Book Organizer sorts into this folder. Automatic sorts look for this file.\n")]
    [InlineData("{ \"format\": \"openaudible-organizer-manifest\", ")]
    [InlineData("{ \"format\": \"openaudible-organizer-manifest\", \"version\": 2, \"books\": {} }")]
    public async Task A_manifest_that_cannot_be_read_is_rebuilt_and_said_so(string content)
    {
        // The first is the plain marker this app left before it kept a manifest; the last a newer version's.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book());
        File.WriteAllText(Path.Combine(workspace.Destination, LibraryManifest.FileName), content);

        var summary = await Sort(workspace, TempWorkspace.Book());
        var again = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(SortCounts.Empty with { UpToDate = 1 }, summary.Counts);
        var problem = Assert.Single(summary.Problems);
        Assert.Equal(new { Kind = SortProblemKind.Warning, Book = "Destination folder" }, new { problem.Kind, problem.Book });
        Assert.Contains("rebuilt", problem.Message);

        // Kept rather than written over: it may be a newer version's record, or one a person wants back.
        var kept = Assert.Single(workspace.DestinationFiles(), file => file.StartsWith(LibraryManifest.FileName + ".unreadable-"));
        Assert.Contains(kept, problem.Message);
        Assert.Equal(content, File.ReadAllText(Path.Combine(workspace.Destination, kept)));
        Assert.Equal([kept, "An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Empty(again.Problems);
        Assert.NotNull(LibraryManifest.Load(workspace.Destination).Get("file:a-book.m4b"));
    }

    [Fact]
    public async Task A_cancelled_sort_still_records_the_books_it_finished()
    {
        using var workspace = new TempWorkspace();
        var books = Enumerable.Range(0, 200)
            .Select(i =>
            {
                workspace.WriteSourceFile($"book-{i}.m4b", new string('x', 200_000));
                return TempWorkspace.Book(title: $"Book {i}", filename: $"book-{i}");
            })
            .ToList();

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, books,
            progress: new CancelOnFirstReport(cancellation), cancellationToken: cancellation.Token));

        var recorded = LibraryManifest.Load(workspace.Destination).Books.Values
            .SelectMany(entry => entry.Files.Select(name => Path.GetRelativePath(workspace.Destination, Path.Combine(entry.Folder, name))))
            .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);
        Assert.NotEmpty(workspace.DestinationFiles());
        Assert.Equal(workspace.DestinationFiles(), recorded);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_saved_is_reported_without_failing_the_sort()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        // A folder where the manifest's temporary file goes makes the save fail.
        Directory.CreateDirectory(Path.Combine(workspace.Destination, LibraryManifest.FileName + ".tmp"));

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(SortCounts.Empty with { New = 1 }, summary.Counts);
        var problem = Assert.Single(summary.Problems);
        Assert.Equal(SortProblemKind.Warning, problem.Kind);
        Assert.StartsWith("Could not save its record of which book is in which folder", problem.Message);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_right_now_stops_the_sort_and_is_left_as_it_is()
    {
        // "Foo" is the only copy of a book that has left the export. Had the locked manifest been
        // taken for an empty one and saved over, "Foo" would have been given to the new edition.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("foo.m4b", "first-edition");
        workspace.WriteSourceFile("other.m4b", "other");
        var other = TempWorkspace.Book(title: "Other", filename: "other", asin: "O");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X"), other);
        File.Delete(Path.Combine(workspace.Source, "foo.m4b"));
        var manifestPath = Path.Combine(workspace.Destination, LibraryManifest.FileName);
        var recorded = File.ReadAllText(manifestPath);

        using (new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => Sort(workspace, other));
            Assert.StartsWith("Could not read its record of which book is in which folder", error.Message);
        }

        Assert.Equal(recorded, File.ReadAllText(manifestPath));
        workspace.WriteSourceFile("foo2.m4b", "second-edition");
        await Sort(workspace, other, TempWorkspace.Book(title: "Foo", filename: "foo2", asin: "Y"));
        Assert.Equal("first-edition", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal("second-edition", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.m4b"));
    }

    [Fact]
    public async Task A_book_keeps_its_pdf_on_record_after_the_pdf_leaves_the_source()
    {
        using var workspace = new TempWorkspace();
        var first = TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X", pdf: "Yes");
        workspace.WriteSourceFile("foo.m4b", "x-audio");
        workspace.WriteSourceFile("foo.pdf", "x-pdf");
        await Sort(workspace, first);
        File.Delete(Path.Combine(workspace.Source, "foo.pdf"));
        await Sort(workspace, first);

        // A same-titled edition with a PDF of its own must not take the first one's PDF for its old copy.
        workspace.WriteSourceFile("foo2.m4b", "y-audio");
        workspace.WriteSourceFile("foo2.pdf", "y-pdf");
        await Sort(workspace, first, TempWorkspace.Book(title: "Foo", filename: "foo2", asin: "Y", pdf: "Yes"));

        Assert.Equal(["Foo.m4b", "Foo.pdf"], LibraryManifest.Load(workspace.Destination).Get("x")!.Files);
        Assert.Equal("x-pdf", ReadDestination(workspace, "An Author", "Foo", "Foo.pdf"));
        Assert.Equal("y-pdf", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.pdf"));
    }

    [Fact]
    public async Task A_new_book_never_takes_a_file_out_of_the_folder_of_a_book_on_record()
    {
        using var workspace = new TempWorkspace();
        var first = TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X");
        workspace.WriteSourceFile("foo.m4b", "x-audio");
        await Sort(workspace, first);
        // Put there by hand, so not on record: still not a file another book may take.
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.pdf"), "x-pdf");
        workspace.WriteSourceFile("foo2.m4b", "y-audio");
        workspace.WriteSourceFile("foo2.pdf", "y-pdf");

        await Sort(workspace, first, TempWorkspace.Book(title: "Foo", filename: "foo2", asin: "Y", pdf: "Yes"));

        Assert.Equal("x-pdf", ReadDestination(workspace, "An Author", "Foo", "Foo.pdf"));
        Assert.Equal("y-pdf", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.pdf"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_series_never_settles_in_the_folder_of_a_book_on_record_that_is_not_sorted(bool stillListed)
    {
        // Gone from the export, or listed but its file emptied while OpenAudible downloads it again:
        // either way the book is not moved in this run, so the series must not be filed around it.
        using var workspace = new TempWorkspace();
        var standalone = TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X");
        var series = TempWorkspace.Book(title: "Bar", filename: "bar", asin: "S1", seriesName: "Foo", seriesSequence: "1");
        workspace.WriteSourceFile("foo.m4b", "standalone");
        workspace.WriteSourceFile("bar.m4b", "series");
        await Sort(workspace, standalone);
        File.WriteAllText(Path.Combine(workspace.Source, "foo.m4b"), "");

        OpenAudible[] books = stillListed ? [standalone, series] : [series];
        var summary = await Sort(workspace, books);
        var again = await Sort(workspace, books);

        Assert.Equal(["An Author/Foo (2)/Book 1/Bar.m4b", "An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = stillListed ? 1 : 0 }, summary.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = 1, NotFound = stillListed ? 1 : 0 }, again.Counts);

        // Once the book can be sorted, it moves out of the series' way, and the series takes its name.
        workspace.WriteSourceFile("foo.m4b", "standalone");
        await Sort(workspace, standalone, series);
        Assert.Equal(["An Author/Foo (3)/Foo.m4b", "An Author/Foo/Book 1/Bar.m4b"], workspace.DestinationFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rows_of_one_book_are_one_book_when_only_one_has_its_file(bool missingRowFirst)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "audio");
        var present = TempWorkspace.Book(title: "Alpha", filename: "first", asin: "B1");
        var missing = TempWorkspace.Book(title: "Alpha", filename: "elsewhere", asin: "b1");

        var summary = await Sort(workspace, missingRowFirst ? [missing, present] : [present, missing]);

        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
        Assert.Empty(summary.Problems);
        Assert.Equal(["An Author/Alpha/Alpha.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task A_book_on_record_that_moves_gets_its_new_folder_before_a_new_book_listed_first()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("r.m4b", "r-audio");
        workspace.WriteSourceFile("n.m4b", "n-audio");
        await Sort(workspace, TempWorkspace.Book(title: "Old", filename: "r", asin: "R"));

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Foo", filename: "n", asin: "N"),
            TempWorkspace.Book(title: "Foo", filename: "r", asin: "R"));

        Assert.Equal("r-audio", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal("n-audio", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, Moved = 1 }, summary.Counts);
    }

    [Fact]
    public async Task A_loose_file_an_earlier_sort_left_is_never_taken_by_a_later_book_of_the_same_name()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-audio");
        workspace.WriteSourceFile("b.m4b", "b-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune.m4b"), "a-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune (2).m4b"), "b-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune (3).m4b"), "returned-only-copy");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Dune", filename: "a", asin: "A"),
            TempWorkspace.Book(title: "Dune", filename: "b", asin: "B")
        ];
        var upgrade = await Sort(workspace, books);
        Assert.Contains("Dune (3).m4b", Assert.Single(upgrade.Problems).Message);

        workspace.WriteSourceFile("c.m4b", "c-audio");
        var summary = await Sort(workspace, [.. books, TempWorkspace.Book(title: "Dune", filename: "c", asin: "C")]);

        Assert.Equal("returned-only-copy", ReadDestination(workspace, "An Author", "Dune (3).m4b"));
        Assert.Equal("c-audio", ReadDestination(workspace, "An Author", "Dune (3)", "Dune.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 2 }, summary.Counts);
    }

    [Fact]
    public async Task A_loose_file_an_earlier_sort_left_stays_left_once_its_same_titled_neighbour_has_moved_away()
    {
        // Main's layout: "Foo.m4b" is R's, "Foo (2).m4b" the only copy of a book that has left the export.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("r.m4b", "r-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo.m4b"), "r-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo (2).m4b"), "d-only-copy");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "r", asin: "R"));
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "r", asin: "R", seriesName: "Saga", seriesSequence: "1"));

        workspace.WriteSourceFile("n.m4b", "n-audio");
        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Foo", filename: "r", asin: "R", seriesName: "Saga", seriesSequence: "1"),
            TempWorkspace.Book(title: "Foo", filename: "n", asin: "N"));

        Assert.Equal("d-only-copy", ReadDestination(workspace, "An Author", "Foo (2).m4b"));
        Assert.Equal("n-audio", ReadDestination(workspace, "An Author", "Foo", "Foo.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
        Assert.Contains(summary.Problems, problem => problem.Message.Contains("Foo (2).m4b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_book_that_moves_takes_the_files_on_record_it_no_longer_writes_along(bool formatChanged)
    {
        // Its PDF gone from the source, or its audio downloaded in another format since.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("foo.mp3", "x-audio");
        workspace.WriteSourceFile("foo.pdf", "x-pdf");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X", m4b: null, mp3: "Yes", pdf: "Yes"));
        if (formatChanged)
        {
            File.Move(Path.Combine(workspace.Source, "foo.mp3"), Path.Combine(workspace.Source, "foo.m4b"));
        }
        else
        {
            File.Delete(Path.Combine(workspace.Source, "foo.pdf"));
        }

        var moved = TempWorkspace.Book(
            title: "Foo", filename: "foo", asin: "X", m4b: formatChanged ? "Yes" : null, mp3: formatChanged ? null : "Yes",
            pdf: formatChanged ? "Yes" : null, seriesName: "Saga", seriesSequence: "1");
        var summary = await Sort(workspace, moved);

        string[] files = formatChanged
            ? ["An Author/Saga/Book 1/Foo.m4b", "An Author/Saga/Book 1/Foo.mp3", "An Author/Saga/Book 1/Foo.pdf"]
            : ["An Author/Saga/Book 1/Foo.mp3", "An Author/Saga/Book 1/Foo.pdf"];
        Assert.Equal(files, workspace.DestinationFiles());
        Assert.Equal(files.Select(file => file.Split('/')[^1]), LibraryManifest.Load(workspace.Destination).Get("x")!.Files.Order(StringComparer.Ordinal));
        Assert.Empty(summary.Problems);

        // So a later book of the title finds nothing of the first in the folder it is given.
        workspace.WriteSourceFile("foo2.mp3", "y-audio");
        workspace.WriteSourceFile("foo2.pdf", "y-pdf");
        await Sort(workspace, moved, TempWorkspace.Book(title: "Foo", filename: "foo2", asin: "Y", m4b: null, mp3: "Yes", pdf: "Yes"));
        Assert.Equal("x-pdf", ReadDestination(workspace, "An Author", "Saga", "Book 1", "Foo.pdf"));
        Assert.Equal("y-pdf", ReadDestination(workspace, "An Author", "Foo", "Foo.pdf"));
    }

    [Fact]
    public async Task A_file_a_moving_book_cannot_take_along_stays_on_record_and_is_named()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("foo.m4b", "x-audio");
        workspace.WriteSourceFile("foo.pdf", "x-pdf");
        await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X", pdf: "Yes"));
        File.Delete(Path.Combine(workspace.Source, "foo.pdf"));
        // Its new folder already has a PDF of the name, which is never replaced.
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 1", "Foo.pdf"), "by-hand");

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X", seriesName: "Saga", seriesSequence: "1"));

        Assert.Equal(["An Author/Foo/Foo.pdf", "An Author/Saga/Book 1/Foo.m4b", "An Author/Saga/Book 1/Foo.pdf"], workspace.DestinationFiles());
        Assert.Contains(Path.Combine("An Author", "Foo", "Foo.pdf"), Assert.Single(summary.Problems).Message);
        var left = Assert.Single(LibraryManifest.Load(workspace.Destination).Books, pair => pair.Key != "x");
        Assert.Equal((Path.Combine(workspace.Destination, "An Author", "Foo"), "Foo.pdf"), (left.Value.Folder, Assert.Single(left.Value.Files)));

        // Its old folder is still given to no other book.
        workspace.WriteSourceFile("foo2.m4b", "y-audio");
        workspace.WriteSourceFile("foo2.pdf", "y-pdf");
        await Sort(
            workspace,
            TempWorkspace.Book(title: "Foo", filename: "foo", asin: "X", seriesName: "Saga", seriesSequence: "1"),
            TempWorkspace.Book(title: "Foo", filename: "foo2", asin: "Y", pdf: "Yes"));
        Assert.Equal("x-pdf", ReadDestination(workspace, "An Author", "Foo", "Foo.pdf"));
        Assert.Equal("y-pdf", ReadDestination(workspace, "An Author", "Foo (2)", "Foo.pdf"));
    }

    [Fact]
    public async Task A_book_in_a_folder_spelled_with_a_colon_is_recorded_where_the_system_allows_it()
    {
        // Legal on Linux and macOS, and reused rather than duplicated beside a folder of the plain spelling.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Foo: Bar"));
        workspace.WriteSourceFile("a.m4b", "a-audio");
        await Sort(workspace, TempWorkspace.Book(title: "Foo Bar", filename: "a", asin: "A"));
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Foo: Bar"), LibraryManifest.Load(workspace.Destination).Get("a")!.Folder);

        // Once the book has left the export, a different one of the title never takes its folder.
        workspace.WriteSourceFile("b.m4b", "b-audio");
        await Sort(workspace, TempWorkspace.Book(title: "Foo Bar", filename: "b", asin: "B"));

        Assert.Equal("a-audio", ReadDestination(workspace, "An Author", "Foo: Bar", "Foo Bar.m4b"));
        Assert.Equal("b-audio", ReadDestination(workspace, "An Author", "Foo: Bar (2)", "Foo Bar.m4b"));
    }

    [Fact]
    public async Task Upgrade_names_a_loose_file_no_book_in_the_export_could_have_left()
    {
        // A returned book's, one whose title changed since, and one in a format the book is no longer downloaded in.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("kept.m4b", "kept");
        workspace.WriteSourceFile("renamed.m4b", "renamed");
        workspace.WriteSourceFile("first.m4b", "first");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Returned Plus Title.m4b"), "returned");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Old Title.m4b"), "renamed");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Kept.mp3"), "kept-mp3");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Kept.m4b"), "kept");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 1", "First.m4b"), "first");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Kept", filename: "kept"),
            TempWorkspace.Book(title: "New Title", filename: "renamed"),
            TempWorkspace.Book(title: "First", filename: "first", seriesName: "Saga", seriesSequence: "1")
        ];

        var summary = await Sort(workspace, books);
        var again = await Sort(workspace, books);

        string[] loose = ["Kept.mp3", "Old Title.m4b", "Returned Plus Title.m4b"];
        string[] expected = loose
            .Select(name => $"Left \"{Path.Combine("An Author", name)}\" where it was: it matches none of the books in the export. If it is an old copy, delete it.")
            .ToArray();
        Assert.Equal(expected, summary.Problems.Select(problem => problem.Message));
        Assert.All(summary.Problems, problem => Assert.Equal(new { Kind = SortProblemKind.Warning, Book = "Destination folder" }, new { problem.Kind, problem.Book }));
        Assert.Equal(expected, again.Problems.Select(problem => problem.Message));
        Assert.Equal("kept", ReadDestination(workspace, "An Author", "Kept", "Kept.m4b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_book_listed_twice_keeps_its_folder_whichever_of_its_ids_comes_first(bool asinFilledIn)
    {
        // Two listings of one file with different ids; or one that had no ASIN, and so was known by its file.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("foo.m4b", "audio");
        var first = TempWorkspace.Book(title: "Foo", filename: "foo", asin: asinFilledIn ? null : "X");
        var second = TempWorkspace.Book(title: "Foo", filename: "foo", asin: "Y");
        await Sort(workspace, asinFilledIn ? [first] : [first, second]);

        var summary = await Sort(workspace, asinFilledIn ? [second] : [second, first]);
        var again = await Sort(workspace, asinFilledIn ? [second] : [second, first]);

        Assert.Equal(["An Author/Foo/Foo.m4b"], workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { UpToDate = asinFilledIn ? 1 : 2 }, summary.Counts);
        Assert.Equal(SortCounts.Empty with { UpToDate = asinFilledIn ? 1 : 2 }, again.Counts);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Foo"), Assert.Single(LibraryManifest.Load(workspace.Destination).Books).Value.Folder);
    }

    [Fact]
    public async Task A_sort_that_changes_nothing_leaves_the_manifest_as_it_is()
    {
        // Backup and sync tools, folder watchers and a sleeping disk see no change from a sort with nothing to do.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        await Sort(workspace, TempWorkspace.Book());
        var path = Path.Combine(workspace.Destination, LibraryManifest.FileName);
        var written = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task A_book_whose_update_fails_after_it_moved_is_recorded_in_its_new_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-only-copy");
        workspace.WriteSourceFile("a.pdf", "a-pdf");
        await Sort(workspace, TempWorkspace.Book(title: "Title", filename: "a", pdf: "a.pdf", asin: "A1"));

        // It joins a series and its PDF was re-released; a folder where the PDF's partial copy goes fails that copy.
        workspace.WriteSourceFile("a.pdf", "a-pdf-re-released");
        var obstacle = Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "S", "Title", "Title.pdf.oabo-partial"));
        var failed = await Sort(workspace, TempWorkspace.Book(title: "Title", filename: "a", pdf: "a.pdf", asin: "A1", seriesName: "S"));
        obstacle.Delete();
        Assert.Equal(SortCounts.Empty with { Failed = 1 }, failed.Counts);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "S", "Title"), LibraryManifest.Load(workspace.Destination).Get("a1")?.Folder);

        // It leaves the export, and a different book of its title in the series arrives.
        File.Delete(Path.Combine(workspace.Source, "a.m4b"));
        workspace.WriteSourceFile("b.m4b", "b-audio");
        await Sort(workspace, TempWorkspace.Book(title: "Title", filename: "b", asin: "B1", seriesName: "S"));

        Assert.Equal("a-only-copy", ReadDestination(workspace, "An Author", "S", "Title", "Title.m4b"));
        Assert.Equal("b-audio", ReadDestination(workspace, "An Author", "S", "Title (2)", "Title.m4b"));
    }

    [Fact]
    public async Task A_new_book_whose_pdf_cannot_be_copied_is_recorded_with_the_audio_it_has()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        workspace.WriteSourceFile("a-book.pdf", "pdf");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.pdf.oabo-partial"));

        var summary = await Sort(workspace, TempWorkspace.Book(pdf: "a-book.pdf", asin: "A1"));

        Assert.Equal(SortCounts.Empty with { Failed = 1 }, summary.Counts);
        var entry = LibraryManifest.Load(workspace.Destination).Get("a1");
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book"), entry?.Folder);
        Assert.Equal(["A Book.m4b"], entry?.Files);
    }

    [Fact]
    public async Task A_book_whose_sort_is_cancelled_after_it_moved_is_recorded_where_its_files_are()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-audio");
        workspace.WriteSourceFile("s.m4b", "small");
        var small = TempWorkspace.Book(title: "Small", filename: "s", asin: "S1");
        await Sort(workspace, TempWorkspace.Book(title: "Title", filename: "a", asin: "A1"), small);

        // It joins a series and its download changed, big enough that the other book finishes, and
        // cancels the run, while it is still being copied.
        workspace.WriteSourceFile("a.m4b", new string('z', 40_000_000));
        workspace.WriteSourceFile("s.m4b", "small-changed");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination,
            [TempWorkspace.Book(title: "Title", filename: "a", asin: "A1", seriesName: "S"), small],
            new SortOptions { MaxParallelism = 2 }, new CancelOnFirstReport(cancellation), cancellation.Token));

        // Wherever the cancel caught it, the record says where its audio is.
        var entry = LibraryManifest.Load(workspace.Destination).Get("a1");
        Assert.NotNull(entry);
        Assert.True(File.Exists(Path.Combine(entry.Folder, "Title.m4b")));
    }

    [Fact]
    public async Task A_cancelled_first_sort_still_lets_the_next_move_a_changed_loose_book_only_it_could_have_left()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "Title.m4b"), "old-release");
        workspace.WriteSourceFile("t.m4b", "new-release!");
        var book = TempWorkspace.Book(title: "Title", filename: "t", asin: "T1");

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, [book],
            progress: new InlineProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));
        Assert.True(File.Exists(Path.Combine(workspace.Destination, LibraryManifest.FileName)));

        var summary = await Sort(workspace, book);

        Assert.Equal(["An Author/Title/Title.m4b"], workspace.DestinationFiles());
        Assert.Equal("new-release!", ReadDestination(workspace, "An Author", "Title", "Title.m4b"));
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task Upgrade_moves_loose_pdfs_along_with_their_books_and_names_a_departed_books()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "Title.m4b"), "t");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Title.pdf"), "t-pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Other.m4b"), "o");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Other.pdf"), "o-pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Returned.m4b"), "r");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Returned.pdf"), "r-pdf");
        workspace.WriteSourceFile("t.m4b", "t");
        workspace.WriteSourceFile("o.m4b", "o");
        OpenAudible[] books =
        [
            TempWorkspace.Book(title: "Title", filename: "t", asin: "T1"),
            TempWorkspace.Book(title: "Other", filename: "o", asin: "O1", seriesName: "Saga")
        ];

        var summary = await Sort(workspace, books);
        var again = await Sort(workspace, books);

        Assert.Equal(
            [
                "An Author/Returned.m4b", "An Author/Returned.pdf", "An Author/Saga/Other/Other.m4b", "An Author/Saga/Other/Other.pdf",
                "An Author/Title/Title.m4b", "An Author/Title/Title.pdf"
            ],
            workspace.DestinationFiles());
        Assert.Equal(["Title.m4b", "Title.pdf"], LibraryManifest.Load(workspace.Destination).Get("t1")?.Files);
        string[] expected = ["Returned.m4b", "Returned.pdf"];
        Assert.Equal(
            expected.Select(name => $"Left \"{Path.Combine("An Author", name)}\" where it was: it matches none of the books in the export. If it is an old copy, delete it."),
            summary.Problems.Select(problem => problem.Message));
        Assert.Equal(summary.Problems.Select(problem => problem.Message), again.Problems.Select(problem => problem.Message));
    }

    [Fact]
    public async Task Warnings_about_loose_files_never_crowd_out_why_a_book_failed()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < SortSummary.MaxReportedProblems + 20; i++)
        {
            workspace.WriteDestinationFile(Path.Combine("An Author", $"Loose {i:D3}.m4b"), "loose");
        }

        workspace.WriteSourceFile("good.m4b");
        workspace.WriteSourceFile("blocked.m4b");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Blocked Book", "Blocked Book.m4b"));

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Good Book", filename: "good"),
            TempWorkspace.Book(title: "Blocked Book", filename: "blocked"));

        Assert.Equal(SortSummary.MaxReportedProblems + 21, summary.ProblemCount);
        Assert.Contains(summary.Problems, problem => problem is { Kind: SortProblemKind.Failed, Book: "Blocked Book — An Author" });
    }

    [Fact]
    public async Task A_loose_file_of_a_book_missing_from_the_source_is_never_called_nobodys()
    {
        // While OpenAudible downloads it again, the loose copy main left may be the book's only one.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("kept.m4b", "kept");
        workspace.WriteSourceFile("gone.m4b", string.Empty);
        workspace.WriteDestinationFile(Path.Combine("An Author", "Gone.m4b"), "gone-only-copy");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Kept", filename: "kept"),
            TempWorkspace.Book(title: "Gone", filename: "gone"));

        Assert.Equal(SortCounts.Empty with { New = 1, NotFound = 1 }, summary.Counts);
        Assert.Equal("gone-only-copy", ReadDestination(workspace, "An Author", "Gone.m4b"));
        var warning = Assert.Single(summary.Problems, problem => problem.Kind == SortProblemKind.Warning);
        Assert.Equal(
            $"Left \"{Path.Combine("An Author", "Gone.m4b")}\" where it was: it is named like \"Gone — An Author\", which is not in the " +
            "source folder, so it may be that book's only copy.",
            warning.Message);
    }

    [Fact]
    public async Task A_book_on_record_that_moves_never_takes_an_unrecorded_folder_of_its_name_holding_another_books_audio()
    {
        // Main left Z's only copy in "S/Book 1"; Z has since left the export. X, on record, joins S as #1.
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha.m4b"), "z-only-copy");
        workspace.WriteSourceFile("x.m4b", "x-audio");
        await Sort(workspace, TempWorkspace.Book(title: "Alpha", filename: "x", asin: "X1"));

        var summary = await Sort(workspace, TempWorkspace.Book(title: "Alpha", filename: "x", asin: "X1", seriesName: "S", seriesSequence: "1"));

        Assert.Equal(["An Author/S/Book 1 (2)/Alpha.m4b", "An Author/S/Book 1/Alpha.m4b"], workspace.DestinationFiles());
        Assert.Equal("z-only-copy", ReadDestination(workspace, "An Author", "S", "Book 1", "Alpha.m4b"));
        Assert.Equal("x-audio", ReadDestination(workspace, "An Author", "S", "Book 1 (2)", "Alpha.m4b"));
        Assert.Equal(SortCounts.Empty with { Moved = 1 }, summary.Counts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_a_finished_sort_a_new_book_never_takes_an_old_book_folder_of_its_name_by_name_alone(bool departedWasListed)
    {
        // Main left a departed edition's only copy in "Saga/Book 1". A finished sort ran without touching it
        // (the edition listed but missing from the source, or not listed at all); then a new edition arrives.
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 1", "Title.m4b"), "old-edition-only-copy");
        workspace.WriteSourceFile("o.m4b", "other");
        var other = TempWorkspace.Book(title: "Other", filename: "o", asin: "O1", seriesName: "Saga", seriesSequence: "2");
        OpenAudible[] firstExport = departedWasListed
            ? [other, TempWorkspace.Book(title: "Title", filename: "z", asin: "Z1", seriesName: "Saga", seriesSequence: "1")]
            : [other];
        await Sort(workspace, firstExport);
        Assert.True(LibraryManifest.Load(workspace.Destination).SortFinished);

        workspace.WriteSourceFile("y.m4b", "new-edition");
        var summary = await Sort(workspace, other, TempWorkspace.Book(title: "Title", filename: "y", asin: "Y1", seriesName: "Saga", seriesSequence: "1"));

        Assert.Equal("old-edition-only-copy", ReadDestination(workspace, "An Author", "Saga", "Book 1", "Title.m4b"));
        Assert.Equal("new-edition", ReadDestination(workspace, "An Author", "Saga", "Book 1 (2)", "Title.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
        Assert.Contains(Path.Combine("An Author", "Saga", "Book 1", "Title.m4b"), Assert.Single(summary.Problems).Message);
    }

    [Fact]
    public async Task Upgrade_never_gives_a_book_another_books_file_from_the_book_folder_a_same_titled_book_keeps()
    {
        // Main's "Book 1" holds A's copy and the only copy of C, an edition that has left the export.
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha.m4b"), "a-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha (2).m4b"), "c-only-copy");
        workspace.WriteSourceFile("a.m4b", "a-audio");
        workspace.WriteSourceFile("b.m4b", "b-audio");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "a", asin: "A1", seriesName: "S", seriesSequence: "1"),
            TempWorkspace.Book(title: "Alpha", filename: "b", asin: "B1", seriesName: "S", seriesSequence: "1"));

        Assert.Equal("a-audio", ReadDestination(workspace, "An Author", "S", "Book 1", "Alpha.m4b"));
        Assert.Equal("c-only-copy", ReadDestination(workspace, "An Author", "S", "Book 1", "Alpha (2).m4b"));
        Assert.Equal("b-audio", ReadDestination(workspace, "An Author", "S", "Book 1 (2)", "Alpha.m4b"));
        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
        Assert.Contains(Path.Combine("An Author", "S", "Book 1", "Alpha (2).m4b"), Assert.Single(summary.Problems).Message);
    }

    [Fact]
    public async Task Upgrade_never_gives_a_book_the_pdf_of_the_same_titled_book_that_keeps_the_book_folder()
    {
        // A's PDF is no longer in the source; B's is.
        using var workspace = new TempWorkspace();
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha.m4b"), "a-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha.pdf"), "a-pdf");
        workspace.WriteDestinationFile(Path.Combine("An Author", "S", "Book 1", "Alpha (2).m4b"), "b-audio");
        workspace.WriteSourceFile("a.m4b", "a-audio");
        workspace.WriteSourceFile("b.m4b", "b-audio");
        workspace.WriteSourceFile("b.pdf", "b-pdf");

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Alpha", filename: "a", asin: "A1", seriesName: "S", seriesSequence: "1"),
            TempWorkspace.Book(title: "Alpha", filename: "b", asin: "B1", seriesName: "S", seriesSequence: "1"));

        Assert.Equal("a-pdf", ReadDestination(workspace, "An Author", "S", "Book 1", "Alpha.pdf"));
        Assert.Equal("b-audio", ReadDestination(workspace, "An Author", "S", "Book 1 (2)", "Alpha.m4b"));
        Assert.Equal("b-pdf", ReadDestination(workspace, "An Author", "S", "Book 1 (2)", "Alpha.pdf"));
        Assert.Equal(
            ["An Author/S/Book 1 (2)/Alpha.m4b", "An Author/S/Book 1 (2)/Alpha.pdf", "An Author/S/Book 1/Alpha.m4b", "An Author/S/Book 1/Alpha.pdf"],
            workspace.DestinationFiles());
        Assert.Equal(SortCounts.Empty with { New = 1, UpToDate = 1 }, summary.Counts);
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
