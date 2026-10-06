using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AudioFileSorter.Model;
using Xunit.Abstractions;

namespace AudioFileSorter.Tests;

/// <summary>
/// Runs <see cref="NoDataLossPropertyTests"/> on its own once the other tests are done: it keeps every
/// core busy, which would starve the tests that wait on timers.
/// </summary>
[CollectionDefinition(nameof(NoDataLossPropertyTests), DisableParallelization = true)]
public sealed class NoDataLossPropertyCollection;

/// <summary>
/// Sorts thousands of random libraries, several times each, and checks after every run that no file
/// already in the destination was lost or filed with another book (see FileSorter.FileOwnership):
/// <list type="bullet">
/// <item>
/// every content the destination held before a run is still somewhere in it afterwards, unless it was
/// a book's own copy, recorded for it in the manifest, that the book's changed download replaced;
/// </item>
/// <item>
/// a file that moved went to a folder the manifest records for the book whose content it is, never
/// another's: not even a PDF named like the book beside audio that moved there, which older versions
/// may have left for a same-titled book that had only its PDF;
/// </item>
/// <item>
/// after a run that finished, an audio file directly in an author or series folder that holds book
/// folders is named in the problems (unless the run left that folder exactly as it found it).
/// </item>
/// </list>
///
/// The libraries are built from a small pool of names chosen to collide: two spellings of an author,
/// titles that differ only in case, "Foo 2" beside the "Foo (2)" older versions wrote, a book called
/// "Saga" beside the series "Saga", an unnumbered series book called "Book 1". Between runs books join
/// and leave the export, change title, author, series and number, gain or lose an ASIN or a PDF, are
/// downloaded again with new content, change format, go missing or are left empty in the source; the
/// manifest is deleted or damaged, and runs are cancelled part way. Half start from the loose layout
/// the version on main left (see <see cref="WriteMainLayout"/>).
///
/// Every file's content says whose it is ("audio|3|1|.m4b" is version 1 of book 3's audio), which is
/// what makes "its own copy" checkable. Scenarios are generated from fixed seeds, and each sort runs one
/// book at a time, so a failure repeats exactly; a failing scenario is minimised before it is reported.
///
/// Set OABO_PROPERTY_SCENARIOS (and OABO_PROPERTY_SEED, the first seed) to run more than the committed
/// <see cref="DefaultScenarios"/>, e.g. <c>OABO_PROPERTY_SCENARIOS=200000 dotnet test --filter NoDataLoss</c>.
/// </summary>
[Collection(nameof(NoDataLossPropertyTests))]
public sealed class NoDataLossPropertyTests(ITestOutputHelper output)
{
    private const int DefaultScenarios = 2000;
    private const int DefaultFirstSeed = 1;

    /// <summary>How many of the smallest failing scenarios of each group (see <see cref="Violation.Group"/>) are minimised and reported.</summary>
    private const int ReportedPerGroup = 3;

    private static readonly string[] Authors = ["Ann Author", "ann author", "Bob Writer"];
    private static readonly string[] Titles = ["Foo", "foo", "Foo 2", "Bar", "Saga", "Book 1", "Alpha"];
    private static readonly string[] SeriesNames = ["Saga", "The Saga Series", "saga", "Foo"];
    private static readonly string[] Sequences = ["1", "2"];

    private static readonly StringComparer PathComparer = StringComparer.FromComparison(PathSanitizer.PathComparison);

    [Fact]
    public async Task No_sort_ever_loses_a_file_or_files_it_with_another_book()
    {
        var count = EnvironmentNumber("OABO_PROPERTY_SCENARIOS") ?? DefaultScenarios;
        var firstSeed = EnvironmentNumber("OABO_PROPERTY_SEED") ?? DefaultFirstSeed;
        var stats = new Stats();
        var failures = new ConcurrentBag<(Scenario Scenario, Violation Violation)>();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForAsync(
            firstSeed,
            firstSeed + count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (seed, _) =>
            {
                var scenario = Generate(seed);
                if (await RunAsync(scenario, stats) is { } violation)
                {
                    failures.Add((scenario, violation));
                }
            });

        output.WriteLine(
            $"{count} scenarios (seeds {firstSeed}..{firstSeed + count - 1}) in {stopwatch.Elapsed.TotalSeconds:0.0} s: {stats}");

        if (failures.IsEmpty)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"{failures.Count} of {count} scenarios broke the invariant:");
        var groups = failures.GroupBy(failure => failure.Violation.Group).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        foreach (var group in groups)
        {
            report.AppendLine($"  {group.Key}: {group.Count()} (seeds {string.Join(", ", group.Select(failure => failure.Scenario.Seed).Order().Take(20))}...)");
        }

        foreach (var group in groups)
        {
            var smallest = group.OrderBy(failure => failure.Scenario.Size).ThenBy(failure => failure.Scenario.Seed).Take(ReportedPerGroup);
            foreach (var (scenario, violation) in smallest)
            {
                var (minimal, minimalViolation) = await MinimiseAsync(scenario, violation);
                report.AppendLine();
                report.AppendLine(Describe(minimal, minimalViolation));
            }
        }

