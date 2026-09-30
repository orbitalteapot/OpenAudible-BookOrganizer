using System.Text.Encodings.Web;
using System.Text.Json;
using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// The organizer's record of which book it filed in which folder, kept at the top of the destination
/// as <see cref="FileName"/>. With it a book keeps its folder from one sort to the next, whatever
/// the export's order or titles do, and no other book is given that folder while the book's files
/// are in it, even once it has left the export: it may be the only copy. Without it, whose a folder
/// is could only be guessed from names, which is left to the first sort of a library an older
/// version laid out (see <see cref="SortPlanner"/>).
///
/// Its presence also tells automatic sorts that the folder is the library, not the empty stand-in
/// for an unmounted drive (see <see cref="SortPathValidator.InspectMarker"/>).
///
/// A recorded file counts as its book's only while it still holds what it held when it went on record
/// (see <see cref="IsRecorded"/>), never by its name alone: a record a sort could not save (a full disk,
/// the app killed, a power cut) stays on disk as it was, and by the time it is read again another book's
/// file may be under a name it records.
///
/// On disk it is JSON with paths relative to the destination, so the library can be moved or
/// mounted elsewhere; in memory every path is a full one inside <see cref="Root"/>.
/// </summary>
public sealed class LibraryManifest
{
    /// <summary>
    /// The manifest's name. The leading dot hides it on macOS and Linux, and library tools pass it by
    /// everywhere; Windows shows it.
    /// </summary>
    public const string FileName = ".openaudible-organizer";

    private const string FormatName = "openaudible-organizer-manifest";
    private const int FormatVersion = 1;

    private const string Note =
        "Written by OpenAudible Book Organizer. It records which book owns which folder, and what each of its files " +
        "held, so that the folder of a book that has left the export is never given to another, and a file changed " +
        "since is never taken for the book's. Deleting it loses that, and automatic sorts pause until a sort is " +
        "started by hand and confirmed.";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // Titles and folder names as they are rather than as \u escapes, for anyone who opens the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>What <see cref="IsPlainName"/> refuses in a name.</summary>
    private static readonly char[] ForbiddenNameCharacters = OperatingSystem.IsWindows() ? ['/', '\\', ':', '\0'] : ['/', '\0'];

    private readonly Dictionary<string, ManifestEntry> _books = new(StringComparer.Ordinal);

    /// <summary>
    /// Where <see cref="Save"/> keeps the file it found that was not a manifest before writing over it,
    /// as it may be a newer version's record or one a person wants back. Null when there is none to keep.
    /// </summary>
    private string? _keepAs;

    private LibraryManifest(string root, bool existed, string? problem)
    {
        Root = root;
        Existed = existed;
        Problem = problem;
    }

    /// <summary>The destination folder, as a full path.</summary>
    public string Root { get; }

    /// <summary>Whether the destination held a manifest file, readable or not.</summary>
    public bool Existed { get; }

    /// <summary>
    /// Why the file in the destination could not be used and the record was started again, worded
    /// for the person sorting. Null when it was read, or there was none.
    /// </summary>
    public string? Problem { get; }

    /// <summary>Every book on record, by book id (see <see cref="PlannedCopy.BookId"/>).</summary>
    public IReadOnlyDictionary<string, ManifestEntry> Books => _books;

    public ManifestEntry? Get(string bookId) => _books.GetValueOrDefault(bookId);

    /// <summary>
    /// Records where a book is filed, in place of anything recorded for it before. Refused, and
    /// false returned, when the folder or a file name would lead outside the destination.
    ///
    /// Each file goes on record with what it holds now, unless the entry gives what it held when it
    /// was recorded before (see <see cref="ManifestEntry.Stamps"/>), which it keeps: taking what a
    /// file carried over from the old record holds now would make whatever is under its name the
    /// book's. So only a file the caller has just proven the book's may be given without one. A file
    /// that cannot be read now goes on record with nothing, which proves nothing.
    /// </summary>
    public bool Set(string bookId, ManifestEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookId);
        ArgumentNullException.ThrowIfNull(entry);

        if (RelativeFolder(entry.Folder) is null || !entry.Files.All(name => IsPlainName(name) && IsInside(Path.Combine(entry.Folder, name))))
        {
            return false;
        }

        var stamps = new Dictionary<string, ContentStamp>(StringComparer.Ordinal);
        foreach (var name in entry.Files)
        {
            var stamp = entry.Stamps.TryGetValue(name, out var recorded) ? recorded : FileComparison.Stamp(Path.Combine(entry.Folder, name));
            if (stamp is { } known)
            {
                stamps[name] = known;
            }
        }

