using System.Text.RegularExpressions;
using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Works out, up front, exactly which file is copied where, and which file already in the
/// destination is moved where.
///
/// Planning is deliberately single threaded and separate from copying. Deciding folder names
/// while dozens of workers race to create them is how a library ends up with both
/// "J.K. Rowling" and "JK Rowling", or with two books quietly overwriting each other; doing it
/// once, in order, makes the outcome of a sort deterministic and repeatable. Folders are only
/// named here, never created, so a book whose files turn out to be unreadable leaves none behind.
///
/// Every book gets a folder of its own: Audiobookshelf, Plex and friends treat a folder as one book,
/// and an audio file loose in an author or series folder makes them read that whole folder as one
/// book, hiding every series under it. Whose a folder is comes from the <see cref="LibraryManifest"/>,
/// not from names; names only decide for books it does not know: new books, and every book the first
/// time a library laid out by an older version is sorted. The passes, in order:
/// <list type="number">
/// <item>series folders are reserved, so no book is ever filed loose among a series' book folders;</item>
/// <item>
/// each book gets its folder (see <see cref="ClaimFolder"/>): first the books on record, which keep
/// their folders, or move when their author, series, number or title changed; then the new books,
/// in export order, each claiming the first free "Title", "Title (2)"...;
/// </item>
/// <item>
/// files an older version left where library tools trip over them move to the book they belong to,
/// and every audio or PDF file still loose beside book folders is named in the problems.
/// </item>
/// </list>
/// </summary>
public sealed class SortPlanner
{
    private const int MaxDisambiguationAttempts = 100;

    /// <summary>What a book's id is when it is its audio file's name (see <see cref="PlannedCopy.BookId"/>).</summary>
    private const string FileIdPrefix = "file:";

    private const string EmptySourceMessage =
        "The book's file in the source folder is empty, as it is while OpenAudible is still downloading or " +
        "converting it. It is copied once it is complete.";

