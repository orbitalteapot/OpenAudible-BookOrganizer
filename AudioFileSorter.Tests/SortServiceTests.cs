using AudioFileSorter.Model;
using ManagerApi.Services;

namespace AudioFileSorter.Tests;

public class SortServiceTests
{
    [Fact]
    public async Task TryStartSort_runs_a_sort_with_the_paths_in_the_settings_and_reports_completion()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        var final = await run;

        Assert.Equal(RunState.Finished, final.State);
        Assert.Equal(RunTrigger.Manual, final.Trigger);
        Assert.Null(final.Error);
        Assert.Equal(1, final.Counts.New);
        Assert.NotNull(final.FinishedUtc);
        Assert.Equal(final, backend.Sort.GetStatus());
        Assert.False(backend.Sort.IsSorting);
        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public void Nothing_has_run_until_a_sort_is_started()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv());

        Assert.Equal(RunState.Idle, backend.Sort.GetStatus().State);
    }

    [Fact]
    public async Task TryStartSort_passes_the_requested_update_check_through_to_the_sorter()
    {
        using var workspace = new TempWorkspace();

        // Same length, differing only outside the chunks the quick check samples, so the mode the
        // service hands down is the only thing that decides whether this book is replaced.
        var (original, edited) = TempWorkspace.SameSizeEditedPair();

        workspace.WriteSourceFile("the-hobbit.m4b", edited);
        var destination = workspace.WriteDestinationFile(Path.Combine("Tolkien", "The Hobbit", "The Hobbit.m4b"), original);
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"));

        Assert.True(backend.Sort.TryStartSort(
            RunTrigger.Manual, new SortOptions { ComparisonMode = FileComparisonMode.Quick }, out var quickRun));
        Assert.Equal(0, (await quickRun).Counts.Updated);
        Assert.Equal(original, File.ReadAllText(destination));

        Assert.True(backend.Sort.TryStartSort(
            RunTrigger.Manual, new SortOptions { ComparisonMode = FileComparisonMode.Full }, out var fullRun));
        Assert.Equal(1, (await fullRun).Counts.Updated);
        Assert.Equal(edited, File.ReadAllText(destination));
    }

    [Fact]
    public async Task A_new_run_replaces_the_status_of_the_previous_one()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var first));
        await first;
        Assert.True(backend.Sort.TryStartSort(RunTrigger.Scheduled, SortOptions.Default, out var second));
        var final = await second;

        Assert.Equal(RunTrigger.Scheduled, backend.Sort.GetStatus().Trigger);
        Assert.Equal(1, final.Counts.UpToDate);
        Assert.Equal(0, final.Counts.New);
    }

    [Fact]
    public async Task A_second_start_joins_the_run_already_going()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Scheduled, new SortOptions { MaxParallelism = 1 }, out var first));
        var startedAgain = backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var joined);

        Assert.False(startedAgain);
        Assert.Same(first, joined);
        Assert.Equal(RunTrigger.Scheduled, (await first).Trigger);
    }

    [Fact]
    public async Task Parallel_start_attempts_only_ever_start_one_run()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(40, 200_000));

        var started = 0;
        var runs = new List<Task>();

        Parallel.For(0, 8, _ =>
        {
            if (backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run))
            {
                Interlocked.Increment(ref started);
                lock (runs)
                {
                    runs.Add(run);
                }
            }
        });

        await Task.WhenAll(runs);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task While_running_the_latest_problems_are_sent_and_the_capped_list_once_finished()
    {
        using var workspace = new TempWorkspace();
        var total = SortSummary.MaxReportedProblems + 20;
        var rows = Enumerable.Range(0, total).Select(i => $"Missing {i},Author,missing-{i}").ToArray();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv(rows));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        var final = await run;

        Assert.Equal(SortSummary.MaxReportedProblems, final.Problems.Count);
        Assert.Equal(SortSummary.MaxReportedProblems, backend.Sort.GetStatus().Problems.Count);

        // Past the cap the kept list stops growing; a page following the run must still see the
        // problems as they happen, not the same 20 from the 500th onwards.
        var running = final with { State = RunState.Running };
        var polled = running.ForPolling();
        Assert.Equal(SortProgressInfo.RecentProblemLimit, polled.Problems.Count);
        Assert.All(polled.Problems, problem => Assert.DoesNotContain(problem, final.Problems));
        Assert.Equal(total, polled.ProblemCount);
    }

    [Fact]
    public async Task CancelSort_stops_a_running_sort_and_keeps_what_it_did()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200)).HoldRunsUntilCanceled();

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, new SortOptions { MaxParallelism = 1 }, out var run));
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the first book");

        Assert.True(backend.Sort.CancelSort());
        var final = await run;

        Assert.Equal(RunState.Finished, final.State);
        Assert.True(final.IsCanceled);
        Assert.Null(final.Error);
        Assert.True(final.CurrentBook > 0);
        Assert.False(backend.Sort.IsSorting);
    }

    [Fact]
    public async Task Closing_the_app_cancels_the_run_and_says_so()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200)).HoldRunsUntilCanceled();

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, new SortOptions { MaxParallelism = 1 }, out var run));
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the first book");

        backend.Lifetime.StopApplication();
        var final = await run;

        Assert.True(final.IsCanceled);
        Assert.Equal("Canceled because the app closed.", final.Error);
    }

    [Fact]
    public async Task Closing_the_app_waits_for_a_sort_started_from_the_page_to_say_why_it_stopped()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(200)).HoldRunsUntilCanceled();

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, new SortOptions { MaxParallelism = 1 }, out _));
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the first book");

        // What the host does on docker stop: stopping cancels the run, then each hosted service is stopped.
        backend.Lifetime.StopApplication();
        await backend.Sort.StopAsync(CancellationToken.None);

        // Nothing waited for it, so the process exited before the run said, or logged, why it ended.
        var status = backend.Sort.GetStatus();
        Assert.Equal(RunState.Finished, status.State);
        Assert.Equal("Canceled because the app closed.", status.Error);
    }

    [Fact]
    public void CancelSort_returns_false_when_nothing_is_running()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv());

        Assert.False(backend.Sort.CancelSort());
    }

    [Fact]
    public void Paths_that_cannot_work_are_refused_before_anything_starts()
    {
        using var workspace = new TempWorkspace();
        var missing = Path.Combine(workspace.Root, "unplugged");
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), destination: missing);

        var ex = Assert.Throws<SortPathException>(() => backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out _));

        Assert.Equal(SortPathProblemCode.DestinationMissing, ex.Problem.Code);
        Assert.Equal(RunState.Idle, backend.Sort.GetStatus().State);
        Assert.False(backend.Sort.IsSorting);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task A_missing_destination_is_created_when_the_person_asked_for_it()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var missing = Path.Combine(workspace.Root, "new-destination");
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), destination: missing);

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, new SortOptions { CreateDestination = true }, out var run));

        Assert.Equal(1, (await run).Counts.New);
        Assert.True(Directory.Exists(missing));
    }

    [Fact]
    public async Task ParseBooks_reads_the_export_in_the_settings()
    {
        using var workspace = new TempWorkspace();
        var firstCsv = workspace.WriteCsv("Book One,Author,book-one");
        var secondCsv = workspace.WriteCsv("Book Two,Author,book-two");
        using var backend = new TestBackend(new ServerConfig());

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CsvPath = firstCsv }, out _));
        await backend.Sort.ParseBooks();
        Assert.Equal("Book One", backend.Sort.GetBooks()[0].Title);

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CsvPath = secondCsv }, out _));
        await backend.Sort.ParseBooks();
        Assert.Equal("Book Two", backend.Sort.GetBooks()[0].Title);
    }

    [Fact]
    public async Task ParseBooks_without_an_export_says_which_setting_is_missing()
    {
        using var backend = new TestBackend(new ServerConfig());

        var ex = await Assert.ThrowsAsync<SortPathException>(() => backend.Sort.ParseBooks());

        Assert.Equal(SortPathField.Csv, ex.Problem.Field);
        Assert.Equal(SortPathProblemCode.NotSet, ex.Problem.Code);
    }

    [Fact]
    public async Task ParseBooks_is_allowed_while_a_sort_runs_and_the_run_keeps_its_own_list()
    {
        using var workspace = new TempWorkspace();
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteLargeLibrary(100));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, new SortOptions { MaxParallelism = 1 }, out var run));
        await TestBackend.WaitUntil(() => backend.Sort.GetStatus().CurrentBook > 0, "the first book");

        var parsed = await backend.Sort.ParseBooks();

        Assert.Equal(100, parsed.Books.Count);
        Assert.Equal(100, (await run).TotalBooks);
    }

    [Fact]
    public async Task Sorting_uses_the_export_in_the_settings_rather_than_the_one_loaded()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");
        var loadedCsv = workspace.WriteCsv("Book One,Author,book-one");
        var chosenCsv = workspace.WriteCsv("Book Two,Author,book-two");
        using var backend = new TestBackend(new ServerConfig());

        Assert.True(backend.Settings.TryUpdate(
            new AppSettingsPatch { CsvPath = loadedCsv, SourcePath = workspace.Source, DestinationPath = workspace.Destination }, out _));
        await backend.Sort.ParseBooks();
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CsvPath = chosenCsv }, out _));

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        await run;

        Assert.Equal(["Author/Book Two/Book Two.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sorting_re_reads_the_csv_when_it_has_been_re_exported_over_the_top()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");

        // OpenAudible writes over the same file every export, and a container's CSV_PATH never
        // changes at all, so "same path" cannot be taken to mean "same library".
        var csv = workspace.WriteCsv("Book One,Author,book-one");
        using var backend = TestBackend.LockedTo(workspace, csv);
        await backend.Sort.ParseBooks();

        await OverwriteCsv(csv, "Book One,Author,book-one", "Book Two,Author,book-two");

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        await run;

        Assert.Equal(["Author/Book One/Book One.m4b", "Author/Book Two/Book Two.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Sorting_reuses_the_loaded_library_when_the_csv_has_not_changed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        using var backend = TestBackend.LockedTo(workspace, workspace.WriteCsv("Book One,Author,book-one"));
        await backend.Sort.ParseBooks();
        var loaded = backend.Sort.GetBooks();

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        await run;

        // Same instance, not merely equal: an untouched file must not be read again, and the
        // library on screen must not be swapped out underneath the person looking at it.
        Assert.Same(loaded, backend.Sort.GetBooks());
    }

    [Fact]
    public async Task A_re_export_that_removes_books_is_picked_up_too()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("book-one.m4b");
        workspace.WriteSourceFile("book-two.m4b");
        var csv = workspace.WriteCsv("Book One,Author,book-one", "Book Two,Author,book-two");
        using var backend = TestBackend.LockedTo(workspace, csv);
        await backend.Sort.ParseBooks();
        Assert.Equal(2, backend.Sort.GetBooks().Count);

        await OverwriteCsv(csv, "Book Two,Author,book-two");

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        await run;

        Assert.Equal(["Author/Book Two/Book Two.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task An_export_that_is_not_from_openaudible_fails_the_run_with_a_code()
    {
        using var workspace = new TempWorkspace();
        var csv = Path.Combine(workspace.Root, "not-an-export.csv");
        File.WriteAllText(csv, "Name,Colour\nApple,Red\n");
        using var backend = TestBackend.LockedTo(workspace, csv);

        Assert.True(backend.Sort.TryStartSort(RunTrigger.Manual, SortOptions.Default, out var run));
        var final = await run;

        Assert.Equal(RunErrors.CsvInvalid, final.ErrorCode);
        Assert.Equal("csvPath", final.ErrorField);
        Assert.False(backend.Sort.IsSorting);
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
}