        _books[bookId] = entry with { Stamps = stamps };
        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a file on record as the book's that still holds what it held
    /// when it went on record: only then is it provably the book's own copy, which a sort may move or
    /// replace. Checked when asked, not only when the record was read, and false whenever it cannot be
    /// told, as for a file that cannot be read right now.
    /// </summary>
    public bool IsRecorded(string bookId, string path)
    {
        if (Get(bookId) is not { } entry)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var name = entry.Files.FirstOrDefault(name =>
            string.Equals(Path.GetFullPath(Path.Combine(entry.Folder, name)), fullPath, PathSanitizer.PathComparison));

        return name is not null && entry.Stamps.TryGetValue(name, out var stamp) && FileComparison.Stamp(fullPath) == stamp;
    }

    public bool Remove(string bookId) => _books.Remove(bookId);


    /// <summary>
    /// Reads the manifest in <paramref name="destinationRoot"/>. Never throws for what the file holds:
    /// a missing file is an empty record, and one that is not a manifest (the plain marker earlier
    /// versions of this branch left, a damaged or hand-edited file, a newer version's) is an empty
    /// record with a <see cref="Problem"/>, kept aside when it is saved over. Either way the planner
    /// finds the books in the export in their folders again; only the record of books that have left
    /// it, which kept their folders from other books, is lost with it.
    ///
    /// Only what is still true is kept: an entry that leads outside the destination is ignored, a
    /// file no longer there or no longer holding what it held when it went on record is dropped (the
    /// person deleted or changed it, or a sort whose record was lost put another book's file under
    /// its name), so is a book with none of its files left, and a folder recorded twice keeps its first
    /// owner, as one folder is one book.
    /// </summary>
    /// <exception cref="IOException">
    /// The file is there but cannot be read right now: another program has it open, a network drive
    /// hiccuped, or its permissions changed. Unlike a file that is not a manifest, it may be a good
    /// record, and one rebuilt and saved over it would forget every book that has left the export,
    /// whose folders a later book could then be given. The message is worded for the person sorting.
    /// </exception>
    public static LibraryManifest Load(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var root = Path.GetFullPath(destinationRoot);
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return new LibraryManifest(root, existed: false, problem: null);
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!TryGetBooks(document.RootElement, out var books))
            {
                return Rebuilt(root, path);
            }