    /// <summary>The " (2)", " (3)"... that <see cref="Suffix"/> adds. " (1)" is none: "Heroes (1)" is a title.</summary>
    private static readonly Regex SuffixPattern = new(@"^(?<name>.+) \((?:[2-9]|[1-9][0-9]+)\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// As the file system compares paths: on Linux "It.m4b" and "IT.m4b" are two books, not one listed twice,
    /// and "Author/foo" is not the folder "Author/Foo".
    /// </summary>
    private static readonly StringComparer PathComparer = StringComparer.FromComparison(PathSanitizer.PathComparison);

    /// <summary>As <see cref="DestinationListing"/> compares folders.</summary>
    private static readonly StringComparer FolderComparer = StringComparer.OrdinalIgnoreCase;

    private readonly DestinationListing _disk = new();
    private readonly Dictionary<string, string> _resolvedDirectoryNames = new(StringComparer.Ordinal);

    /// <summary>The folders given out in this run, to series and to books.</summary>
    private readonly HashSet<string> _claimedFolders = new(FolderComparer);

    /// <summary>The folders on record (see <see cref="LibraryManifest"/>), and whose each is.</summary>
    private Dictionary<string, string> _folderOwners = new(FolderComparer);

    /// <summary>
    /// The folders on record for books this run does not sort (gone from the export or from the
    /// source, or recorded by a file name another book now has; see <see cref="MayBeRecordedAs"/>),
    /// which therefore stay where they are, in the way of anything else.
    /// </summary>
    private readonly HashSet<string> _heldFolders = new(FolderComparer);

    private readonly List<string> _warnings = [];

    private string _root = string.Empty;

    /// <summary>What the last <see cref="Plan"/> found wrong with the destination as a whole rather than with one book.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Builds the copy plan for every book in <paramref name="books"/>.</summary>
    /// <param name="manifest">What earlier sorts recorded about this destination (see <see cref="LibraryManifest.Load"/>). Not changed.</param>
    /// <param name="cancellationToken">
    /// Checked once per book in every pass: planning a large library on a slow network drive can
    /// take minutes, and Cancel has to work during that time too.
    /// </param>
    public List<PlannedCopy> Plan(
        IReadOnlyList<OpenAudible> books,
        string sourceRoot,
        string destinationRoot,
        LibraryManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        _root = Path.GetFullPath(destinationRoot);
        _folderOwners = manifest.Books
            .DistinctBy(pair => pair.Value.Folder, FolderComparer)
            .ToDictionary(pair => pair.Value.Folder, pair => pair.Key, FolderComparer);
        var rows = ResolveBooks(books, sourceRoot, manifest, cancellationToken);
        var unique = rows.Where(book => !book.IsRepeat).ToList();
        var inSource = unique.Where(book => book.AudioSource is not null).ToList();

        // Reserved first, so a standalone book titled like a series ("Bobiverse") gets a
        // "Bobiverse (2)" folder of its own instead of lying loose in the series, wherever it is listed.
        _claimedFolders.UnionWith(unique.Where(book => book.Naming.SeriesName is not null).Select(book => book.Parent));

        // The books on record first, whatever the export's order: their files are provably theirs, so
        // a new book listed before one that moved must not take the folder it moves to.
        foreach (var book in inSource.OrderBy(book => book.Record is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimFolder(book);
        }

        inSource.Where(book => book.Folder is not null).ToList().ForEach(PlanFiles);

        // Only now is every destination known, so an old file is never taken from where another
        // book is about to be written.
        var offered = AdoptOldFiles(unique, manifest, cancellationToken);
        WarnAboutLooseFiles(unique, manifest, offered);

        return rows.Select(ToPlannedCopy).ToList();
    }

    /// <summary>One row of the export as the planner sees it, filled in pass by pass.</summary>
    private sealed class Book(OpenAudible row, BookSortPlan naming, string title)
    {
        public OpenAudible Row { get; } = row;
        public BookSortPlan Naming { get; } = naming;
        public string Title { get; } = title;
        public string StemKey { get; } = NameKey(naming.FileStem);
        public string? Id { get; set; }

        /// <summary>
        /// Every id the book is listed under: each of its rows' ASIN, Key, ProductID and audio file name
        /// (see <see cref="BookIdsFor"/>), the rows repeating it included.
        /// </summary>
        public List<string> Ids { get; init; } = [];

        public string? AudioSource { get; init; }
        public bool IsRepeat { get; set; }
        public string? PdfSource { get; set; }

        /// <summary>The author folder, or the series folder in it; <see cref="BaseName"/> is the book's folder in it before any "(2)".</summary>
        public string Parent { get; set; } = string.Empty;

        public string BaseName { get; set; } = string.Empty;

        /// <summary>Where the manifest says the book is; null when it does not know the book.</summary>
        public ManifestEntry? Record { get; set; }

        public string? Folder { get; set; }
        public string? AudioDestination { get; set; }
        public string? AudioMoveFrom { get; set; }
        public string? PdfDestination { get; set; }
        public string? PdfMoveFrom { get; set; }
        public List<(string From, string To)> OtherMoves { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    /// <summary>Works out every row's identity, files and wanted folder, and which rows repeat a book listed before.</summary>
    private List<Book> ResolveBooks(IReadOnlyList<OpenAudible> rows, string sourceRoot, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        var books = new List<Book>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var naming = BookNaming.BuildPlan(row);
            var audioSource = SourceFileLocator.FindAudioFile(row, sourceRoot);
            var ids = BookIdsFor(row, audioSource);
            books.Add(new Book(row, naming, BuildTitle(row, naming)) { Id = ids.FirstOrDefault(), Ids = ids, AudioSource = audioSource });
        }

        MarkRepeats(books);
        var recordsTaken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books.Where(book => !book.IsRepeat))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Under whichever of its ids it was recorded: which row of a book listed twice comes first,
            // and which ids a row has (an ASIN filled in since), can change from one export to the next.
            var recordedId = book.Ids.FirstOrDefault(id => manifest.Get(id) is { } entry && !recordsTaken.Contains(id) && MayBeRecordedAs(book, id, entry));
            if (recordedId is not null)
            {
                recordsTaken.Add(recordedId);
                book.Id = recordedId;
                book.Record = manifest.Get(recordedId);
            }
        }

        // Before any folder is named: a series must not be filed in the folder of a book that stays.
        var sortedIds = books.Where(book => !book.IsRepeat && book.AudioSource is not null && book.Record is not null).Select(book => book.Id!).ToHashSet(StringComparer.Ordinal);
        _heldFolders.UnionWith(_folderOwners.Where(pair => !sortedIds.Contains(pair.Value)).Select(pair => pair.Key));

        foreach (var book in books.Where(book => !book.IsRepeat))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (book.AudioSource is null && SourceFileLocator.HasEmptyAudioFile(book.Row, sourceRoot))
            {
                book.Warnings.Add(EmptySourceMessage);
            }

            book.PdfSource = book.AudioSource is null ? null : SourceFileLocator.FindPdfFile(book.Row, sourceRoot);
            book.Parent = ResolveParentDirectory(book.Naming);
            book.BaseName = book.Naming.SeriesSequence is null
                ? ResolveDirectoryName(book.Parent, book.Naming.FileStem, isSeries: false)
                : $"Book {book.Naming.SeriesSequence}";
        }

        return books;
    }

