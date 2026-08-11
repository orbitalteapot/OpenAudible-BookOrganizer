using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

/// <summary>
/// The two update checks. Authors re-issue audiobooks, so a book already at the destination has to
/// be replaced when its source changes — the two modes differ only in how hard they look.
/// </summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData(FileComparisonMode.Quick)]
    [InlineData(FileComparisonMode.Full)]
    public async Task Either_mode_replaces_a_book_whose_size_changed(FileComparisonMode mode)
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "the-re-recorded-and-longer-edition");
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "old");

        var summary = await Sort(workspace, mode, TempWorkspace.Book());

        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(1, summary.UpdatedBooks);
        Assert.Equal(
            "the-re-recorded-and-longer-edition",
            File.ReadAllText(Path.Combine(workspace.Destination, "An Author", "A Book.m4b")));
    }

    [Theory]
    [InlineData(FileComparisonMode.Quick)]
    [InlineData(FileComparisonMode.Full)]
    public async Task Either_mode_leaves_an_identical_book_alone(FileComparisonMode mode)
    {
        using var workspace = new TempWorkspace();
        var content = new string('x', 300_000);
        workspace.WriteSourceFile("a-book.m4b", content);
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), content);
        var writtenAt = File.GetLastWriteTimeUtc(destination);

        var summary = await Sort(workspace, mode, TempWorkspace.Book());

        Assert.Equal(0, summary.CopiedBooks);
        Assert.Equal(0, summary.UpdatedBooks);
        Assert.Equal(1, summary.SkippedBooks);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(destination));
    }

    /// <summary>
    /// The sampled check reads the first, middle and last 4 KB. A re-release that kept the exact
    /// same length and changed only the space between those windows is invisible to it — which is
    /// the whole reason the full check exists.
    /// </summary>
    [Fact]
    public async Task Quick_mode_misses_a_same_size_edit_between_the_sampled_chunks()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var summary = await Sort(workspace, FileComparisonMode.Quick, TempWorkspace.Book());

        Assert.Equal(0, summary.CopiedBooks);
        Assert.Equal(1, summary.SkippedBooks);
        Assert.Equal(original, File.ReadAllText(destination));
    }

    /// <summary>
    /// The other half of the quick check's contract: what it samples, it must actually compare.
    /// Without this, the sampling loop could be deleted entirely and every other test would still
    /// pass, because they only ever assert that quick mode does *not* notice a difference.
    /// </summary>
    [Theory]
    [InlineData(TempWorkspace.SampleWindow.Head)]
    [InlineData(TempWorkspace.SampleWindow.Middle)]
    [InlineData(TempWorkspace.SampleWindow.Tail)]
    public async Task Quick_mode_replaces_a_same_size_edit_inside_a_sampled_chunk(TempWorkspace.SampleWindow window)
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditInWindow(window);
        workspace.WriteSourceFile("a-book.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var summary = await Sort(workspace, FileComparisonMode.Quick, TempWorkspace.Book());

        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(1, summary.UpdatedBooks);
        Assert.Equal(edited, File.ReadAllText(destination));
    }

    [Fact]
    public async Task Full_mode_replaces_a_same_size_edit_between_the_sampled_chunks()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var summary = await Sort(workspace, FileComparisonMode.Full, TempWorkspace.Book());

        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(1, summary.UpdatedBooks);
        Assert.Equal(0, summary.FailedBooks);
        Assert.Equal(edited, File.ReadAllText(destination));
    }

    [Fact]
    public async Task Full_mode_replaces_a_changed_companion_pdf()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", "audio");
        workspace.WriteSourceFile("a-book.pdf", edited);
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), "audio");
        var destinationPdf = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.pdf"), original);

        var summary = await Sort(workspace, FileComparisonMode.Full, TempWorkspace.Book());

        Assert.Equal(1, summary.UpdatedBooks);
        Assert.Equal(edited, File.ReadAllText(destinationPdf));
    }

    [Fact]
    public async Task A_brand_new_book_counts_as_copied_but_not_as_updated()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");

        var summary = await Sort(workspace, FileComparisonMode.Full, TempWorkspace.Book());

        Assert.Equal(1, summary.CopiedBooks);
        Assert.Equal(0, summary.UpdatedBooks);
    }

    [Fact]
    public async Task Quick_is_the_default_when_no_options_are_supplied()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var summary = await new FileSorter().SortAudioFiles(
            workspace.Source, workspace.Destination, [TempWorkspace.Book()]);

        Assert.Equal(0, summary.CopiedBooks);
        Assert.Equal(original, File.ReadAllText(destination));
    }

    [Fact]
    public async Task Full_mode_reports_updates_through_progress()
    {
        using var workspace = new TempWorkspace();
        var (original, edited) = TempWorkspace.SameSizeEditedPair();
        workspace.WriteSourceFile("a-book.m4b", edited);
        workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), original);

        var reports = new List<SortProgressInfo>();
        await new FileSorter().SortAudioFiles(
            workspace.Source,
            workspace.Destination,
            [TempWorkspace.Book()],
            new SortOptions { ComparisonMode = FileComparisonMode.Full },
            new InlineTestProgress(reports.Add));

        Assert.Contains(reports, report => report.UpdatedBooks == 1);
        Assert.Equal(1, reports[^1].UpdatedBooks);
    }

    /// <summary>
    /// Cancels mid-run, once books are actually being compared and copied, rather than before the
    /// first one — a run that aborts at the starting gate never enters the byte-for-byte loop and
    /// proves nothing about it.
    /// </summary>
    [Fact]
    public async Task Full_mode_honours_cancellation_once_it_is_already_comparing()
    {
        using var workspace = new TempWorkspace();
        var books = new List<OpenAudible>();
        for (var i = 0; i < 300; i++)
        {
            var content = new string((char)('a' + (i % 26)), 200_000);
            workspace.WriteSourceFile($"book-{i}.m4b", content);

            // Half the books already exist at the destination with identical contents, so the run
            // has to read both files in full before deciding to skip them.
            if (i % 2 == 0)
            {
                workspace.WriteDestinationFile(Path.Combine("An Author", $"Book {i}.m4b"), content);
            }

            books.Add(TempWorkspace.Book(title: $"Book {i}", filename: $"book-{i}"));
        }

        using var cancellation = new CancellationTokenSource();
        var comparedBeforeCancel = 0;

        var progress = new InlineTestProgress(report =>
        {
            comparedBeforeCancel = report.CurrentBook;
            if (report.CurrentBook >= 5)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileSorter().SortAudioFiles(
                workspace.Source,
                workspace.Destination,
                books,
                new SortOptions { ComparisonMode = FileComparisonMode.Full },
                progress,
                cancellation.Token));

        Assert.True(comparedBeforeCancel >= 5, $"expected the run to get going before cancelling, got {comparedBeforeCancel}");
        Assert.True(comparedBeforeCancel < books.Count, "the run finished instead of being cancelled");

        Assert.Empty(Directory.GetFiles(workspace.Destination, "*.oabo-partial", SearchOption.AllDirectories));

        // Whatever was written before the cancel must be complete, not truncated.
        foreach (var file in Directory.GetFiles(workspace.Destination, "*.m4b", SearchOption.AllDirectories))
        {
            Assert.Equal(200_000, new FileInfo(file).Length);
        }
    }

    /// <summary>
    /// The byte-for-byte comparison is the only unbounded loop in a sort. On a large library a
    /// cancelled run must stop inside it, not after it — otherwise Cancel reports success while
    /// the backend keeps reading whole files.
    /// </summary>
    [Fact]
    public async Task The_byte_for_byte_comparison_stops_when_cancelled()
    {
        using var workspace = new TempWorkspace();
        var content = new string('z', 2_000_000);
        var first = workspace.WriteSourceFile("a-book.m4b", content);
        var second = workspace.WriteDestinationFile("copy.m4b", content);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileSorter.AreFilesIdenticalAsync(first, second, cancellation.Token));
    }

    [Fact]
    public async Task The_byte_for_byte_comparison_still_reports_identical_files_as_identical()
    {
        using var workspace = new TempWorkspace();
        var content = new string('z', 2_000_000);
        var first = workspace.WriteSourceFile("a-book.m4b", content);
        var second = workspace.WriteDestinationFile("copy.m4b", content);

        Assert.True(await FileSorter.AreFilesIdenticalAsync(first, second, CancellationToken.None));
    }

    [Fact]
    public async Task The_byte_for_byte_comparison_spots_a_difference_in_the_final_block()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.WriteSourceFile("a-book.m4b", new string('z', 2_000_000) + "end-a");
        var second = workspace.WriteDestinationFile("copy.m4b", new string('z', 2_000_000) + "end-b");

        Assert.False(await FileSorter.AreFilesIdenticalAsync(first, second, CancellationToken.None));
    }

    [Fact]
    public async Task Full_mode_aborts_before_touching_anything_when_the_token_is_already_cancelled()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", "audio");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileSorter().SortAudioFiles(
                workspace.Source,
                workspace.Destination,
                [TempWorkspace.Book()],
                new SortOptions { ComparisonMode = FileComparisonMode.Full },
                cancellationToken: cancellation.Token));

        Assert.Empty(workspace.DestinationFiles());
    }

    [Fact]
    public async Task Full_mode_still_replaces_a_truncated_destination()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("a-book.m4b", new string('y', 100_000));
        var destination = workspace.WriteDestinationFile(Path.Combine("An Author", "A Book.m4b"), new string('y', 40_000));

        var summary = await Sort(workspace, FileComparisonMode.Full, TempWorkspace.Book());

        Assert.Equal(1, summary.UpdatedBooks);
        Assert.Equal(100_000, new FileInfo(destination).Length);
    }

    [Theory]
    [InlineData(null, FileComparisonMode.Quick)]
    [InlineData("", FileComparisonMode.Quick)]
    [InlineData("   ", FileComparisonMode.Quick)]
    [InlineData("quick", FileComparisonMode.Quick)]
    [InlineData("QUICK", FileComparisonMode.Quick)]
    [InlineData(" Fast ", FileComparisonMode.Quick)]
    [InlineData("sampled", FileComparisonMode.Quick)]
    [InlineData("full", FileComparisonMode.Full)]
    [InlineData("Full", FileComparisonMode.Full)]
    [InlineData("verify", FileComparisonMode.Full)]
    [InlineData("exact", FileComparisonMode.Full)]
    [InlineData("content", FileComparisonMode.Full)]
    public void Comparison_mode_parses_the_spellings_the_api_accepts(string? value, FileComparisonMode expected)
    {
        Assert.True(SortOptions.TryParseComparisonMode(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("thorough")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("quick-ish")]
    public void An_unrecognised_comparison_mode_is_rejected_rather_than_downgraded(string value)
    {
        Assert.False(SortOptions.TryParseComparisonMode(value, out _));
    }

    [Theory]
    [InlineData(FileComparisonMode.Quick, "quick")]
    [InlineData(FileComparisonMode.Full, "full")]
    public void Comparison_mode_round_trips_through_its_wire_value(FileComparisonMode mode, string expected)
    {
        Assert.Equal(expected, SortOptions.ToWireValue(mode));
        Assert.True(SortOptions.TryParseComparisonMode(SortOptions.ToWireValue(mode), out var parsed));
        Assert.Equal(mode, parsed);
    }

    private static Task<SortSummary> Sort(TempWorkspace workspace, FileComparisonMode mode, params OpenAudible[] books)
    {
        return new FileSorter().SortAudioFiles(
            workspace.Source,
            workspace.Destination,
            [.. books],
            new SortOptions { ComparisonMode = mode });
    }

    /// <summary>Reports on the calling thread so the assertions do not race the thread pool.</summary>
    private sealed class InlineTestProgress(Action<SortProgressInfo> handler) : IProgress<SortProgressInfo>
    {
        public void Report(SortProgressInfo value) => handler(value);
    }
}