            var manifest = new LibraryManifest(root, existed: true, problem: null);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books.EnumerateObject())
            {
                if (manifest.ReadEntry(book.Value) is { } entry && folders.Add(entry.Folder))
                {
                    manifest._books.TryAdd(book.Name, entry);
                }
            }

            return manifest;
        }
        catch (JsonException)
        {
            return Rebuilt(root, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not read its record of which book is in which folder (the {FileName} file), so nothing was sorted: " +
                $"{ex.Message} Sort again once the file can be read.",
                ex);
        }
    }

    /// <summary>An empty record in place of the file at <paramref name="path"/>, which is not a manifest.</summary>
    private static LibraryManifest Rebuilt(string root, string path)
    {
        var keepAs = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var problem =
            $"Its record of which book is in which folder (the {FileName} file) could not be read, so it was rebuilt " +
            $"from the books found in their folders, and the old file kept as {Path.GetFileName(keepAs)}. " +
            "The folders of books that have left the export are no longer set aside for them, so a new book of the same " +
            "name may be filed in one, until that file is repaired and put back.";

        return new LibraryManifest(root, existed: true, problem) { _keepAs = keepAs };
    }

    /// <summary>
    /// Writes the manifest to the destination, atomically (see <see cref="AtomicFile.Write"/>), first
    /// keeping a copy of a file there that was not a manifest (see <see cref="Load"/>).
    /// </summary>
    /// <exception cref="IOException">It could not be written; the one on disk, if any, is untouched.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination cannot be written.</exception>
    public void Save()
    {
        // Sorted, so a person comparing two versions of the file sees what changed and nothing else.
        var books = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (bookId, entry) in _books)
        {
            var files = entry.Files.Select(name => entry.Stamps.TryGetValue(name, out var stamp)
                ? (object)new { name, size = stamp.Size, sample = stamp.Sample }
                : new { name });
            books[bookId] = new { folder = RelativeFolder(entry.Folder), files, title = entry.Title };
        }

        var file = new { format = FormatName, version = FormatVersion, note = Note, books };
        var content = JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions);
        var path = Path.Combine(Root, FileName);

        // Not written again when nothing changed: sorts run as often as every quarter of an hour,
        // and backup and sync tools, folder watchers and a sleeping disk should see no change then.
        if (_keepAs is null && HoldsAlready(path, content))
        {
            return;
        }

        // Copied rather than moved, so the destination keeps its marker if the write fails.
        if (_keepAs is not null && File.Exists(path))
        {
            File.Copy(path, _keepAs, overwrite: false);
        }

        _keepAs = null;
        AtomicFile.Write(path, stream => stream.Write(content));
    }

    /// <summary>Whether the file at <paramref name="path"/> holds exactly <paramref name="content"/>. False when it cannot be read.</summary>
    private static bool HoldsAlready(string path, byte[] content)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetBooks(JsonElement root, out JsonElement books)
    {
        books = default;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String &&
               format.GetString() == FormatName &&
               root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
               version.TryGetInt32(out var number) && number == FormatVersion &&
               root.TryGetProperty("books", out books) && books.ValueKind == JsonValueKind.Object;
    }

    /// <summary>One book's entry, or null when it is malformed, leads outside the destination, or its files are gone.</summary>
    private ManifestEntry? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("folder", out var folderElement) || folderElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var folder = FullFolder(folderElement.GetString()!);
        if (folder is null || IsGone(folder))
        {
            return null;
        }

        // A folder that cannot be looked into right now (its permissions, or its parent's, a network
        // hiccup) is not a book the person deleted: it stays the book's, with the files on record.
        var canLook = Directory.Exists(folder) && ListNames(folder) is not null;
        var files = new List<string>();
        var stamps = new Dictionary<string, ContentStamp>(StringComparer.Ordinal);
        foreach (var file in filesElement.EnumerateArray())
        {
            // A file on record with nothing to tell it by (a hand edit, an earlier build's record) proves nothing.
            if (ReadFile(file) is not var (name, stamp) || !IsPlainName(name) || !IsInside(Path.Combine(folder, name)) ||
                stamps.ContainsKey(name) || (canLook && !StillHolds(Path.Combine(folder, name), stamp)))
            {
                continue;
            }

            files.Add(name);
            stamps[name] = stamp;
        }

        var title = element.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()!
            : string.Empty;

        return files.Count == 0 ? null : new ManifestEntry(folder, files, title) { Stamps = stamps };
    }

    /// <summary>One recorded file's name and what it held, or null when the record does not say both.</summary>
    private static (string Name, ContentStamp Stamp)? ReadFile(JsonElement file)
    {
        return file.ValueKind == JsonValueKind.Object &&
               file.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
               file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var length) && length >= 0 &&
               file.TryGetProperty("sample", out var sample) && sample.ValueKind == JsonValueKind.String && sample.GetString() is { Length: > 0 } hash
            ? (name.GetString()!, new ContentStamp(length, hash))
            : null;
    }

    /// <summary>
    /// Whether the recorded file at <paramref name="path"/> is still there holding what it held when it
    /// went on record. One that is there but cannot be read right now stays on record, as a folder that
    /// cannot be looked into does: it still keeps its folder from other books, and <see cref="IsRecorded"/>
    /// takes it for the book's only once it can be read and holds that.
    /// </summary>
    private static bool StillHolds(string path, ContentStamp stamp)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return FileComparison.Stamp(path) is not { } now || now == stamp;
    }

    /// <summary>
    /// The full path of a folder the file records as <paramref name="relative"/> ("Author/Series/Book 1"),
    /// or null when it is not a plain relative path inside the destination: rooted, climbing out with
    /// "..", or naming a drive. The file is only ever written by this class, but anyone can edit it.
    /// </summary>
    private string? FullFolder(string relative)
    {
        var segments = relative.Split('/');
        if (Path.IsPathRooted(relative) || !segments.All(IsPlainName))
        {
            return null;
        }

        try
        {
            var folder = Path.GetFullPath(Path.Combine([Root, .. segments]));
            return IsInside(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>How the file records <paramref name="folder"/>: relative, with "/" on every platform. Null when it is not inside the destination.</summary>
    private string? RelativeFolder(string folder)
    {
        if (!IsInside(folder))
        {
            return null;
        }

        var relative = Path.GetRelativePath(Root, folder).Replace(Path.DirectorySeparatorChar, '/');
        return FullFolder(relative) is null ? null : relative;
    }

    private bool IsInside(string path) => PathSanitizer.IsWithin(Root, path);

    /// <summary>
    /// Whether <paramref name="folder"/> is certainly not there: its parent can be listed and does not
    /// hold it. Not when that cannot be told, as when a parent cannot be looked into right now: a
    /// book dropped from the record then would have its folder, maybe its only copy, given to another.
    /// </summary>
    private bool IsGone(string folder)
    {
        if (Directory.Exists(folder))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(folder);
        if (parent is null)
        {
            return false;
        }

        return Directory.Exists(parent)
            ? ListNames(parent) is { } names && !names.Contains(Path.GetFileName(folder), StringComparer.FromComparison(PathSanitizer.PathComparison))
            : IsInside(parent) && IsGone(parent);
    }

    /// <summary>The names of what is in <paramref name="folder"/>, or null when it cannot be listed right now.</summary>
    private static string[]? ListNames(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is one plain folder or file name: not "." or "..", and nothing that
    /// separates folders or names a drive. Only what does that on this system: ':' and '\' are ordinary
    /// characters on Linux and macOS, where folders spelled with them are books like any other.
    /// </summary>
    private static bool IsPlainName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name.IndexOfAny(ForbiddenNameCharacters) < 0;
    }
}