    /// <summary>
    /// Whether the record under <paramref name="id"/>, one of the book's ids, may be the book's. An ASIN, Key
    /// or ProductID is the book's own, and so is the file name it is known by when it has none of those. But
    /// another book can be downloaded under the file name a book that has left the export was recorded
    /// under: a record found by a file name the book is not known by is only the book's when it holds the
    /// book's own audio, as it does for a book whose ASIN was filled in since. Otherwise the book is a new
    /// one, and the folder, maybe the other book's only copy, stays that book's.
    /// </summary>
    private static bool MayBeRecordedAs(Book book, string id, ManifestEntry entry)
    {
        return !id.StartsWith(FileIdPrefix, StringComparison.Ordinal) ||
               id == book.Ids[0] ||
               (book.AudioSource is not null && entry.Files.Any(name => FileComparison.AreSameQuick(book.AudioSource, Path.Combine(entry.Folder, name))));
    }

    /// <summary>
    /// A book listed again (in two accounts or regions, say) is the same book whatever title the
    /// other listing gives it: planning it again would give it a second folder and a second copy.
    /// Of the rows listing one book, the first with its audio in the source is the book, and one
    /// without only when no row has it, so a book is never reported both sorted and missing. Only a
    /// row with neither an id nor a file is a book of its own whatever else is listed.
    /// </summary>
    private static void MarkRepeats(List<Book> books)
    {
        var byId = new Dictionary<string, Book>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, Book>(PathComparer);

        // The rows with the file first, so a row without it repeats one with it wherever it is listed.
        var withFile = books.Where(book => book.AudioSource is not null);
        foreach (var book in withFile.Concat(books.Where(book => book.AudioSource is null && book.Id is not null)))
        {
            var first = (book.AudioSource is null ? null : bySource.GetValueOrDefault(book.AudioSource)) ?? byId.GetValueOrDefault(book.Id!) ?? book;
            if (book.AudioSource is not null)
            {
                bySource.TryAdd(book.AudioSource, first);
            }

            byId.TryAdd(book.Id!, first);
            if (first != book)
            {
                book.IsRepeat = true;
                first.Ids.AddRange(book.Ids);
            }
        }
    }

    /// <summary>
    /// Every id of a row, the one it is known by (see <see cref="PlannedCopy.BookId"/>) first. The file
    /// name keeps its case: on a case-sensitive disk "It.m4b" and "IT.m4b" are two books, and an export
    /// spells a file the same on every platform.
    /// </summary>
    private static List<string> BookIdsFor(OpenAudible row, string? audioSource)
    {
        var ids = new[] { row.ASIN, row.Key, row.ProductID }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .ToList();
        if (audioSource is not null)
        {
            ids.Add(FileIdPrefix + Path.GetFileName(audioSource));
        }

        return ids;
    }

