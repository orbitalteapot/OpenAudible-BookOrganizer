using AudioFileSorter.Model;
using ManagerApi.Services;

namespace AudioFileSorter.Tests;

public class SortServiceTests
{
    [Fact]
    public async Task TryStartSort_runs_a_sort_and_reports_completion()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var csvPath = WriteCsv(workspace, "The Hobbit,Tolkien,the-hobbit");

        var service = new SortService();

        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;

        var progress = service.GetProgress();
        Assert.True(progress.IsComplete);
        Assert.Null(progress.Error);
        Assert.Equal(1, progress.CopiedBooks);
        Assert.False(service.IsSorting);
        Assert.Equal(["Tolkien/The Hobbit.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task TryStartSort_passes_the_requested_update_check_through_to_the_sorter()
    {
        using var workspace = new TempWorkspace();

        // Same length, differing only outside the chunks the quick check samples, so the mode the
        // service hands down is the only thing that decides whether this book is replaced.
        var (original, edited) = TempWorkspace.SameSizeEditedPair();

        workspace.WriteSourceFile("the-hobbit.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("Tolkien", "The Hobbit.m4b"), original);
        var csvPath = WriteCsv(workspace, "The Hobbit,Tolkien,the-hobbit");

        var quickService = new SortService();
        Assert.True(quickService.TryStartSort(
            csvPath, workspace.Source, workspace.Destination,
            new SortOptions { ComparisonMode = FileComparisonMode.Quick }, out var quickTask));
        await quickTask;

        Assert.Equal(0, quickService.GetProgress().UpdatedBooks);
        Assert.Equal(original, File.ReadAllText(destination));

        var fullService = new SortService();
        Assert.True(fullService.TryStartSort(
            csvPath, workspace.Source, workspace.Destination,
            new SortOptions { ComparisonMode = FileComparisonMode.Full }, out var fullTask));
        await fullTask;

        Assert.Equal(1, fullService.GetProgress().UpdatedBooks);
        Assert.Equal(edited, File.ReadAllText(destination));
    }

    [Fact]
    public async Task A_late_progress_report_cannot_un_finish_a_completed_sort()
    {
        // A per-book report delivered after the run ended used to overwrite the completed state.
        // The UI stops polling on that flag, so losing it left it spinning on a finished run.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var csvPath = WriteCsv(workspace, "The Hobbit,Tolkien,the-hobbit");

        var service = new SortService();
        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;
        Assert.True(service.GetProgress().IsComplete);

        service.SetProgress(new SortProgressInfo { CurrentBook = 1, TotalBooks = 25, Percentage = 4 });

        var progress = service.GetProgress();
        Assert.True(progress.IsComplete);
        Assert.Equal(100, progress.Percentage);
    }

    [Fact]
    public async Task A_new_run_clears_the_completed_state_of_the_previous_one()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var csvPath = WriteCsv(workspace, "The Hobbit,Tolkien,the-hobbit");

        var service = new SortService();
        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var first));
        await first;

        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var second));
        await second;

        var progress = service.GetProgress();
        Assert.True(progress.IsComplete);
        Assert.Equal(1, progress.TotalBooks);
    }

    [Fact]
    public async Task TryStartSort_refuses_a_second_concurrent_run()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < 60; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b", new string('x', 300_000));
        }

        var csvPath = WriteCsv(
            workspace,
            Enumerable.Range(0, 60).Select(i => $"Book {i},Author,book-{i}").ToArray());

        var service = new SortService();
        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var first));

        var startedAgain = service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out _);

        await first;
        Assert.False(startedAgain);
    }

    [Fact]
    public async Task Parallel_start_attempts_only_ever_start_one_run()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < 40; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b", new string('x', 200_000));
        }

        var csvPath = WriteCsv(
            workspace,
            Enumerable.Range(0, 40).Select(i => $"Book {i},Author,book-{i}").ToArray());

        var service = new SortService();
        var started = 0;
        var tasks = new List<Task>();

        Parallel.For(0, 8, _ =>
        {
            if (service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var task))
            {
                Interlocked.Increment(ref started);
                lock (tasks)
                {
                    tasks.Add(task);
                }
            }
        });

        await Task.WhenAll(tasks);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task CancelSort_stops_a_running_sort_and_records_it()
    {
        using var workspace = new TempWorkspace();
        for (var i = 0; i < 200; i++)
        {
            workspace.WriteSourceFile($"book-{i}.m4b", new string('x', 300_000));
        }

        var csvPath = WriteCsv(
            workspace,
            Enumerable.Range(0, 200).Select(i => $"Book {i},Author,book-{i}").ToArray());

        var service = new SortService();
        Assert.True(service.TryStartSort(csvPath, workspace.Source, workspace.Destination, out var sortTask));

        // Wait until the run is actually under way before cancelling it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.GetProgress().CurrentBook == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(service.CancelSort());
        await sortTask;

        var progress = service.GetProgress();
        Assert.True(progress.IsComplete);
        Assert.True(progress.IsCanceled);
        Assert.False(service.IsSorting);
    }

    [Fact]
    public void CancelSort_returns_false_when_nothing_is_running()
    {
        Assert.False(new SortService().CancelSort());
    }

    [Fact]
    public async Task A_failing_sort_reports_the_error_instead_of_hanging()
    {
        using var workspace = new TempWorkspace();
        var csvPath = WriteCsv(workspace, "The Hobbit,Tolkien,the-hobbit");

        var service = new SortService();
        Assert.True(service.TryStartSort(csvPath, Path.Combine(workspace.Root, "missing"), workspace.Destination, out var sortTask));
        await sortTask;

        var progress = service.GetProgress();
        Assert.True(progress.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(progress.Error));
        Assert.False(service.IsSorting);
    }

    [Fact]
    public async Task ParseBooks_reloads_when_the_csv_path_changes()
    {
        using var workspace = new TempWorkspace();
        var firstCsv = WriteCsv(workspace, "Book One,Author,book-one");
        var secondCsv = WriteCsv(workspace, "Book Two,Author,book-two");

        var service = new SortService();
        await service.ParseBooks(firstCsv);
        Assert.Equal("Book One", service.GetBooks()[0].Title);

        await service.ParseBooks(secondCsv);
        Assert.Equal("Book Two", service.GetBooks()[0].Title);
    }

    [Fact]
    public async Task Sorting_a_different_csv_than_the_loaded_one_uses_the_requested_file()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");

        var loadedCsv = WriteCsv(workspace, "Book One,Author,book-one");
        var requestedCsv = WriteCsv(workspace, "Book Two,Author,book-two");

        var service = new SortService();
        await service.ParseBooks(loadedCsv);

        Assert.True(service.TryStartSort(requestedCsv, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;

        Assert.Equal(["Author/Book Two.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sorting_re_reads_the_csv_when_it_has_been_re_exported_over_the_top()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");

        // OpenAudible writes over the same file every export, and a container's CSV_PATH never
        // changes at all, so "same path" cannot be taken to mean "same library".
        var csv = WriteCsv(workspace, "Book One,Author,book-one");

        var service = new SortService();
        await service.ParseBooks(csv);

        await OverwriteCsv(csv, "Book One,Author,book-one", "Book Two,Author,book-two");

        Assert.True(service.TryStartSort(csv, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;

        Assert.Equal(["Author/Book One.m4b", "Author/Book Two.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sorting_reuses_the_loaded_library_when_the_csv_has_not_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");

        var csv = WriteCsv(workspace, "Book One,Author,book-one");

        var service = new SortService();
        await service.ParseBooks(csv);
        var loaded = service.GetBooks();

        Assert.True(service.TryStartSort(csv, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;

        // Same instance, not merely equal: an untouched file must not be read again, and the
        // library on screen must not be swapped out underneath the person looking at it.
        Assert.Same(loaded, service.GetBooks());
    }

    [Fact]
    public async Task A_re_export_that_removes_books_is_picked_up_too()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");

        var csv = WriteCsv(workspace, "Book One,Author,book-one", "Book Two,Author,book-two");

        var service = new SortService();
        await service.ParseBooks(csv);
        Assert.Equal(2, service.GetBooks().Count);

        await OverwriteCsv(csv, "Book Two,Author,book-two");

        Assert.True(service.TryStartSort(csv, workspace.Source, workspace.Destination, out var sortTask));
        await sortTask;

        Assert.Equal(["Author/Book Two.m4b"], workspace.DestinationFiles());
    }

    /// <summary>
    /// Rewrites an export in place. The delay is deliberate: the change is detected from the
    /// file's last-write time, and a same-millisecond rewrite of the same length would be
    /// indistinguishable from no change at all on a coarse filesystem clock.
    /// </summary>
    private static async Task OverwriteCsv(string path, params string[] rows)
    {
        await Task.Delay(20);
        await File.WriteAllTextAsync(path, "Title,Author,File name\n" + string.Join("\n", rows) + "\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private static string WriteCsv(TempWorkspace workspace, params string[] rows)
    {
        var path = Path.Combine(workspace.Root, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "Title,Author,File name\n" + string.Join("\n", rows) + "\n");
        return path;
    }
}
