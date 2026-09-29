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
                "No file for this book in the source folder"),
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
    public async Task Sort_updates_a_moved_loose_book_whose_source_has_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "old-audio!");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(["An Author/A Book/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal("new-audio", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book", "A Book.m4b")));
        // Moved and then replaced: counted once, as the stronger of the two.
        Assert.Equal(SortCounts.Empty with { Updated = 1 }, summary.Counts);
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