    /// <summary>
    /// Gives a book one of "Name", "Name (2)"... that no book has in this run and the manifest records
    /// for no other book, so a folder on record is never another book's while its files are there.
    /// <list type="number">
    /// <item>
    /// Pass B: the folder the manifest records for the book, while it still belongs there (same author
    /// or series, same title or number, no series now in it). Kept even when it is a "(2)", so
    /// same-titled books never swap folders. Otherwise the book moves, with its recorded files.
    /// </item>
    /// <item>
    /// Pass C: the folder holding a copy of this book, found by its audio: which "(n)" a book was given
    /// depended on an export order that may have changed since, and on books that may have left it.
    /// Otherwise the first that <see cref="MayTake"/> allows.
    /// </item>
    /// </list>
    /// </summary>
    private void ClaimFolder(Book book)
    {
        var free = Enumerable.Range(1, MaxDisambiguationAttempts)
            .Select(attempt => AsRecorded(book, _disk.Spelled(book.Parent, book.BaseName + Suffix(attempt))))
            .Where(folder => !_claimedFolders.Contains(folder) && !(_folderOwners.TryGetValue(folder, out var owner) && owner != book.Id))
            .ToList();

        // Other books' recorded folders are not free, so a recorded one left is this book's own. Kept only
        // while it is still a book's folder: once book folders are in it (a sort stopped after filing a
        // series there, before this book moved out), its audio would lie loose beside them, so it moves.
        var folder = free.FirstOrDefault(folder => _folderOwners.ContainsKey(folder) && !_disk.HoldsBookFolders(folder))
                     ?? free.FirstOrDefault(folder => _disk.HasFolder(book.Parent, Path.GetFileName(folder)) && HoldsCopyOf(book, folder))
                     ?? free.FirstOrDefault(folder => MayTake(book, folder));
        if (folder is null)
        {
            book.Warnings.Add($"Could not find a free folder name for \"{book.Naming.FileStem}\"");
            return;
        }

        // Passed over for a recording under this book's name that is not its download: an older copy
        // of it, or another book's only copy. Only the person can tell, so they are told.
        var passedOver = free
            .TakeWhile(candidate => candidate != folder)
            .Where(candidate => !FolderComparer.Equals(candidate, book.Record?.Folder) && NamedAudioFiles(book, candidate).Any());
        foreach (var passed in passedOver)
        {
            book.Warnings.Add(
                $"Left \"{Path.GetRelativePath(_root, passed)}\" alone: it holds a recording named like this book that differs " +
                "from the download, so the book was given a folder of its own. If it is an old copy of this book, delete it.");
        }

        _claimedFolders.Add(folder);
        book.Folder = folder;
    }

    /// <summary>
    /// The folder the manifest records for the book, spelled exactly as recorded, where <paramref name="folder"/>
    /// names it spelled otherwise. On a case-sensitive disk "Author/foo" beside the book's "Author/Foo" is another
    /// folder, maybe another book's, and the book must neither move into it nor take the names on record there.
    /// </summary>
    private static string AsRecorded(Book book, string folder) =>
        book.Record is { } record && FolderComparer.Equals(folder, record.Folder) ? record.Folder : folder;

    /// <summary>
    /// Whether <paramref name="folder"/>, which no other book has, may be this book's: it holds no audio,
    /// or it holds this book's own audio, as a sort that stopped before saving its record left it, or an
    /// older version's "Book N" folder. A recording merely named like the book is not enough: the export
    /// cannot tell a changed download from another book's only copy.
    /// </summary>
    private bool MayTake(Book book, string folder)
    {
        return !_disk.HoldsBookFolders(folder) && (_disk.AudioFiles(folder).Count == 0 || HoldsCopyOf(book, folder));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is a book's folder with this book's audio under its name. Never
    /// a folder holding book folders: that is a series, even one that has left the export.
    /// </summary>
    private bool HoldsCopyOf(Book book, string folder)
    {
        return !_disk.HoldsBookFolders(folder) && NamedAudioFiles(book, folder).Any(file => FileComparison.AreSameQuick(book.AudioSource!, file));
    }

    /// <summary>The audio files in <paramref name="folder"/> under the book's own name: "Title.m4b", not "Title (2).m4b".</summary>
    private IEnumerable<string> NamedAudioFiles(Book book, string folder)
    {
        return _disk.AudioFiles(folder).Where(file => SameName(Path.GetFileNameWithoutExtension(file), book.Naming.FileStem));
    }

    /// <summary>The book's files in its folder, and the files the manifest records for it elsewhere, which move there with it.</summary>
    private void PlanFiles(Book book)
    {
        var audioDestination = DestinationFile(book.Folder!, book.Naming.FileStem, Path.GetExtension(book.AudioSource!));
        var pdfDestination = book.PdfSource is null ? null : DestinationFile(book.Folder!, book.Naming.FileStem, ".pdf");
        if (!new[] { book.Folder, audioDestination, pdfDestination }.OfType<string>().All(path => PathSanitizer.IsWithin(_root, path)))
        {
            book.Folder = null;
            book.Warnings.Add($"Refusing to write \"{book.Naming.FileStem}\" outside the destination folder");
            return;
        }

        book.AudioDestination = audioDestination;
        book.PdfDestination = pdfDestination;
        book.AudioMoveFrom = RecordedFile(book.Record, audioDestination);
        book.PdfMoveFrom = RecordedFile(book.Record, pdfDestination);

        // A book that moves takes every file on record with it, not only those this run writes: a PDF
        // no longer in the source, or its audio in a format it has changed from, is still its own, and
        // left behind off the record it would be in a folder another book of its title may be given.
        if (book.Record is { } record && !PathComparer.Equals(record.Folder, book.Folder))
        {
            book.OtherMoves.AddRange(record.Files
                .Select(name => Path.Combine(record.Folder, name))
                .Where(file => !PathComparer.Equals(file, book.AudioMoveFrom) && !PathComparer.Equals(file, book.PdfMoveFrom))
                .Select(file => (file, DestinationFile(book.Folder!, book.Naming.FileStem, Path.GetExtension(file)))));
        }
    }

    /// <summary>The book's file in its folder: one already there under a name that means the same, or the stem.</summary>
    private string DestinationFile(string folder, string stem, string extension)
    {
        var existing = _disk.Files(folder).FirstOrDefault(name =>
            string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase) &&
            NameKey(Path.GetFileNameWithoutExtension(name)) == NameKey(stem));

        return Path.Combine(folder, existing ?? stem + extension);
    }

