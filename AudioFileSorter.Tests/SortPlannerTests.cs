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
        Assert.Equal(Path.Combine(authorFolder, "The Singularity Trap", "The Singularity Trap.m4b"), planned[0].AudioDestination);
        Assert.Equal(Path.Combine(authorFolder, "Bobiverse", "Book 1", "We Are Legion.m4b"), planned[1].AudioDestination);
        Assert.All(planned, p => Assert.NotEqual(authorFolder, Path.GetDirectoryName(p.AudioDestination)));
    }

    [Fact]
    public void Plan_reuses_an_existing_author_folder_that_is_spelled_differently()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "JK Rowling"));
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(author: "J.K. Rowling"));

        Assert.Equal(Path.Combine(workspace.Destination, "JK Rowling", "A Book", "A Book.m4b"), planned[0].AudioDestination);
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
    public void Plan_reuses_an_author_folder_whose_name_the_disk_stores_decomposed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "Rene\u0301 Author"));

        var planned = Plan(workspace, TempWorkspace.Book(author: "Ren\u00e9 Author"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "Rene\u0301 Author", "A Book", "A Book.m4b"),
            planned[0].AudioDestination);
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
    public void Plan_gives_two_different_books_with_the_same_name_separate_folders()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b");
        workspace.WriteSourceFile("second.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        // A folder is one book to a library tool, so sharing one would merge them.
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works", "Collected Works.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works (2)", "Collected Works.m4b"),
            planned[1].AudioDestination);
    }

    [Fact]
    public void Plan_gives_two_books_with_the_same_series_number_folders_of_their_own()
    {
        // Two narrations of one book: sharing "Book 1" would make a library tool read them as one
        // book with two tracks.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("hp1-fry.m4b", "fry-edition");
        workspace.WriteSourceFile("hp1-dale.m4b", "dale-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "hp1-fry", seriesName: "Harry Potter", seriesSequence: "1"),
            TempWorkspace.Book(filename: "hp1-dale", seriesName: "Harry Potter", seriesSequence: "1"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Harry Potter", "Book 1", "A Book.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Harry Potter", "Book 1 (2)", "A Book.m4b"),
            planned[1].AudioDestination);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_never_files_a_standalone_book_inside_a_series_folder_of_the_same_name(bool standaloneFirst)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b");
        workspace.WriteSourceFile("series-1.m4b");
        var standalone = TempWorkspace.Book(title: "Bobiverse", filename: "standalone");
        var seriesBook = TempWorkspace.Book(title: "We Are Legion", filename: "series-1", seriesName: "Bobiverse", seriesSequence: "1");

        var planned = Plan(workspace, standaloneFirst ? [standalone, seriesBook] : [seriesBook, standalone]);

        Assert.Contains(
            planned,
            copy => copy.AudioDestination == Path.Combine(workspace.Destination, "An Author", "Bobiverse (2)", "Bobiverse.m4b"));
        Assert.Contains(
            planned,
            copy => copy.AudioDestination == Path.Combine(workspace.Destination, "An Author", "Bobiverse", "Book 1", "We Are Legion.m4b"));
    }

    [Fact]
    public void Plan_keeps_a_book_apart_from_a_series_that_differs_only_by_the()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("witcher.m4b");
        workspace.WriteSourceFile("blood-of-elves.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Blood of Elves", filename: "blood-of-elves", seriesName: "The Witcher", seriesSequence: "1"),
            TempWorkspace.Book(title: "Witcher", filename: "witcher"));

        // Series names ignore a leading "The"; book names do not.
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "The Witcher", "Book 1", "Blood of Elves.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Witcher", "Witcher.m4b"), planned[1].AudioDestination);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_later_run_keeps_a_series_and_a_book_that_differs_only_by_the_in_their_own_folders(bool seriesFolderFirst)
    {
        // The disk's listing order follows creation order on some file systems and the alphabet
        // on others, so both orders are tried.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("witcher.m4b");
        workspace.WriteSourceFile("blood-of-elves.m4b");
        var seriesFile = Path.Combine("An Author", "The Witcher", "Book 1", "Blood of Elves.m4b");
        var bookFile = Path.Combine("An Author", "Witcher", "Witcher.m4b");
        foreach (var file in seriesFolderFirst ? new[] { seriesFile, bookFile } : [bookFile, seriesFile])
        {
            workspace.WriteDestinationFile(file, "audio");
        }

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Blood of Elves", filename: "blood-of-elves", seriesName: "The Witcher", seriesSequence: "1"),
            TempWorkspace.Book(title: "Witcher", filename: "witcher"));

        Assert.Equal(Path.Combine(workspace.Destination, seriesFile), planned[0].AudioDestination);
        Assert.Equal(Path.Combine(workspace.Destination, bookFile), planned[1].AudioDestination);
    }

    [Fact]
    public void A_new_series_does_not_take_over_the_folder_of_a_book_named_like_it()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("witcher.m4b");
        workspace.WriteSourceFile("blood-of-elves.m4b");
        var bookFile = workspace.WriteDestinationFile(Path.Combine("An Author", "Witcher", "Witcher.m4b"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Blood of Elves", filename: "blood-of-elves", seriesName: "The Witcher", seriesSequence: "1"),
            TempWorkspace.Book(title: "Witcher", filename: "witcher"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "The Witcher", "Book 1", "Blood of Elves.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(bookFile, planned[1].AudioDestination);
    }

    [Fact]
    public void Plan_identifies_a_book_by_its_asin_whatever_its_case()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(asin: " B00ABC "));

        Assert.Equal("b00abc", planned[0].BookId);
    }

    [Fact]
    public void Plan_identifies_a_book_without_an_asin_by_its_audio_file()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal("file:a-book.m4b", planned[0].BookId);
    }

    [Fact]
    public void Plan_copies_a_row_listing_the_same_file_again_only_once()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(), TempWorkspace.Book(title: "Another Title"));

        Assert.NotNull(planned[0].AudioDestination);
        Assert.False(planned[1].HasWork);
        Assert.Null(planned[1].Warning);
    }

    [Fact]
    public void Plan_copies_rows_with_the_same_asin_only_once()
    {
        // The same book from two accounts or regions, downloaded twice.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b");
        workspace.WriteSourceFile("second.m4b");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "first", asin: "B00ABC"),
            TempWorkspace.Book(title: "Another Title", filename: "second", asin: "b00abc"));

        Assert.NotNull(planned[0].AudioDestination);
        Assert.False(planned[1].HasWork);
        Assert.False(planned[1].IsMissingFromSource);
    }

    [Fact]
    public void Plan_keeps_a_recorded_book_in_its_folder_even_when_it_is_a_second_one()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("b.m4b", "b-edition");
        var recorded = workspace.WriteDestinationFile(Path.Combine("An Author", "Foo (2)", "Foo.m4b"), "b-edition");
        Record(workspace, "b", Path.Combine("An Author", "Foo (2)"), "Foo.m4b");

        // "Foo" is free now, but the book stays where it is.
        var planned = Plan(workspace, TempWorkspace.Book(title: "Foo", filename: "b", asin: "B"));

        Assert.Equal(recorded, planned[0].AudioDestination);
        Assert.Null(planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Plan_never_gives_a_folder_the_manifest_records_to_another_book()
    {
        // The recorded book has left the export, and its file may be the only copy.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("b.m4b", "b-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.m4b"), "a-edition");
        Record(workspace, "a", Path.Combine("An Author", "Foo"), "Foo.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(title: "Foo", filename: "b", asin: "B"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Foo (2)", "Foo.m4b"), planned[0].AudioDestination);
        Assert.Null(planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Plan_moves_a_recorded_book_whose_title_changed_and_renames_its_files()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "audio");
        workspace.WriteSourceFile("a.pdf", "pdf");
        var oldAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "Old Name", "Old Name.m4b"), "audio");
        var oldPdf = workspace.WriteDestinationFile(Path.Combine("An Author", "Old Name", "Old Name.pdf"), "pdf");
        Record(workspace, "a", Path.Combine("An Author", "Old Name"), "Old Name.m4b", "Old Name.pdf");

        var planned = Plan(workspace, TempWorkspace.Book(title: "New Name", filename: "a", asin: "A", pdf: "a.pdf"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "New Name", "New Name.m4b"), planned[0].AudioDestination);
        Assert.Equal(oldAudio, planned[0].AudioMoveFrom);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "New Name", "New Name.pdf"), planned[0].PdfDestination);
        Assert.Equal(oldPdf, planned[0].PdfMoveFrom);
    }

    [Fact]
    public void Plan_moves_a_recorded_books_pdf_along_when_the_pdf_has_left_the_source()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "audio");
        var oldAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.m4b"), "audio");
        var oldPdf = workspace.WriteDestinationFile(Path.Combine("An Author", "Foo", "Foo.pdf"), "pdf");
        Record(workspace, "a", Path.Combine("An Author", "Foo"), "Foo.m4b", "Foo.pdf");

        var planned = Plan(workspace, TempWorkspace.Book(title: "Foo", filename: "a", asin: "A", seriesName: "Saga", seriesSequence: "1"));

        var folder = Path.Combine(workspace.Destination, "An Author", "Saga", "Book 1");
        Assert.Equal(oldAudio, planned[0].AudioMoveFrom);
        Assert.Null(planned[0].PdfDestination);
        Assert.Equal((oldPdf, Path.Combine(folder, "Foo.pdf")), Assert.Single(planned[0].OtherMoves));
    }

    [Fact]
    public void Plan_renames_a_recorded_series_book_whose_title_changed_in_the_folder_it_keeps()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "audio");
        var old = workspace.WriteDestinationFile(Path.Combine("An Author", "Saga", "Book 1", "Old Name.m4b"), "audio");
        Record(workspace, "a", Path.Combine("An Author", "Saga", "Book 1"), "Old Name.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(title: "New Name", filename: "a", asin: "A", seriesName: "Saga", seriesSequence: "1"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Saga", "Book 1", "New Name.m4b"), planned[0].AudioDestination);
        Assert.Equal(old, planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Plan_skips_a_folder_that_holds_only_another_books_audio()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "Something Else.m4b"), "other");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book (2)", "A Book.m4b"), planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_takes_a_folder_holding_the_books_own_copy_that_no_manifest_records()
    {
        // As a sort that crashed before saving its manifest, or a manifest that was deleted, leaves it.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "download");
        var existing = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "download");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(existing, planned[0].AudioDestination);
    }

    [Fact]
    public void Plan_leaves_an_unrecorded_folder_whose_recording_of_the_books_name_differs()
    {
        // An older copy of this book, or another book's only copy: the name cannot say which.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-download");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "old-download");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book (2)", "A Book.m4b"), planned[0].AudioDestination);
        Assert.Contains($"Left \"{Path.Combine("An Author", "A Book")}\" alone", planned[0].Warning);
    }

    // The upgrade tests below seed the destination with exactly what the version on main wrote:
    // standalone books loose in the author folder, series books without a number loose in the
    // series folder, numbered series books in "Series/Book N/", and a " (2)" suffix only when two
    // files of the same name and extension landed in the same folder, in list order.

    [Fact]
    public void Upgrade_moves_a_book_an_older_version_left_loose_in_the_author_folder()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteSourceFile("a-book.pdf", "pdf");
        var looseAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        var loosePdf = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.pdf"), "pdf");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Equal(looseAudio, planned[0].AudioMoveFrom);
        Assert.Equal(loosePdf, planned[0].PdfMoveFrom);
    }

    [Fact]
    public void Upgrade_does_not_move_a_loose_file_when_the_book_folder_already_has_one()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.Null(planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_matches_two_same_titled_loose_books_by_content_not_by_order()
    {
        // main named them in list order, and the export's order has changed since.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "first-book");
        workspace.WriteSourceFile("second.m4b", "second-book");
        var secondLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "second-book");
        var firstLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "first-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(firstLoose, planned[0].AudioMoveFrom);
        Assert.Equal(secondLoose, planned[1].AudioMoveFrom);
        Assert.All(planned, copy => Assert.Null(copy.Warning));
    }

    [Fact]
    public void Upgrade_moves_both_formats_of_a_title_that_never_needed_a_suffix()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b");
        workspace.WriteSourceFile("second.mp3");
        var looseM4b = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "audio");
        var looseMp3 = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.mp3"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second", m4b: null, mp3: "Yes"));

        Assert.Equal(looseM4b, planned[0].AudioMoveFrom);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works (2)", "Collected Works.mp3"),
            planned[1].AudioDestination);
        Assert.Equal(looseMp3, planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_warns_once_about_a_loose_file_that_matches_none_of_the_books_that_could_have_left_it()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "first-book");
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "someone-else");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.All(planned, copy => Assert.Null(copy.AudioMoveFrom));
        Assert.Equal(
            $"Left \"{Path.Combine("An Author", "Collected Works.m4b")}\" where it was: it matches none of the books in the export. " +
            "If it is an old copy, delete it.",
            Assert.Single(planned, copy => copy.Warning is not null).Warning);
    }

    [Fact]
    public void Upgrade_moves_a_standalone_book_named_like_its_authors_series()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b");
        workspace.WriteSourceFile("bobiverse-1.m4b");
        var loose = workspace.WriteDestinationFile(Path.Combine("Dennis E. Taylor", "Bobiverse.m4b"), "audio");
        workspace.WriteDestinationFile(Path.Combine("Dennis E. Taylor", "Bobiverse", "Book 1", "We Are Legion.m4b"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(author: "Dennis E. Taylor", title: "Bobiverse", filename: "standalone"),
            TempWorkspace.Book(
                author: "Dennis E. Taylor", title: "We Are Legion", filename: "bobiverse-1",
                seriesName: "Bobiverse", seriesSequence: "1"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "Dennis E. Taylor", "Bobiverse (2)", "Bobiverse.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(loose, planned[0].AudioMoveFrom);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_matches_same_titled_loose_books_by_content_when_a_series_has_the_plain_folder_name()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("series-1.m4b", "volume-one");
        workspace.WriteSourceFile("first.m4b", "first-book");
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works", "Book 1", "Volume One.m4b"), "volume-one");
        var firstLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        var secondLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Volume One", filename: "series-1", seriesName: "Collected Works", seriesSequence: "1"),
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        // The series takes "Collected Works", so the books' folders are "(2)" and "(3)".
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works (2)", "Collected Works.m4b"),
            planned[1].AudioDestination);
        Assert.Equal(firstLoose, planned[1].AudioMoveFrom);
        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "Collected Works (3)", "Collected Works.m4b"),
            planned[2].AudioDestination);
        Assert.Equal(secondLoose, planned[2].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_keeps_a_series_in_the_folder_main_gave_it_when_a_book_without_a_number_lies_loose_in_it()
    {
        // main reused "Expanse" for "The Expanse" and left the unnumbered book loose in it. Taking
        // that for a book's own folder copied the whole series into a second one.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("b1.m4b", "leviathan");
        workspace.WriteSourceFile("novella.m4b", "novella");
        var numbered = workspace.WriteDestinationFile(Path.Combine("An Author", "Expanse", "Book 1", "Leviathan Wakes.m4b"), "leviathan");
        var loose = workspace.WriteDestinationFile(Path.Combine("An Author", "Expanse", "The Churn.m4b"), "novella");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Leviathan Wakes", filename: "b1", seriesName: "The Expanse", seriesSequence: "1"),
            TempWorkspace.Book(title: "The Churn", filename: "novella", seriesName: "The Expanse"));

        Assert.Equal(numbered, planned[0].AudioDestination);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Expanse", "The Churn", "The Churn.m4b"), planned[1].AudioDestination);
        Assert.Equal(loose, planned[1].AudioMoveFrom);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Upgrade_files_every_spelling_of_a_series_in_the_folder_main_used_whatever_the_row_order(bool otherSpellingFirst)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("eye.m4b", "eye");
        workspace.WriteSourceFile("hunt.m4b", "hunt");
        workspace.WriteSourceFile("spring.m4b", "spring");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Wheel of Time", "Book 1", "The Eye of the World.m4b"), "eye");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Wheel of Time", "Book 2", "The Great Hunt.m4b"), "hunt");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Wheel of Time", "New Spring.m4b"), "spring");
        var other = TempWorkspace.Book(title: "The Great Hunt", filename: "hunt", seriesName: "The Wheel of Time", seriesSequence: "2");
        OpenAudible[] rest =
        [
            TempWorkspace.Book(title: "The Eye of the World", filename: "eye", seriesName: "Wheel of Time", seriesSequence: "1"),
            TempWorkspace.Book(title: "New Spring", filename: "spring", seriesName: "Wheel of Time")
        ];

        var planned = Plan(workspace, otherSpellingFirst ? [other, .. rest] : [.. rest, other]);

        var seriesFolder = Path.Combine(workspace.Destination, "An Author", "Wheel of Time");
        Assert.All(planned, copy => Assert.StartsWith(seriesFolder + Path.DirectorySeparatorChar, copy.AudioDestination));
    }

    [Fact]
    public void Upgrade_moves_a_loose_series_book_that_has_since_gained_a_number()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var old = workspace.WriteDestinationFile(Path.Combine("An Author", "The Series", "A Book.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "The Series", seriesSequence: "2"));

        Assert.Equal(
            Path.Combine(workspace.Destination, "An Author", "The Series", "Book 2", "A Book.m4b"),
            planned[0].AudioDestination);
        Assert.Equal(old, planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_moves_a_book_out_of_its_folder_when_a_series_of_the_same_name_takes_it_over()
    {
        // Filed as a standalone book, then given series metadata whose series is named after the
        // book. The series reuses the folder; the file must not stay loose in it.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var old = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book(seriesName: "A Book", seriesSequence: "1"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "Book 1", "A Book.m4b"), planned[0].AudioDestination);
        Assert.Equal(old, planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_takes_a_file_another_book_is_about_to_write()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("standalone.m4b");
        workspace.WriteSourceFile("series.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book", "A Book.m4b"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "series", seriesName: "Other Series", seriesSequence: "1"),
            TempWorkspace.Book(filename: "standalone"));

        Assert.Null(planned[0].AudioMoveFrom);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b"), planned[1].AudioDestination);
    }

    [Fact]
    public void Upgrade_gives_a_loose_file_to_the_book_that_left_it_there_over_one_that_changed_shape()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("series.m4b");
        workspace.WriteSourceFile("standalone.m4b");
        var loose = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "series", seriesName: "Other Series", seriesSequence: "1"),
            TempWorkspace.Book(filename: "standalone"));

        Assert.Null(planned[0].AudioMoveFrom);
        Assert.Equal(loose, planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_gives_a_missing_books_loose_file_to_another_book_of_the_same_title()
    {
        // The first "Collected Works" is still in the export, but its file is gone from the source.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        var firstLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        var secondLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.True(planned[0].IsMissingFromSource);
        Assert.Null(planned[0].AudioMoveFrom);
        Assert.Equal(secondLoose, planned[1].AudioMoveFrom);
        Assert.Contains(Path.GetRelativePath(workspace.Destination, firstLoose), planned[1].Warning);
    }

    [Fact]
    public void Upgrade_moves_the_loose_file_of_the_only_downloaded_book_of_a_title()
    {
        // main gave a book with no file no name, so the downloaded second book got the plain one.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        var loose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "second-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.True(planned[0].IsMissingFromSource);
        Assert.Equal(loose, planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_leaves_a_loose_file_in_place_when_it_could_be_a_missing_books_and_is_not_this_ones()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        var loose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Null(planned[1].AudioMoveFrom);
        Assert.Contains(Path.GetRelativePath(workspace.Destination, loose), planned[1].Warning);
    }

    [Fact]
    public void Upgrade_moves_a_books_own_copy_when_the_plain_name_holds_a_book_that_has_left_the_export()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        var firstLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        var secondLoose = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");

        var planned = Plan(workspace, TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(secondLoose, planned[0].AudioMoveFrom);
        Assert.Contains(Path.GetRelativePath(workspace.Destination, firstLoose), planned[0].Warning);
    }

    [Fact]
    public void Upgrade_never_gives_a_missing_books_folder_to_another_book_of_the_same_title()
    {
        // As a sort without a manifest (a deleted one, or a crash) left them.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("second.m4b", "second-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works", "Collected Works.m4b"), "first-book");
        var secondFile = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2)", "Collected Works.m4b"), "second-book");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Collected Works", filename: "first"),
            TempWorkspace.Book(title: "Collected Works", filename: "second"));

        Assert.Equal(secondFile, planned[1].AudioDestination);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_moves_a_missing_standalone_books_file_into_a_series_book_of_the_same_title()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("dune-series.m4b");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune", "Dune.m4b"), "standalone-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Dune", filename: "dune-standalone"),
            TempWorkspace.Book(title: "Dune", filename: "dune-series", seriesName: "Dune Chronicles", seriesSequence: "1"));

        Assert.True(planned[0].IsMissingFromSource);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_moves_a_missing_standalone_books_loose_file_into_a_series_book_of_the_same_title()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("dune-series.m4b", "series-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune.m4b"), "standalone-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Dune", filename: "dune-standalone"),
            TempWorkspace.Book(title: "Dune", filename: "dune-series", seriesName: "Dune Chronicles", seriesSequence: "1"));

        Assert.True(planned[0].IsMissingFromSource);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_moves_the_second_book_with_a_series_number_out_of_the_book_folder_main_shared()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("hp1-fry.m4b", "fry-edition");
        workspace.WriteSourceFile("hp1-dale.m4b", "dale-edition");
        var first = workspace.WriteDestinationFile(Path.Combine("An Author", "Harry Potter", "Book 1", "A Book.m4b"), "fry-edition");
        var second = workspace.WriteDestinationFile(Path.Combine("An Author", "Harry Potter", "Book 1", "A Book (2).m4b"), "dale-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "hp1-fry", seriesName: "Harry Potter", seriesSequence: "1"),
            TempWorkspace.Book(filename: "hp1-dale", seriesName: "Harry Potter", seriesSequence: "1"));

        Assert.Equal(first, planned[0].AudioDestination);
        Assert.Null(planned[0].AudioMoveFrom);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Harry Potter", "Book 1 (2)", "A Book.m4b"), planned[1].AudioDestination);
        Assert.Equal(second, planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_leaves_a_downloaded_edition_in_the_book_folder_it_has_when_an_undownloaded_one_is_listed_first()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("hp1-dale.m4b", "dale-edition");
        var old = workspace.WriteDestinationFile(Path.Combine("An Author", "Harry Potter", "Book 1", "A Book.m4b"), "dale-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "hp1-fry", seriesName: "Harry Potter", seriesSequence: "1"),
            TempWorkspace.Book(filename: "hp1-dale", seriesName: "Harry Potter", seriesSequence: "1"));

        Assert.True(planned[0].IsMissingFromSource);
        Assert.Equal(old, planned[1].AudioDestination);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_moves_a_missing_editions_file_into_another_edition_with_the_same_number()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("hp1-dale.m4b", "dale-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Harry Potter", "Book 1", "A Book.m4b"), "fry-edition");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(filename: "hp1-fry", seriesName: "Harry Potter", seriesSequence: "1"),
            TempWorkspace.Book(filename: "hp1-dale", seriesName: "Harry Potter", seriesSequence: "1"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Harry Potter", "Book 1 (2)", "A Book.m4b"), planned[1].AudioDestination);
        Assert.Null(planned[1].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_moves_a_loose_file_whose_name_the_disk_stores_decomposed()
    {
        // HFS+, and files a Mac wrote to a NAS, store "é" as "e" + a combining accent; the export
        // spells it as one character.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var loose = workspace.WriteDestinationFile(Path.Combine("An Author", "Cafe\u0301.m4b"), "audio");

        var planned = Plan(workspace, TempWorkspace.Book(title: "Caf\u00e9"));

        Assert.Equal(loose, planned[0].AudioMoveFrom);
    }

    [Fact]
    public void Upgrade_never_gives_a_book_titled_with_a_number_the_loose_file_of_a_second_same_titled_book()
    {
        // Main's layout: "Dune (2).m4b" is the second "Dune", whose edition has left the export; "Dune 2" is a sequel.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("dune.m4b", "dune-first");
        workspace.WriteSourceFile("sequel.m4b", "sequel");
        var first = workspace.WriteDestinationFile(Path.Combine("An Author", "Dune.m4b"), "dune-first");
        var second = workspace.WriteDestinationFile(Path.Combine("An Author", "Dune (2).m4b"), "dune-second-only-copy");

        var planned = Plan(
            workspace,
            TempWorkspace.Book(title: "Dune", filename: "dune", asin: "D1"),
            TempWorkspace.Book(title: "Dune 2", filename: "sequel", asin: "D3"));

        Assert.Equal(first, planned[0].AudioMoveFrom);
        Assert.Null(planned[1].AudioMoveFrom);
        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Dune 2", "Dune 2.m4b"), planned[1].AudioDestination);
        Assert.Contains(planned, copy => copy.Warning?.Contains(Path.GetRelativePath(workspace.Destination, second)) == true);
    }

    [Fact]
    public void Plan_never_files_a_book_titled_with_a_number_in_the_folder_of_a_second_same_titled_book()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("sequel.m4b", "sequel");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune", "Dune.m4b"), "dune-first");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Dune (2)", "Dune.m4b"), "dune-second");
        Record(workspace, "d1", Path.Combine("An Author", "Dune"), "Dune.m4b");
        Record(workspace, "d2", Path.Combine("An Author", "Dune (2)"), "Dune.m4b");

        var planned = Plan(workspace, TempWorkspace.Book(title: "Dune 2", filename: "sequel", asin: "D3"));

        Assert.Equal(Path.Combine(workspace.Destination, "An Author", "Dune 2", "Dune 2.m4b"), planned[0].AudioDestination);
    }

    [Fact]
    public void Upgrade_leaves_a_loose_pdf_beside_its_books_audio_when_the_source_has_none_to_compare_it_with()
    {
        // Main numbered each type of file on its own: beside this book's audio, "A Book.pdf" may be the only
        // copy of a same-titled book that had only its PDF then. Its name alone does not make it this book's.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        var looseAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        var loosePdf = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.pdf"), "pdf");
        var planner = new SortPlanner();

        var planned = planner.Plan([TempWorkspace.Book()], workspace.Source, workspace.Destination, LibraryManifest.Load(workspace.Destination));

        Assert.Equal(looseAudio, planned[0].AudioMoveFrom);
        Assert.Null(planned[0].PdfDestination);
        Assert.Empty(planned[0].OtherMoves);
        Assert.Equal(
            $"Left \"{Path.GetRelativePath(workspace.Destination, loosePdf)}\" where it was: it is named like \"A Book — An Author\", " +
            "but the source folder has no PDF of that book to compare it with, so it may as well be another book's. If it is this " +
            $"book's, move it into \"{Path.Combine("An Author", "A Book")}\" yourself.",
            Assert.Single(planner.Warnings));
    }

    [Fact]
    public void Upgrade_says_a_loose_pdf_named_like_a_book_missing_from_the_source_may_be_its_only_copy()
    {
        // The reverse of the above, as main left it for a book that had only its PDF: the same-titled book
        // listed but missing from the source may be the one whose only copy it is, which counts for more.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a.m4b", "a-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Foo.m4b"), "a-audio");
        var loosePdf = workspace.WriteDestinationFile(Path.Combine("An Author", "Foo.pdf"), "b-pdf");
        var planner = new SortPlanner();

        var planned = planner.Plan(
            [TempWorkspace.Book(title: "Foo", filename: "a"), TempWorkspace.Book(title: "Foo", filename: "b")],
            workspace.Source,
            workspace.Destination,
            LibraryManifest.Load(workspace.Destination));

        Assert.Empty(planned[0].OtherMoves);
        Assert.Equal(
            $"Left \"{Path.GetRelativePath(workspace.Destination, loosePdf)}\" where it was: it is named like \"Foo — An Author\", " +
            "which is not in the source folder, so it may be that book's only copy.",
            Assert.Single(planner.Warnings));
    }

    [Fact]
    public void Upgrade_leaves_a_loose_pdf_that_may_be_a_same_titled_books_and_names_it()
    {
        // Main numbered each type of file on its own: this "Collected Works.pdf" is the second book's.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("first.m4b", "first-book");
        var looseAudio = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.m4b"), "first-book");
        workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works (2).m4b"), "second-book");
        var loosePdf = workspace.WriteDestinationFile(Path.Combine("An Author", "Collected Works.pdf"), "second-pdf");
        var planner = new SortPlanner();

        var planned = planner.Plan(
            [TempWorkspace.Book(title: "Collected Works", filename: "first")],
            workspace.Source,
            workspace.Destination,
            LibraryManifest.Load(workspace.Destination));

        Assert.Equal(looseAudio, planned[0].AudioMoveFrom);
        Assert.Empty(planned[0].OtherMoves);
        Assert.Contains(planner.Warnings, warning => warning.Contains(Path.GetRelativePath(workspace.Destination, loosePdf)));
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
        Assert.True(planned[0].IsMissingFromSource);
        Assert.Null(planned[0].TargetDirectory);
        Assert.Null(planned[0].Warning);
    }

    [Fact]
    public void Plan_reports_an_empty_source_file_as_missing_and_says_why()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "");

        var planned = Plan(workspace, TempWorkspace.Book());

        Assert.True(planned[0].IsMissingFromSource);
        Assert.StartsWith("The book's file in the source folder is empty", planned[0].Warning);
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

        var planned = Plan(workspace, TempWorkspace.Book(m4b: null, filename: "not-this-one", filePaths: stalePath));

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

        var planned = Plan(workspace, TempWorkspace.Book(m4b: null, filename: "not-this-one", filePaths: recordedPath));

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

        var first = Plan(workspace, [.. books]);
        var second = Plan(workspace, [.. books]);

        Assert.Equal(first.Select(p => p.AudioDestination), second.Select(p => p.AudioDestination));
        Assert.Equal(20, first.Select(p => p.TargetDirectory).Distinct().Count());
    }

    [Fact]
    public void Plan_stops_when_the_run_is_canceled()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => new SortPlanner().Plan(
            [TempWorkspace.Book()], workspace.Source, workspace.Destination, LibraryManifest.Load(workspace.Destination), canceled.Token));
    }

    /// <summary>Records in the destination's manifest that <paramref name="bookId"/> is filed in <paramref name="folder"/>.</summary>
    private static void Record(TempWorkspace workspace, string bookId, string folder, params string[] files)
    {
        var manifest = LibraryManifest.Load(workspace.Destination);
        Assert.True(manifest.Set(bookId, new ManifestEntry(Path.Combine(workspace.Destination, folder), files, bookId)));
        manifest.Save();
    }

    private static List<PlannedCopy> Plan(TempWorkspace workspace, params OpenAudible[] books)
    {
        return new SortPlanner().Plan(books, workspace.Source, workspace.Destination, LibraryManifest.Load(workspace.Destination));
    }
}
