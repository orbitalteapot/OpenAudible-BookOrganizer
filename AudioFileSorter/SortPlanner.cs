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
/// <item>files an older version left where library tools trip over them move to the book they belong to.</item>
/// </list>
/// </summary>
public sealed class SortPlanner
{
    private const int MaxDisambiguationAttempts = 100;

    private const string EmptySourceMessage =
        "The book's file in the source folder is empty, as it is while OpenAudible is still downloading or " +
        "converting it. It is copied once it is complete.";

    /// <summary>The " (2)", " (3)"... that <see cref="Suffix"/> adds. " (1)" is none: "Heroes (1)" is a title.</summary>
    private static readonly Regex SuffixPattern = new(@"^(?<name>.+) \((?:[2-9]|[1-9][0-9]+)\)$", RegexOptions.CultureInvariant);

    /// <summary>As the file system compares paths: on Linux "It.m4b" and "IT.m4b" are two books, not one listed twice.</summary>
    private static readonly StringComparer SourceComparer = StringComparer.FromComparison(PathSanitizer.PathComparison);

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
    /// source), which therefore stay where they are, in the way of anything else.
    /// </summary>
    private readonly HashSet<string> _heldFolders = new(FolderComparer);

    private string _root = string.Empty;

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
        var sharingFolderName = unique.Where(book => book.Record is null).ToLookup(SharedFolderKey, FolderComparer);
        foreach (var book in inSource.OrderBy(book => book.Record is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimFolder(book, contested: sharingFolderName[SharedFolderKey(book)].Any(other => other != book));
        }

        inSource.Where(book => book.Folder is not null).ToList().ForEach(PlanFiles);

        // Only now is every destination known, so an old file is never taken from where another
        // book is about to be written.
        AdoptOldFiles(unique, manifest, cancellationToken);

        return rows.Select(ToPlannedCopy).ToList();
    }

    /// <summary>One row of the export as the planner sees it, filled in pass by pass.</summary>
    private sealed class Book(OpenAudible row, BookSortPlan naming, string title)
    {
        public OpenAudible Row { get; } = row;
        public BookSortPlan Naming { get; } = naming;
        public string Title { get; } = title;
        public string StemKey { get; } = NameKey(naming.FileStem);
        public string? Id { get; init; }
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
            books.Add(new Book(row, naming, BuildTitle(row, naming)) { Id = BookIdFor(row, audioSource), AudioSource = audioSource });
        }