        Assert.Fail(report.ToString());
    }

    // ----- Scenarios -----------------------------------------------------------------------------

    private enum SourceState
    {
        Present,
        Missing,
        Empty
    }

    /// <summary>One book as one row of one export lists it, and what its files in the source folder are.</summary>
    /// <param name="Id">Who the book is: its source files are src-{Id}.*, its ASIN B{Id:D9}.</param>
    /// <param name="AudioVersion">Which download of the audio the source holds; a new one has new content.</param>
    /// <param name="Pdf">Which download of the PDF the source holds, or null for none.</param>
    private sealed record BookState(
        int Id,
        bool Asin,
        string Author,
        string Title,
        string? Series,
        string? Sequence,
        string Extension,
        int AudioVersion,
        SourceState Audio,
        int? Pdf);

    /// <summary>One sort: the export's rows in order (a book listed twice is a repeat), and what happens before and during it.</summary>
    /// <param name="CancelAtReport">Cancel the run at this progress report: 1 is the one made once planning is done.</param>
    /// <param name="UserFile">Put a file of the person's own in the destination folder with this index, before the run.</param>
    private sealed record RunSpec(
        IReadOnlyList<BookState> Rows,
        bool DeleteManifest,
        bool DamageManifest,
        int? CancelAtReport,
        int? UserFile);

    /// <param name="OldLayout">The books the version on main sorted into the destination first, in its export order.</param>
    private sealed record Scenario(int Seed, IReadOnlyList<BookState> OldLayout, IReadOnlyList<RunSpec> Runs)
    {
        public int Size => OldLayout.Count + Runs.Sum(run => run.Rows.Count + 1);
    }

    private static Scenario Generate(int seed)
    {
        var random = new Random(seed);
        var books = Enumerable.Range(0, random.Next(2, 7)).Select(id => NewBook(random, id)).ToArray();

        var oldLayout = new List<BookState>();
        if (random.Next(2) == 0)
        {
            foreach (var id in Shuffled(random, Enumerable.Range(0, books.Length)))
            {
                if (Chance(random, 0.75))
                {
                    oldLayout.Add(books[id] with { Audio = Chance(random, 0.1) ? SourceState.Missing : SourceState.Present });
                }
            }
        }

        var inExport = books.Select(_ => Chance(random, 0.8)).ToArray();
        var order = Shuffled(random, Enumerable.Range(0, books.Length));
        var runs = new List<RunSpec>();
        var runCount = random.Next(1, 5);
        for (var run = 0; run < runCount; run++)
        {
            if (run > 0 || oldLayout.Count > 0)
            {
                for (var id = 0; id < books.Length; id++)
                {
                    books[id] = Mutate(random, books[id]);
                    if (run > 0 && Chance(random, 0.2))
                    {
                        inExport[id] = !inExport[id];
                    }
                }

                if (Chance(random, 0.3))
                {
                    order = Shuffled(random, order);
                }
            }

            // Whether the download is in the source folder is decided afresh for every run.
            var rows = order.Where(id => inExport[id]).Select(id => books[id] with { Audio = RollSource(random) }).ToList();
            if (rows.Count > 0 && Chance(random, 0.06))
            {
                var repeated = rows[random.Next(rows.Count)];
                rows.Insert(random.Next(rows.Count + 1), repeated with { Title = Pick(random, Titles) });
            }

            runs.Add(new RunSpec(
                rows,
                DeleteManifest: run > 0 && Chance(random, 0.15),
                DamageManifest: run > 0 && Chance(random, 0.04),
                CancelAtReport: Chance(random, 0.2) ? random.Next(1, rows.Count + 2) : null,
                UserFile: Chance(random, 0.1) ? random.Next(1000) : null));
        }

        return new Scenario(seed, oldLayout, runs);
    }

    private static BookState NewBook(Random random, int id)
    {
        var series = Chance(random, 0.45) ? Pick(random, SeriesNames) : null;
        return new BookState(
            id,
            Asin: Chance(random, 0.5),
            Author: Pick(random, Authors),
            Title: Pick(random, Titles),
            Series: series,
            Sequence: series is not null && Chance(random, 0.6) ? Pick(random, Sequences) : null,
            Extension: Chance(random, 0.85) ? ".m4b" : ".mp3",
            AudioVersion: 0,
            Audio: SourceState.Present,
            Pdf: Chance(random, 0.35) ? 0 : null);
    }

    /// <summary>What can change about a book between two sorts.</summary>
    private static BookState Mutate(Random random, BookState book)
    {
        if (Chance(random, 0.1))
        {
            book = book with { Title = Pick(random, Titles) };
        }

        if (Chance(random, 0.06))
        {
            book = book with { Author = Pick(random, Authors) };
        }

        if (Chance(random, 0.12))
        {
            book = book.Series is null
                ? book with { Series = Pick(random, SeriesNames), Sequence = Chance(random, 0.5) ? Pick(random, Sequences) : null }
                : Chance(random, 0.5)
                    ? book with { Series = null, Sequence = null }
                    : book with { Series = Pick(random, SeriesNames) };
        }

        if (book.Series is not null && Chance(random, 0.1))
        {
            book = book with { Sequence = book.Sequence is null || Chance(random, 0.5) ? Pick(random, Sequences) : null };
        }

        if (Chance(random, 0.08))
        {
            book = book with { Asin = !book.Asin };
        }

        if (Chance(random, 0.15))
        {
            book = book with { AudioVersion = book.AudioVersion + 1 };
        }

        if (Chance(random, 0.04))
        {
            book = book with { Extension = book.Extension == ".m4b" ? ".mp3" : ".m4b" };
        }

        if (Chance(random, 0.1))
        {
            book = book with { Pdf = book.Pdf is null ? 0 : Chance(random, 0.5) ? null : book.Pdf + 1 };
        }

        return book;
    }

    private static SourceState RollSource(Random random)
    {
        var roll = random.NextDouble();
        return roll < 0.1 ? SourceState.Missing : roll < 0.17 ? SourceState.Empty : SourceState.Present;
    }

    private static bool Chance(Random random, double probability) => random.NextDouble() < probability;

    private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    private static List<int> Shuffled(Random random, IEnumerable<int> values)
    {
        var list = values.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static OpenAudible ToRow(BookState book) => new()
    {
        ASIN = book.Asin ? $"B{book.Id:D9}" : null,
        Title = book.Title,
        Author = book.Author,
        Filename = $"src-{book.Id}",
        SeriesName = book.Series,
        SeriesSequence = book.Sequence,
        M4B = book.Extension == ".m4b" ? "Yes" : null,
        MP3 = book.Extension == ".mp3" ? "Yes" : null
    };

    // ----- Contents: every file says whose it is -------------------------------------------------

    private static string AudioContent(BookState book) => $"audio|{book.Id}|{book.AudioVersion}|{book.Extension}";

    private static string PdfContent(int id, int version) => $"pdf|{id}|{version}";

    private sealed record Owner(int Id, bool IsAudio, string Extension);

    /// <summary>Whose book a content is, or null for a file of the person's own.</summary>
    private static Owner? ContentOwner(string content)
    {
        var parts = content.Split('|');
        return parts[0] switch
        {
            "audio" when parts.Length == 4 => new Owner(int.Parse(parts[1]), IsAudio: true, parts[3]),
            "pdf" when parts.Length == 3 => new Owner(int.Parse(parts[1]), IsAudio: false, ".pdf"),
            _ => null
        };
    }

    private static string Label(string content)
    {
        var parts = content.Split('|');
        return parts[0] switch
        {
            "audio" when parts.Length == 4 => $"book {parts[1]} audio v{parts[2]}",
            "pdf" when parts.Length == 3 => $"book {parts[1]} pdf v{parts[2]}",
            "" => "(empty)",
            _ => $"\"{content}\""
        };
    }

    /// <summary>The book a manifest key is for: "b000000003", "file:src-3.m4b", or either followed by " left in ...".</summary>
    private static int? KeyOwner(string key)
    {
        var id = key.Split(" left in ", 2)[0];
        var number = id.StartsWith("file:src-", StringComparison.Ordinal)
            ? id["file:src-".Length..].Split('.')[0]
            : id.StartsWith('b') ? id[1..] : null;
        return int.TryParse(number, out var value) ? value : null;
    }

    // ----- The layout the version on main left ---------------------------------------------------

    /// <summary>
    /// Sorts <paramref name="books"/> into an empty <paramref name="destination"/> as the version on main
    /// did: standalone books loose in the author folder, series books without a number loose in the
    /// series folder, numbered ones in "Series/Book N", and a second file of one name in a folder as
    /// "Title (2)", told apart per file type. Author and series spellings that mean the same share the
    /// first one's folder. A book whose audio is missing still had its PDF copied.
    /// </summary>
    private static void WriteMainLayout(string destination, IReadOnlyList<BookState> books)
    {
        var folderNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var book in books)
        {
            var plan = BookNaming.BuildPlan(ToRow(book));
            var hasAudio = book.Audio == SourceState.Present;
            if (!hasAudio && book.Pdf is null)
            {
                continue;
            }

            var folder = Path.Combine(destination, FolderName(destination, plan.Author, PathSanitizer.NormalizeComparisonKey));
            if (plan.SeriesName is not null)
            {
                folder = Path.Combine(folder, FolderName(folder, plan.SeriesName, PathSanitizer.NormalizeSeriesKey));
                if (plan.SeriesSequence is not null)
                {
                    folder = Path.Combine(folder, $"Book {plan.SeriesSequence}");
                }
            }

            if (hasAudio && Claim(folder, plan.FileStem, book.Extension, $"src-{book.Id}{book.Extension}") is { } audio)
            {
                Write(audio, AudioContent(book));
            }

            if (book.Pdf is { } pdfVersion && Claim(folder, plan.FileStem, ".pdf", $"src-{book.Id}.pdf") is { } pdf)
            {
                Write(pdf, PdfContent(book.Id, pdfVersion));
            }
        }

        string FolderName(string parent, string name, Func<string?, string> normalise)
        {
            var key = normalise(name);
            if (key.Length == 0)
            {
                return name;
            }

            var cacheKey = $"{parent}\u0000{key}";
            if (!folderNames.TryGetValue(cacheKey, out var resolved))
            {
                folderNames[cacheKey] = resolved = name;
            }

            return resolved;
        }

        string? Claim(string folder, string stem, string extension, string source)
        {
            if (!fileIndex.TryGetValue(folder, out var index))
            {
                fileIndex[folder] = index = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var name = index.GetValueOrDefault(IndexKey(stem, extension)) ?? stem + extension;
            for (var attempt = 1; attempt <= 100; attempt++)
            {
                var candidate = Path.Combine(folder, attempt == 1 ? name : $"{stem} ({attempt}){extension}");
                if (claimedBy.TryAdd(candidate, source))
                {
                    index.TryAdd(IndexKey(Path.GetFileNameWithoutExtension(candidate), extension), Path.GetFileName(candidate));
                    return candidate;
                }

                if (string.Equals(claimedBy[candidate], source, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return null;
        }

        static string IndexKey(string stem, string extension) => $"{PathSanitizer.NormalizeComparisonKey(stem)}\u0000{extension.ToLowerInvariant()}";
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ----- Running a scenario --------------------------------------------------------------------

    private enum ViolationKind
    {
        /// <summary>A file's content is gone from the destination, and it was not its own book's copy being updated.</summary>
        Lost,

        /// <summary>A file was moved into a folder that is not its own book's.</summary>
        Misfiled,

        /// <summary>Audio lies loose beside book folders and the problems do not say so.</summary>
        Unnamed,

        /// <summary>The sort threw something other than the cancellation asked for.</summary>
        Threw
    }

    /// <param name="Cause">
    /// What had already gone wrong before the run, when something had: another book's file on record
    /// for a book (see <see cref="Misrecorded"/>), or a loose file that was already loose. Failures are
    /// grouped and minimised by it, so one root cause does not hide another.
    /// </param>
    private sealed record Violation(ViolationKind Kind, int Run, string Message, string? Cause, string Detail)
    {
        public string Group => Cause is null
            ? Kind.ToString()
            : Kind == ViolationKind.Unnamed ? $"{Kind}, already loose before the run" : $"{Kind}, after the manifest recorded another book's file";
    }

    /// <summary>What the destination and the record were before and after one run, and what the run was given.</summary>
    private sealed record RunFacts(
        string Destination,
        Dictionary<string, string> Before,
        Dictionary<string, string> After,
        LibraryManifest RecordBefore,
        Dictionary<string, HashSet<int>> FolderOwnersAfter,
        Dictionary<int, (string? Audio, string? Pdf)> Downloads,
        SortSummary? Summary)
    {
        /// <summary>
        /// The books a content may be the own copy of: only the book it was downloaded for, whatever it
        /// lay beside or was moved along with. Empty for a file of the person's own.
        /// </summary>
        public IReadOnlySet<int> OwnersOf(string content) =>
            ContentOwner(content) is { } owner ? new HashSet<int> { owner.Id } : new HashSet<int>();
    }

    /// <summary>Runs every sort of <paramref name="scenario"/> in a fresh workspace; the first violation, or null.</summary>
    private static async Task<Violation?> RunAsync(Scenario scenario, Stats? stats)
    {
        using var workspace = new TempWorkspace();
        var destination = workspace.Destination;
        WriteMainLayout(destination, scenario.OldLayout);

        for (var index = 0; index < scenario.Runs.Count; index++)
        {
            var run = scenario.Runs[index];
            var downloads = PrepareSource(workspace.Source, run.Rows);
            PrepareRecord(destination, run);
            if (run.UserFile is { } slot)
            {
                AddUserFile(destination, slot, index);
            }

            var before = Snapshot(destination);
            var recordBefore = LibraryManifest.Load(destination);

            using var cancellation = new CancellationTokenSource();
            SortSummary? summary = null;
            Exception? failure = null;
            try
            {
                summary = await new FileSorter().SortAudioFiles(
                    workspace.Source,
                    destination,
                    run.Rows.Select(ToRow).ToList(),
                    new SortOptions { MaxParallelism = 1 },
                    new CancelAtReport(run.CancelAtReport, cancellation),
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Asked for: the checks below hold for a cancelled run too.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var facts = new RunFacts(destination, before, Snapshot(destination), recordBefore, FolderOwners(destination), downloads, summary);
            stats?.Add(facts);

            var violation = failure is not null
                ? (ViolationKind.Threw, failure.ToString(), null)
                : Check(facts);
            if (violation is var (kind, message, cause))
            {
                return new Violation(kind, index, message, cause, Detail(facts));
            }
        }

        return null;
    }

    /// <summary>Fills the source folder with the files <paramref name="rows"/> say are there; returns each book's downloads.</summary>
    private static Dictionary<int, (string? Audio, string? Pdf)> PrepareSource(string source, IReadOnlyList<BookState> rows)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            File.Delete(file);
        }

        var downloads = new Dictionary<int, (string? Audio, string? Pdf)>();
        foreach (var book in rows.DistinctBy(book => book.Id))
        {
            var audioPath = Path.Combine(source, $"src-{book.Id}{book.Extension}");
            string? audio = null;
            switch (book.Audio)
            {
                case SourceState.Present:
                    audio = AudioContent(book);
                    File.WriteAllText(audioPath, audio);
                    break;
                case SourceState.Empty:
                    File.WriteAllText(audioPath, string.Empty);
                    break;
            }

            string? pdf = null;
            if (book.Pdf is { } version)
            {
                File.WriteAllText(Path.Combine(source, $"src-{book.Id}.pdf"), PdfContent(book.Id, version));

                // A book whose audio is not there is not sorted, so neither is its PDF.
                pdf = audio is null ? null : PdfContent(book.Id, version);
            }

            downloads[book.Id] = (audio, pdf);
        }

        return downloads;
    }

    private static void PrepareRecord(string destination, RunSpec run)
    {
        var manifest = Path.Combine(destination, LibraryManifest.FileName);
        if (run.DeleteManifest)
        {
            File.Delete(manifest);
        }

        if (run.DamageManifest && File.Exists(manifest))
        {
            // The sort keeps the damaged file aside under a name with the time in it, which must be free.
            foreach (var kept in Directory.GetFiles(destination, LibraryManifest.FileName + ".unreadable-*"))
            {
                File.Delete(kept);
            }

            File.WriteAllText(manifest, "{ \"format\": \"openaudible-organizer-manifest\", \"books\": ");
        }
    }

    private static void AddUserFile(string destination, int slot, int run)
    {
        var folders = Directory.GetDirectories(destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Prepend(destination).ToList();
        File.WriteAllText(Path.Combine(folders[slot % folders.Count], $"notes-{run}.txt"), $"user|notes|{run}");
    }

    /// <summary>Every file in the destination, by path relative to it with '/', and its content. Not the sort's own record.</summary>
    private static Dictionary<string, string> Snapshot(string destination)
    {
        return Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Relative(destination, path), Path: path))
            .Where(file => !file.Relative.StartsWith(LibraryManifest.FileName, StringComparison.Ordinal))
            .ToDictionary(file => file.Relative, file => File.ReadAllText(file.Path), PathComparer);
    }

    /// <summary>The books the manifest file records for each folder (relative, with '/'), read as it is on disk.</summary>
    private static Dictionary<string, HashSet<int>> FolderOwners(string destination)
    {
        var owners = new Dictionary<string, HashSet<int>>(PathComparer);
        var path = Path.Combine(destination, LibraryManifest.FileName);
        if (!File.Exists(path))
        {
            return owners;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("books", out var books) || books.ValueKind != JsonValueKind.Object)
            {
                return owners;
            }

            foreach (var book in books.EnumerateObject())
            {
                if (KeyOwner(book.Name) is { } id &&
                    book.Value.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String)
                {
                    var key = folder.GetString()!;
                    if (!owners.TryGetValue(key, out var set))
                    {
                        owners[key] = set = [];
                    }

                    set.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Damaged and not saved again: it records no folder for anyone.
        }

        return owners;
    }

    private static string Relative(string destination, string path) =>
        Path.GetRelativePath(destination, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Parent(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? string.Empty : relative[..slash];
    }

    private static bool IsAudio(string path) =>
        SourceFileLocator.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + "/", PathSanitizer.PathComparison);

    /// <summary>Cancels the run at the given progress report (counting from 1).</summary>
    private sealed class CancelAtReport(int? report, CancellationTokenSource cancellation) : IProgress<SortProgressInfo>
    {
        private int _reports;

        public void Report(SortProgressInfo value)
        {
            if (++_reports == report)
            {
                cancellation.Cancel();
            }
        }
    }

    // ----- The invariant -------------------------------------------------------------------------

    private static (ViolationKind Kind, string Message, string? Cause)? Check(RunFacts facts)
    {
        return Lost(facts) is { } lost ? (ViolationKind.Lost, lost, Misrecorded(facts))
            : Misfiled(facts) is { } misfiled ? (ViolationKind.Misfiled, misfiled, Misrecorded(facts))
            : Unnamed(facts) is { } unnamed ? (ViolationKind.Unnamed, unnamed.Message, unnamed.WasLoose ? "already loose" : null)
            : null;
    }

    /// <summary>
    /// A file the manifest the run started with records for one book that holds another book's audio or
    /// PDF, or null. Not itself a lost file, but what makes the sort treat it as the wrong book's own.
    /// </summary>
    private static string? Misrecorded(RunFacts facts)
    {
        foreach (var (key, entry) in facts.RecordBefore.Books.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var name in entry.Files)
            {
                var path = Relative(facts.Destination, Path.Combine(entry.Folder, name));
                if (facts.Before.TryGetValue(path, out var content) && ContentOwner(content) is not null &&
                    !(KeyOwner(key) is { } recordedFor && facts.OwnersOf(content).Contains(recordedFor)))
                {
                    return $"Before the run the manifest already recorded \"{path}\" ({Label(content)}) for book {KeyOwner(key)}.";
                }
            }
        }

        return null;
    }

    /// <summary>A content the destination held before the run that is nowhere in it now, and was not a book's own recorded copy its new download replaced.</summary>
    private static string? Lost(RunFacts facts)
    {
        var remaining = facts.After.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var (path, content) in facts.Before.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (!remaining.Contains(content) && !IsOwnCopyBeingUpdated(facts, path, content))
            {
                return $"\"{path}\" ({Label(content)}) is gone, and its content is nowhere in the destination.";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> held its own book's copy, recorded for that book in the manifest
    /// the run started with, and the book's download in this run is a different one of the same kind.
    /// </summary>
    private static bool IsOwnCopyBeingUpdated(RunFacts facts, string path, string content)
    {
        if (ContentOwner(content) is not { } kind)
        {
            return false;
        }

        return facts.OwnersOf(content).Any(id =>
            facts.Downloads.TryGetValue(id, out var download) &&
            (kind.IsAudio ? download.Audio : download.Pdf) is { } current &&
            current != content &&
            ContentOwner(current)!.Extension == kind.Extension &&
            facts.RecordBefore.Books.Any(pair =>
                KeyOwner(pair.Key) == id &&
                pair.Value.Files.Any(name => PathComparer.Equals(Relative(facts.Destination, Path.Combine(pair.Value.Folder, name)), path))));
    }

    /// <summary>
    /// A file that moved to a folder the manifest records for another book (or for none): allowed only
    /// into its own book's folder. A PDF that lay beside a book's audio is no exception: older versions
    /// numbered each type of file on its own, so "Title.pdf" beside one book's "Title.m4b" may be the only
    /// copy of a same-titled book that had nothing but its PDF then.
    /// </summary>
    private static string? Misfiled(RunFacts facts)
    {
        foreach (var group in facts.Before.GroupBy(file => file.Value, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var content = group.Key;
            var left = group.Select(file => file.Key).Where(path => !(facts.After.TryGetValue(path, out var now) && now == content)).ToList();
            if (left.Count == 0)
            {
                continue;
            }

            var arrived = facts.After
                .Where(file => file.Value == content && !(facts.Before.TryGetValue(file.Key, out var was) && was == content))
                .Select(file => file.Key)
                .Order(StringComparer.Ordinal);
            foreach (var destination in arrived)
            {
                if (!MayArrive(facts, content, destination))
                {
                    var owners = facts.FolderOwnersAfter.GetValueOrDefault(Parent(destination));
                    return $"\"{left[0]}\" ({Label(content)}) was moved to \"{destination}\", whose folder the manifest records for " +
                           (owners is null ? "no book" : $"book {string.Join(", book ", owners.Order())}") + ".";
                }
            }
        }

        return null;
    }

    private static bool MayArrive(RunFacts facts, string content, string destination)
    {
        var owners = facts.FolderOwnersAfter.GetValueOrDefault(Parent(destination)) ?? [];
        return owners.Overlaps(facts.OwnersOf(content));
    }

    /// <summary>
    /// After a run that finished: audio directly in a folder that also holds book folders, which library
    /// tools then read as one book, that the problems do not name. Not when the run left that folder and
    /// everything in it exactly as it was: the sort only looks where it files books.
    /// </summary>
    private static (string Message, bool WasLoose)? Unnamed(RunFacts facts)
    {
        if (facts.Summary is null)
        {
            return null;
        }

        var folders = facts.After.Keys.Select(Parent).Where(folder => folder.Length > 0).Distinct(PathComparer).Order(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!HoldsBookFolders(facts.After, folder))
            {
                continue;
            }

            foreach (var file in facts.After.Keys.Where(path => IsAudio(path) && PathComparer.Equals(Parent(path), folder)).Order(StringComparer.Ordinal))
            {
                var named = facts.Summary.Problems.Any(problem =>
                    problem.Message.Contains(file.Replace('/', Path.DirectorySeparatorChar), PathSanitizer.PathComparison));
                var wasLoose = facts.Before.ContainsKey(file) && HoldsBookFolders(facts.Before, folder);
                if (!named && !(wasLoose && Unchanged(facts, folder)))
                {
                    return ($"\"{file}\" ({Label(facts.After[file])}) lies loose beside book folders, and no problem names it.", wasLoose);
                }
            }
        }

        return null;
    }

    /// <summary>Whether a folder below <paramref name="folder"/> holds audio: a book's folder, or a series of them.</summary>
    private static bool HoldsBookFolders(Dictionary<string, string> files, string folder) =>
        files.Keys.Any(path => IsAudio(path) && IsInside(Parent(path), folder));

    private static bool Unchanged(RunFacts facts, string folder)
    {
        var before = facts.Before.Where(file => IsInside(file.Key, folder)).ToList();
        var after = facts.After.Where(file => IsInside(file.Key, folder)).ToList();
        return before.Count == after.Count &&
               before.All(file => facts.After.TryGetValue(file.Key, out var now) && now == file.Value);
    }

    // ----- Minimising and reporting --------------------------------------------------------------

    /// <summary>Shrinks a failing scenario, one simplification at a time, while it still fails the same way.</summary>
    private static async Task<(Scenario, Violation)> MinimiseAsync(Scenario scenario, Violation violation)
    {
        var best = (Scenario: scenario, Violation: violation);
        while (true)
        {
            var candidates = Shrinks(best.Scenario).ToList();
            var results = new Violation?[candidates.Count];
            await Parallel.ForAsync(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                async (i, _) => results[i] = await RunAsync(candidates[i], stats: null));

            var first = Enumerable.Range(0, candidates.Count).FirstOrDefault(i => results[i]?.Group == violation.Group, -1);
            if (first < 0)
            {
                return best;
            }

            best = (candidates[first], results[first]!);
        }
    }

    /// <summary>Every scenario one step simpler than <paramref name="scenario"/>, the biggest steps first.</summary>
    private static IEnumerable<Scenario> Shrinks(Scenario scenario)
    {
        var runs = scenario.Runs;
        for (var r = runs.Count - 1; r >= 0 && runs.Count > 1; r--)
        {
            yield return scenario with { Runs = Without(runs, r) };
        }

        if (scenario.OldLayout.Count > 0)
        {
            yield return scenario with { OldLayout = [] };
        }

        foreach (var id in scenario.OldLayout.Concat(runs.SelectMany(run => run.Rows)).Select(book => book.Id).Distinct().Order())
        {
            yield return scenario with
            {
                OldLayout = scenario.OldLayout.Where(book => book.Id != id).ToList(),
                Runs = runs.Select(run => run with { Rows = run.Rows.Where(book => book.Id != id).ToList() }).ToList()
            };
        }

        for (var i = 0; i < scenario.OldLayout.Count; i++)
        {
            yield return scenario with { OldLayout = Without(scenario.OldLayout, i) };
        }

        for (var r = 0; r < runs.Count; r++)
        {
            for (var i = 0; i < runs[r].Rows.Count; i++)
            {
                yield return WithRun(scenario, r, runs[r] with { Rows = Without(runs[r].Rows, i) });
            }
        }

        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.DeleteManifest)
            {
                yield return WithRun(scenario, r, run with { DeleteManifest = false });
            }

            if (run.DamageManifest)
            {
                yield return WithRun(scenario, r, run with { DamageManifest = false });
            }

            if (run.CancelAtReport is { } report)
            {
                yield return WithRun(scenario, r, run with { CancelAtReport = null });
                if (report > 1)
                {
                    yield return WithRun(scenario, r, run with { CancelAtReport = report - 1 });
                }
            }

            if (run.UserFile is not null)
            {
                yield return WithRun(scenario, r, run with { UserFile = null });
            }
        }

        // One simplification of one book everywhere it appears, then in one place only.
        var ids = scenario.OldLayout.Concat(runs.SelectMany(run => run.Rows)).Select(book => book.Id).Distinct().Order().ToList();
        foreach (var simplify in Simplifications)
        {
            foreach (var id in ids)
            {
                var candidate = MapBooks(scenario, book => book.Id == id ? simplify(book) : book);
                if (candidate != scenario)
                {
                    yield return candidate;
                }
            }
        }

        foreach (var simplify in Simplifications)
        {
            for (var i = 0; i < scenario.OldLayout.Count; i++)
            {
                var book = scenario.OldLayout[i];
                if (simplify(book) is var simpler && simpler != book)
                {
                    yield return scenario with { OldLayout = Replace(scenario.OldLayout, i, simpler) };
                }
            }

            for (var r = 0; r < runs.Count; r++)
            {
                for (var i = 0; i < runs[r].Rows.Count; i++)
                {
                    var book = runs[r].Rows[i];
                    if (simplify(book) is var simpler && simpler != book)
                    {
                        yield return WithRun(scenario, r, runs[r] with { Rows = Replace(runs[r].Rows, i, simpler) });
                    }
                }
            }
        }
    }

    private static readonly Func<BookState, BookState>[] Simplifications =
    [
        book => book with { Pdf = null },
        book => book with { Series = null, Sequence = null },
        book => book with { Sequence = null },
        book => book with { Asin = false },
        book => book with { Audio = SourceState.Present },
        book => book with { Extension = ".m4b" },
        book => book with { AudioVersion = Math.Max(0, book.AudioVersion - 1) },
        book => book with { Pdf = book.Pdf is { } pdf ? Math.Max(0, pdf - 1) : null },
        book => book with { Author = Authors[0] },
        book => book with { Series = book.Series is null ? null : SeriesNames[0] },
        book => book with { Title = Titles[0] }
    ];

    private static Scenario MapBooks(Scenario scenario, Func<BookState, BookState> map)
    {
        var oldLayout = scenario.OldLayout.Select(map).ToList();
        var runs = scenario.Runs.Select(run => run with { Rows = run.Rows.Select(map).ToList() }).ToList();
        var same = oldLayout.SequenceEqual(scenario.OldLayout) &&
                   runs.Zip(scenario.Runs).All(pair => pair.First.Rows.SequenceEqual(pair.Second.Rows));
        return same ? scenario : scenario with { OldLayout = oldLayout, Runs = runs };
    }

    private static Scenario WithRun(Scenario scenario, int index, RunSpec run) =>
        scenario with { Runs = Replace(scenario.Runs, index, run) };

    private static List<T> Without<T>(IReadOnlyList<T> list, int index) => list.Where((_, i) => i != index).ToList();

    private static List<T> Replace<T>(IReadOnlyList<T> list, int index, T value) => list.Select((item, i) => i == index ? value : item).ToList();

    private static string Describe(Scenario scenario, Violation violation)
    {
        var text = new StringBuilder();
        text.AppendLine($"=== {violation.Group}: run {violation.Run + 1} of seed {scenario.Seed}, minimised ===");
        text.AppendLine(violation.Message);
        if (violation.Cause is { } cause && violation.Kind != ViolationKind.Unnamed)
        {
            text.AppendLine(cause);
        }

        if (scenario.OldLayout.Count == 0)
        {
            text.AppendLine("The destination starts empty.");
        }
        else
        {
            text.AppendLine("The destination starts as the version on main left it, after sorting:");
            foreach (var book in scenario.OldLayout)
            {
                text.AppendLine($"    {DescribeBook(book)}");
            }
        }

        for (var r = 0; r < scenario.Runs.Count; r++)
        {
            var run = scenario.Runs[r];
            var notes = new List<string>();
            if (run.DeleteManifest)
            {
                notes.Add("manifest deleted first");
            }

            if (run.DamageManifest)
            {
                notes.Add("manifest damaged first");
            }

            if (run.UserFile is { } slot)
            {
                notes.Add($"a notes file added to destination folder #{slot}");
            }

            if (run.CancelAtReport is { } report)
            {
                notes.Add($"cancelled at progress report {report}");
            }

            text.AppendLine($"Run {r + 1}{(notes.Count > 0 ? $" ({string.Join("; ", notes)})" : string.Empty)}, export:");
            foreach (var book in run.Rows)
            {
                text.AppendLine($"    {DescribeBook(book)}");
            }

            if (run.Rows.Count == 0)
            {
                text.AppendLine("    (no books)");
            }
        }

        text.Append(violation.Detail);
        return text.ToString();
    }

    private static string DescribeBook(BookState book)
    {
        var series = book.Series is null ? string.Empty : $", series \"{book.Series}\"{(book.Sequence is null ? string.Empty : $" #{book.Sequence}")}";
        var audio = book.Audio switch
        {
            SourceState.Missing => "audio missing from the source",
            SourceState.Empty => "audio 0 bytes in the source",
            _ => $"audio v{book.AudioVersion}"
        };
        return $"book {book.Id}: \"{book.Title}\" by {book.Author}{series}{(book.Asin ? ", ASIN" : ", no ASIN")}, " +
               $"{audio} {book.Extension}{(book.Pdf is { } pdf ? $", pdf v{pdf}" : string.Empty)}";
    }

    private static string Detail(RunFacts facts)
    {
        var text = new StringBuilder();
        text.AppendLine("Destination before the run:");
        AppendFiles(text, facts.Before);
        text.AppendLine("Manifest before the run:");
        foreach (var (key, entry) in facts.RecordBefore.Books.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"    {key} -> {Relative(facts.Destination, entry.Folder)} [{string.Join(", ", entry.Files)}]");
        }

        text.AppendLine("Destination after the run:");
        AppendFiles(text, facts.After);
        text.AppendLine(facts.Summary is null ? "The run was cancelled." : $"Counts: {facts.Summary.Counts}");
        foreach (var problem in facts.Summary?.Problems ?? [])
        {
            text.AppendLine($"    {problem.Kind} {problem.Book}: {problem.Message}");
        }

        return text.ToString();
    }

    private static void AppendFiles(StringBuilder text, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"    {path}  = {Label(content)}");
        }

        if (files.Count == 0)
        {
            text.AppendLine("    (nothing)");
        }
    }

    private static int? EnvironmentNumber(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    /// <summary>What the scenarios covered, so a change that stops them reaching a case shows.</summary>
    private sealed class Stats
    {
        private long _runs;
        private long _cancelled;
        private long _new;
        private long _updated;
        private long _moved;
        private long _upToDate;
        private long _notFound;
        private long _failed;
        private long _ownCopiesUpdated;
        private long _filesMoved;

        public void Add(RunFacts facts)
        {
            Interlocked.Increment(ref _runs);
            if (facts.Summary is not { } summary)
            {
                Interlocked.Increment(ref _cancelled);
            }
            else
            {
                Interlocked.Add(ref _new, summary.Counts.New);
                Interlocked.Add(ref _updated, summary.Counts.Updated);
                Interlocked.Add(ref _moved, summary.Counts.Moved);
                Interlocked.Add(ref _upToDate, summary.Counts.UpToDate);
                Interlocked.Add(ref _notFound, summary.Counts.NotFound);
                Interlocked.Add(ref _failed, summary.Counts.Failed);
            }

            var remaining = facts.After.Values.ToHashSet(StringComparer.Ordinal);
            Interlocked.Add(ref _ownCopiesUpdated, facts.Before.Count(file => !remaining.Contains(file.Value)));
            Interlocked.Add(ref _filesMoved, facts.Before.Count(file =>
                !facts.After.ContainsKey(file.Key) &&
                facts.After.Any(now => now.Value == file.Value && !facts.Before.ContainsKey(now.Key))));
        }

        public override string ToString() =>
            $"{_runs} runs ({_cancelled} cancelled); books new {_new}, updated {_updated}, moved {_moved}, up to date {_upToDate}, " +
            $"not found {_notFound}, failed {_failed}; own copies replaced by a new download {_ownCopiesUpdated}, files moved {_filesMoved}";
    }
}
