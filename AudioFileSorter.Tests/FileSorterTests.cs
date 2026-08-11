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

        Assert.Equal(["An Author/A Book.m4b"], workspace.DestinationFiles());
        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(0, summary.FailedBooks);
        Assert.Equal("audio-bytes", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book.m4b")));
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

        Assert.Equal(["An Author/A Book.m4b", "An Author/A Book.pdf"], workspace.DestinationFiles());
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

        Assert.Equal(1, first.CopiedBooks);
        Assert.Equal(0, second.CopiedBooks);
        Assert.Equal(1, second.SkippedBooks);
        Assert.Single(workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sort_replaces_a_destination_file_whose_contents_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "new-audio");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "old-audio!");

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal("new-audio", File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book.m4b")));
    }

    [Fact]
    public async Task Sort_does_not_create_folders_for_books_with_no_files()
    {
        using var workspace = new TempWorkspace();

        var summary = await Sort(workspace, TempWorkspace.Book());

        Assert.Empty(workspace.DestinationDirectories());
        Assert.Equal(0, summary.CopiedBooks);
        Assert.Equal(1, summary.SkippedBooks);
        Assert.Equal(1, summary.WarningCount);
    }

    [Fact]
    public async Task Sort_keeps_going_when_one_book_cannot_be_written()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("good.m4b");
        workspace.WriteSourceFile("blocked.m4b");

        // A folder sitting where the file should go makes the copy fail for that book only.
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Blocked Book.m4b"));

        var summary = await Sort(
            workspace,
            TempWorkspace.Book(title: "Good Book", filename: "good"),
            TempWorkspace.Book(title: "Blocked Book", filename: "blocked"));

        Assert.Equal(2, summary.TotalBooks);
        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(1, summary.FailedBooks);
        Assert.Contains("An Author/Good Book.m4b", workspace.DestinationFiles());
        Assert.Contains(summary.Warnings, warning => warning.Contains("blocked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sort_leaves_no_partial_file_behind_when_a_copy_fails()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("blocked.m4b");
        Directory.CreateDirectory(Path.Combine(workspace.Destination, "An Author", "Blocked Book.m4b"));

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

        using var cancellation = new CancellationTokenSource();
        var sortTask = new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, books, cancellationToken: cancellation.Token);

        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));

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
        await WaitForAsync(() => reports.Any(r => r.IsComplete));
        Assert.All(reports, report => Assert.False(double.IsNaN(report.Percentage)));
    }

    [Fact]
    public async Task Sort_rejects_a_missing_source_folder_instead_of_silently_doing_nothing()
    {
        using var workspace = new TempWorkspace();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new FileSorter().SortAudioFiles(
            Path.Combine(workspace.Root, "does-not-exist"), workspace.Destination, [TempWorkspace.Book()]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sort_rejects_a_missing_path(string? path)
    {
        using var workspace = new TempWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => new FileSorter().SortAudioFiles(
            path, workspace.Destination, [TempWorkspace.Book()]));

        await Assert.ThrowsAsync<ArgumentException>(() => new FileSorter().SortAudioFiles(
            workspace.Source, path, [TempWorkspace.Book()]));
    }

    [Fact]
    public async Task Sort_creates_the_destination_folder_when_it_does_not_exist_yet()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b");
        var destination = Path.Combine(workspace.Root, "new-destination");

        await new FileSorter().SortAudioFiles(workspace.Source, destination, [TempWorkspace.Book()]);

        Assert.True(File.Exists(Path.Combine(destination, "An Author", "A Book.m4b")));
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
                return reports.Any(r => r.IsComplete);
            }
        });

        lock (reports)
        {
            Assert.All(reports, report => Assert.InRange(report.Percentage, 0, 100));
            Assert.Equal(100, reports.Last(r => r.IsComplete).Percentage);
        }
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

        Assert.Equal(120, summary.CopiedBooks);
        Assert.Equal(0, summary.FailedBooks);
        Assert.Equal(3, Directory.GetDirectories(workspace.Destination).Length);
        Assert.Equal(120, workspace.DestinationFiles().Length);
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