        MarkRepeats(books);
        var sortedIds = books.Where(book => !book.IsRepeat && book.AudioSource is not null).Select(book => book.Id!).ToHashSet(StringComparer.Ordinal);
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
            book.Record = book.Id is null ? null : manifest.Get(book.Id);
        }

        return books;
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
        var idsInSource = books.Where(book => book.AudioSource is not null).Select(book => book.Id!).ToHashSet(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(SourceComparer);

        foreach (var book in books)
        {
            // '|' so both are remembered.
            book.IsRepeat = book.AudioSource is not null
                ? !seenSources.Add(book.AudioSource) | !seenIds.Add(book.Id!)
                : book.Id is not null && (idsInSource.Contains(book.Id) || !seenIds.Add(book.Id));
        }
    }

    /// <summary>
    /// See <see cref="PlannedCopy.BookId"/>. The file name keeps its case: on a case-sensitive disk
    /// "It.m4b" and "IT.m4b" are two books, and an export spells a file the same on every platform.
    /// </summary>
    private static string? BookIdFor(OpenAudible row, string? audioSource)
    {
        var listed = new[] { row.ASIN, row.Key, row.ProductID }.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return listed?.Trim().ToLowerInvariant() ?? (audioSource is null ? null : "file:" + Path.GetFileName(audioSource));
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
    /// <param name="contested">Another book the manifest does not know wants a folder of this name, for a book of this title.</param>
    private void ClaimFolder(Book book, bool contested)
    {
        var free = Enumerable.Range(1, MaxDisambiguationAttempts)
            .Select(attempt => _disk.Spelled(book.Parent, book.BaseName + Suffix(attempt)))
            .Where(folder => !_claimedFolders.Contains(folder) && !(_folderOwners.TryGetValue(folder, out var owner) && owner != book.Id))
            .ToList();

        // Other books' recorded folders are not free, so a recorded one left is this book's own.
        var folder = free.FirstOrDefault(_folderOwners.ContainsKey)
                     ?? free.FirstOrDefault(folder => _disk.Folders(book.Parent).ContainsKey(Path.GetFileName(folder)) && HoldsCopyOf(book, folder))
                     ?? free.FirstOrDefault(folder => MayTake(book, folder, contested));
        if (folder is null)
        {
            book.Warnings.Add($"Could not find a free folder name for \"{book.Naming.FileStem}\"");
            return;
        }

        _claimedFolders.Add(folder);
        book.Folder = folder;
    }

    /// <summary>
    /// Whether <paramref name="folder"/>, which no other book has, may be this book's: it holds no audio,
    /// or its only recording is under this book's name, as a sort that crashed before saving its manifest
    /// left it, or an older version's "Book N" folder. The update check then replaces the file if the
    /// download changed. But the export cannot tell a changed download from another book's only copy:
    /// when another book the manifest does not know could have been filed there too, or the folder holds
    /// more than one recording, only this book's own audio makes it this book's.
    /// </summary>
    private bool MayTake(Book book, string folder, bool contested)
    {
        var audio = _disk.AudioFiles(folder);
        return !_disk.HoldsBookFolders(folder) &&
               (audio.Count == 0 ||
                (NamedAudioFiles(book, folder).Any() && ((!contested && audio.Count == 1) || HoldsCopyOf(book, folder))));
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
        return _disk.AudioFiles(folder).Where(file => NameKey(Path.GetFileNameWithoutExtension(file)) == book.StemKey);
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
    private void AdoptOldFiles(List<Book> books, LibraryManifest manifest, CancellationToken cancellationToken)
    {
        var newBooks = books.Where(book => book.Record is null).ToList();
        var possibleOwners = newBooks
            .SelectMany(book => OldFileFolders(book).Select(folder => (Key: OldFileKey(folder, book), Book: book)))
            .ToLookup(pair => pair.Key, pair => pair.Book, FolderComparer);
        var untouchable = books
            .SelectMany(book => new[] { book.AudioDestination, book.PdfDestination })
            .OfType<string>()
            .Concat(manifest.Books.Values.SelectMany(entry => entry.Files.Select(name => Path.Combine(entry.Folder, name))))
            .ToHashSet(FolderComparer);
        var adopted = new HashSet<string>(FolderComparer);
        var firstSeenBy = new Dictionary<string, Book>(FolderComparer);

        foreach (var book in newBooks.Where(book => book.Folder is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            book.AudioMoveFrom = Adopt(book, book.AudioSource, book.AudioDestination);
            book.PdfMoveFrom = Adopt(book, book.PdfSource, book.PdfDestination);
        }

        // Once per file, however many books could have left it.
        foreach (var (file, book) in firstSeenBy.Where(pair => !adopted.Contains(pair.Key)))
        {
            book.Warnings.Add(
                $"Left \"{Path.GetRelativePath(_root, file)}\" where it was: it matches none of the books in the export. " +
                "If it is an old copy, delete it.");
        }

        string? Adopt(Book book, string? source, string? destination)
        {
            if (source is null || destination is null || File.Exists(destination))
            {
                return null;
            }

            foreach (var folder in OldFileFolders(book))
            {
                var candidates = OldFiles(folder, book.StemKey, Path.GetExtension(destination))
                    .Where(file => !untouchable.Contains(file) && PathSanitizer.IsWithin(_root, file))
                    .ToList();
                candidates.ForEach(candidate => firstSeenBy.TryAdd(candidate, book));

                var onlyOwner = candidates.Count == 1 && possibleOwners[OldFileKey(folder, book)].Count() == 1 && !NamesakeOnRecord(book);
                var match = candidates.FirstOrDefault(file => !adopted.Contains(file) && (onlyOwner || FileComparison.AreSameQuick(source, file)));
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
    /// Where an older version may have left a book's files: loose in its parent folder (standalone
    /// books in the author folder, series books without a number in the series folder), and in the
    /// plain "Title" or "Book N" folder when the book now has a "(2)" one, as every book with one
    /// number shared "Book N", and a series named like a standalone book may now have its folder.
    /// Never a folder on record for another book: what is in it is that book's, or put there by hand.
    /// </summary>
    private IEnumerable<string> OldFileFolders(Book book)
    {
        return new[] { book.Parent, _disk.Spelled(book.Parent, book.BaseName) }
            .Where(folder => !FolderComparer.Equals(folder, book.Folder) && !_folderOwners.ContainsKey(folder));
    }

    /// <summary>
    /// Whether a book of this one's folder name is on record beside where it goes. Then a sort that
    /// kept a record has been through the folder, and a loose file of the name still there is one it
    /// left because it matched no book in the export: it may be a returned book's only copy, and only
    /// this book's own audio makes it this book's.
    /// </summary>
    private bool NamesakeOnRecord(Book book)
    {
        var nameKey = NameKey(book.BaseName);
        return _folderOwners.Keys.Any(folder =>
            FolderComparer.Equals(Path.GetDirectoryName(folder), book.Parent) &&
            NameKey(WithoutSuffix(Path.GetFileName(folder)) ?? Path.GetFileName(folder)) == nameKey);
    }

    /// <summary>The files in <paramref name="folder"/> named after a book, the plain name first, then "Title (2)"...</summary>
    private IEnumerable<string> OldFiles(string folder, string stemKey, string extension)
    {
        return _disk.Files(folder)
            .Where(name => string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
            .Where(name => NameKey(Path.GetFileNameWithoutExtension(name)) == stemKey ||
                           (WithoutSuffix(Path.GetFileNameWithoutExtension(name)) is { } unnumbered && NameKey(unnumbered) == stemKey))
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => Path.Combine(folder, name));
    }

    /// <summary>What the books that could have left a file of their name in <paramref name="folder"/> have in common.</summary>
    private static string OldFileKey(string folder, Book book) => $"{folder}\u0000{book.StemKey}";

    /// <summary>What books have in common that the "(n)" of one folder name tells apart, and that share a title.</summary>
    private static string SharedFolderKey(Book book) => $"{book.Parent}\u0000{book.BaseName}\u0000{book.StemKey}";

    /// <summary>A name ignoring spelling and punctuation ("A Book: The Sequel" is "A Book - The Sequel"); punctuation alone as it is.</summary>
    private static string NameKey(string name)
    {
        var key = PathSanitizer.NormalizeComparisonKey(name);
        return key.Length > 0 ? key : name.Trim().ToLowerInvariant();
    }

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
        // book's own folder: "The Witcher" must not settle in the folder of a book called "Witcher".
        var existingNames = _disk.Folders(parentDirectory).Values.Order(StringComparer.Ordinal).ToList();
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
        Warning = book.Warnings.Count > 0 ? string.Join("; ", book.Warnings) : null
    };

    /// <summary>"We Are Legion (We Are Bob) — Dennis E. Taylor": how a person would name the book.</summary>
    private static string BuildTitle(OpenAudible book, BookSortPlan plan)
    {
        var title = string.IsNullOrWhiteSpace(book.Title) ? plan.FileStem : book.Title.Trim();
        return $"{title} — {plan.Author}";
    }
}