    /// <summary>The file the manifest records for a book in the format of <paramref name="destination"/>, unless it is already there.</summary>
    private static string? RecordedFile(ManifestEntry? record, string? destination)
    {
        var recorded = record?.Files
            .Where(name => string.Equals(Path.GetExtension(name), Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
            .Select(name => Path.Combine(record.Folder, name))
            .FirstOrDefault();

        return destination is null || recorded is null || string.Equals(recorded, destination, PathSanitizer.PathComparison) ? null : recorded;
    }

    /// <summary>
    /// Pass D: moves the files an older version left loose into the books they belong to, rather than
    /// copying those books again beside them. Only for books the manifest does not know, whose file is
    /// not in place yet, and never a file some book is written to in this run or the manifest records.
    ///
    /// Whose a file is is decided by content, not by order. A file only one book could have left is that
    /// book's: moved, it is replaced by the update check if the download changed. Where several books
    /// share its name (older versions told them apart as "Title (2)"), each takes only a file with its
    /// own audio: the rest may be the only copy of a book missing from the source or gone from the
    /// export, so they stay and the problems list names them.
    /// </summary>
    /// <returns>Every file offered to a book, adopted or named in a book's problems.</returns>
    private HashSet<string> AdoptOldFiles(List<Book> books, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        var newBooks = books.Where(book => book.Record is null).ToList();
        var untouchable = SpokenFor(books, manifest);
        var adopted = new HashSet<string>(FolderComparer);
        var firstSeenBy = new Dictionary<string, Book>(FolderComparer);

        // Only a file with the book's own download to compare it with: a PDF named like the book beside
        // the audio it adopts is not the book's for that (see FileSorter.FileOwnership.MayMove), and is
        // left where it is and named in the problems (see WarnAboutLooseFiles).
        foreach (var book in newBooks.Where(book => book.Folder is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            book.AudioMoveFrom = Adopt(book, book.AudioSource, book.AudioDestination);
            book.PdfMoveFrom = Adopt(book, book.PdfSource, book.PdfDestination);
        }

        // Once per file, however many books could have left it.
        foreach (var (file, book) in firstSeenBy.Where(pair => !adopted.Contains(pair.Key)))
        {
            book.Warnings.Add(LeftInPlaceWarning(file, books));
        }

        return firstSeenBy.Keys.ToHashSet(FolderComparer);

        string? Adopt(Book book, string? source, string? destination)
        {
            if (source is null || destination is null || File.Exists(destination))
            {
                return null;
            }

            foreach (var folder in OldFileFolders(book))
            {
                var candidates = OldFiles(folder, book, Path.GetExtension(destination))
                    .Where(file => !untouchable.Contains(file) && PathSanitizer.IsWithin(_root, file))
                    .ToList();
                candidates.ForEach(candidate => firstSeenBy.TryAdd(candidate, book));

                // Only the book's own audio makes a file the book's: a name alone may as well be another
                // book of the same title that has left the export. (FileSorter checks this again.)
                var match = candidates.FirstOrDefault(file => !adopted.Contains(file) && FileComparison.AreSameQuick(source, file));
                if (match is not null)
                {
                    adopted.Add(match);
                    return match;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Names each audio or PDF file still loose in a folder this run files a book in or moves a file out
    /// of, or any folder around those, that no book was offered (see <see cref="AdoptOldFiles"/>): one a
    /// book that has left the export left, or one from before a book's title or format changed. Beside
    /// book folders it makes library tools read the whole author or series folder as one book, so it is
    /// not left unsaid, least of all where this run changed what is around it.
    /// </summary>
    private void WarnAboutLooseFiles(List<Book> books, LibraryManifest manifest, HashSet<string> offered)
    {
        var spokenFor = SpokenFor(books, manifest);
        spokenFor.UnionWith(offered);

        var movedFrom = books
            .SelectMany(book => new[] { book.AudioMoveFrom, book.PdfMoveFrom }.Concat(book.OtherMoves.Select(move => move.From)))
            .OfType<string>();
        var looseFiles = _claimedFolders
            .Concat(movedFrom)
            .SelectMany(FoldersHolding)
            .Distinct(PathComparer)
            .SelectMany(_disk.BookFiles)
            .Where(file => !spokenFor.Contains(file))
            .Order(StringComparer.Ordinal);

        _warnings.AddRange(looseFiles.Select(file => LeftInPlaceWarning(file, books)));
    }

    /// <summary>The folders <paramref name="path"/> is in, from its own up to the destination, which is not one of them.</summary>
    private IEnumerable<string> FoldersHolding(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && PathSanitizer.IsWithin(_root, parent); parent = Path.GetDirectoryName(parent))
        {
            yield return parent;
        }
    }

    /// <summary>
    /// What the problems list says about a file left where it was, by the books named like it where an
    /// older version would have left their files. One named like a book missing from the source may be
    /// its only copy: it is never called nobody's, nor is deleting it suggested. Nor is it for a PDF
    /// named like a book this run sorts without a PDF download to compare it with: it may be that
    /// book's, or a same-titled book's only copy, and only the person can tell.
    /// </summary>
    private string LeftInPlaceWarning(string file, List<Book> books)
    {
        var path = Path.GetRelativePath(_root, file);
        var namedLike = books
            .Where(book =>
                OldFileFolders(book).Contains(Path.GetDirectoryName(file), FolderComparer) &&
                IsNamedAfter(Path.GetFileNameWithoutExtension(file), book.Naming.FileStem))
            .ToList();
        var missing = namedLike.FirstOrDefault(book => book.AudioSource is null);
        var unproven = string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase)
            ? namedLike.FirstOrDefault(book => book.Folder is not null && book.PdfSource is null)
            : null;

        return (missing, unproven) switch
        {
            ({ } book, _) => $"Left \"{path}\" where it was: it is named like \"{book.Title}\", which is not in the source folder, " +
                             "so it may be that book's only copy.",
            (_, { } book) => $"Left \"{path}\" where it was: it is named like \"{book.Title}\", but the source folder has no PDF of " +
                             "that book to compare it with, so it may as well be another book's. If it is this book's, move it into " +
                             $"\"{Path.GetRelativePath(_root, book.Folder!)}\" yourself.",
            _ => $"Left \"{path}\" where it was: it matches none of the books in the export. If it is an old copy, delete it."
        };
    }

    /// <summary>
    /// The files a book is written to or moved from or to in this run, or that the manifest records:
    /// never another book's old copy.
    /// </summary>
    private static HashSet<string> SpokenFor(List<Book> books, LibraryManifest manifest)
    {
        return books
            .SelectMany(book => new[] { book.AudioDestination, book.PdfDestination, book.AudioMoveFrom, book.PdfMoveFrom })
            .Concat(books.SelectMany(book => book.OtherMoves.SelectMany(move => new[] { move.From, move.To })))
            .OfType<string>()
            .Concat(manifest.Books.Values.SelectMany(entry => entry.Files.Select(name => Path.Combine(entry.Folder, name))))
            .ToHashSet(FolderComparer);
    }

    /// <summary>
    /// Where an older version may have left a book's files: loose in its parent folder (standalone
    /// books in the author folder, series books without a number in the series folder), and in the
    /// plain "Title" or "Book N" folder when the book now has a "(2)" one, as every book with one
    /// number shared "Book N", and a series named like a standalone book may now have its folder.
    /// Never a folder on record for another book: what is in it is that book's, or put there by hand.
    /// </summary>
    private IEnumerable<string> OldFileFolders(Book book)
    {
        return UnownedOldFolders(book).Where(folder => !FolderComparer.Equals(folder, book.Folder));
    }

    /// <summary>The <see cref="OldFileFolders"/> of a book, and the book's own folder when it is one of them.</summary>
    private IEnumerable<string> UnownedOldFolders(Book book)
    {
        return new[] { book.Parent, _disk.Spelled(book.Parent, book.BaseName) }.Where(folder => !_folderOwners.ContainsKey(folder));
    }

    /// <summary>
    /// The files in <paramref name="folder"/> named after a book, of one <paramref name="extension"/> or
    /// (null) any, the plain name first, then "Title (2)"...
    /// </summary>
    private IEnumerable<string> OldFiles(string folder, Book book, string? extension)
    {
        return _disk.Files(folder)
            .Where(name => extension is null || string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
            .Where(name => IsNamedAfter(Path.GetFileNameWithoutExtension(name), book.Naming.FileStem))
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => Path.Combine(folder, name));
    }

    /// <summary>What the books that could have left a file of their name in <paramref name="folder"/> have in common.</summary>

    /// <summary>What books have in common that the "(n)" of one folder name tells apart, and that share a title.</summary>

    /// <summary>A name ignoring spelling and punctuation ("A Book: The Sequel" is "A Book - The Sequel"); punctuation alone as it is.</summary>
    private static string NameKey(string name)
    {
        var key = PathSanitizer.NormalizeComparisonKey(name);
        return key.Length > 0 ? key : name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="name"/> is <paramref name="stem"/>, spelling and punctuation aside. A " (2)"
    /// is no mere punctuation: "Dune (2)" is the second "Dune", not a book called "Dune 2".
    /// </summary>
    private static bool SameName(string name, string stem) => NameKey(name) == NameKey(stem) && NumberedAlike(name, stem);

    /// <summary>Whether <paramref name="name"/> is a book's, as an older version named it: "Title", or "Title (2)"... for a later book of the title.</summary>
    private static bool IsNamedAfter(string name, string stem) => SameName(name, stem) || (WithoutSuffix(name) is { } unnumbered && SameName(unnumbered, stem));

    /// <summary>Whether both names, or neither, end in a <see cref="Suffix"/>.</summary>
    private static bool NumberedAlike(string name, string other) => (WithoutSuffix(name) is null) == (WithoutSuffix(other) is null);

    /// <summary>The author folder, or the series folder in it.</summary>
    private string ResolveParentDirectory(BookSortPlan plan)
    {
        var authorDirectory = Path.Combine(_root, ResolveDirectoryName(_root, plan.Author, isSeries: false));

        return plan.SeriesName is null
            ? authorDirectory
            : Path.Combine(authorDirectory, ResolveDirectoryName(authorDirectory, plan.SeriesName, isSeries: true));
    }

    /// <summary>
    /// Picks the folder name to use inside <paramref name="parentDirectory"/>, preferring a folder
    /// that already exists and means the same thing so repeat runs do not fragment a library.
    /// </summary>
    /// <param name="isSeries">A series name, which also ignores a leading "The" and a trailing "Series", "Saga"...</param>
    private string ResolveDirectoryName(string parentDirectory, string requestedName, bool isSeries)
    {
        var normalized = isSeries ? PathSanitizer.NormalizeSeriesKey(requestedName) : PathSanitizer.NormalizeComparisonKey(requestedName);
        if (normalized.Length == 0)
        {
            return requestedName;
        }

        // The kind is part of the key: "The Witcher" as a series and "Witcher" as a book normalise
        // to the same text under different rules, and must not be given each other's folder. Each
        // spelling is also remembered on its own, so the first row's spelling never decides the
        // folder of a row whose own spelling names a folder on disk.
        var sharedKey = $"{parentDirectory}\u0000{isSeries}\u0000{normalized}";
        var exactKey = PathSanitizer.NormalizeComparisonKey(requestedName);
        var spellingKey = $"{sharedKey}\u0000{exactKey}";
        if (_resolvedDirectoryNames.TryGetValue(spellingKey, out var cached))
        {
            return cached;
        }

        // The folder spelled like this name comes first, then the folder another spelling was given
        // in this run, so a new series listed under two spellings gets one folder. Only then the
        // looser series match ("Wheel of Time" for "The Wheel of Time Series"), which never takes a
        // book's own folder: "The Witcher" must not settle in the folder of a book called "Witcher". Never
        // a folder whose "(2)" the name lacks: "Dune (2)" is the second "Dune", not the folder of "Dune 2".
        var existingNames = _disk.Folders(parentDirectory)
            .Where(name => NumberedAlike(name, requestedName))
            .Order(StringComparer.Ordinal)
            .ToList();
        var resolved = existingNames.FirstOrDefault(name => PathSanitizer.NormalizeComparisonKey(name) == exactKey)
            ?? _resolvedDirectoryNames.GetValueOrDefault(sharedKey)
            ?? existingNames.FirstOrDefault(name =>
                isSeries && PathSanitizer.NormalizeSeriesKey(name) == normalized && !_disk.IsBookFolder(Path.Combine(parentDirectory, name)))
            ?? requestedName;

        if (isSeries)
        {
            resolved = UnheldFolderName(parentDirectory, resolved);
        }

        _resolvedDirectoryNames.TryAdd(sharedKey, resolved);
        _resolvedDirectoryNames[spellingKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// <paramref name="name"/>, or else the first "name (2)"... that is not another book's folder, when
    /// <paramref name="name"/> is the folder of a book on record that this run leaves where it is (see
    /// <see cref="_heldFolders"/>). A series filed in it would lie beside that book's audio, and library
    /// tools would read the whole folder as that one book. A book this run sorts moves out of the way instead.
    /// </summary>
    private string UnheldFolderName(string parentDirectory, string name)
    {
        return Enumerable.Range(1, MaxDisambiguationAttempts)
            .Select(attempt => (Attempt: attempt, Folder: _disk.Spelled(parentDirectory, name + Suffix(attempt))))
            .Where(candidate => !_heldFolders.Contains(candidate.Folder) && (candidate.Attempt == 1 || !_disk.IsBookFolder(candidate.Folder)))
            .Select(candidate => Path.GetFileName(candidate.Folder))
            .FirstOrDefault() ?? name;
    }

    /// <summary>"", " (2)", " (3)"...: what tells apart the folders of books that share a name.</summary>
    private static string Suffix(int attempt) => attempt == 1 ? string.Empty : $" ({attempt})";

    /// <summary><paramref name="name"/> without the <see cref="Suffix"/> it has, or null when it has none.</summary>
    private static string? WithoutSuffix(string name)
    {
        var match = SuffixPattern.Match(name);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// A repeat, and a book missing from the source, end up with no folder and nothing to write: the
    /// sort counts a repeat as up to date, as its first listing copies it. A book is its audio, so a
    /// missing book's PDF is not copied on its own either: that would report it sorted and give library
    /// tools a book folder with nothing to play.
    /// </summary>
    private static PlannedCopy ToPlannedCopy(Book book) => new()
    {
        Book = book.Row,
        Title = book.Title,
        BookId = book.Id,
        IsMissingFromSource = !book.IsRepeat && book.AudioSource is null,
        TargetDirectory = book.Folder,
        AudioSource = book.AudioDestination is null ? null : book.AudioSource,
        AudioDestination = book.AudioDestination,
        AudioMoveFrom = book.AudioMoveFrom,
        PdfSource = book.PdfDestination is null ? null : book.PdfSource,
        PdfDestination = book.PdfDestination,
        PdfMoveFrom = book.PdfMoveFrom,
        OtherMoves = book.OtherMoves,
        Warning = book.Warnings.Count > 0 ? string.Join("; ", book.Warnings) : null
    };

    /// <summary>"We Are Legion (We Are Bob) — Dennis E. Taylor": how a person would name the book.</summary>
    private static string BuildTitle(OpenAudible book, BookSortPlan plan)
    {
        var title = string.IsNullOrWhiteSpace(book.Title) ? plan.FileStem : book.Title.Trim();
        return $"{title} — {plan.Author}";
    }
}
