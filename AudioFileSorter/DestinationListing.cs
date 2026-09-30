namespace AudioFileSorter;

/// <summary>
/// What the destination held when planning started, each folder listed at most once. Planning
/// writes nothing, so the disk does not change under it, and on a network share every listing saved
/// is a round trip saved. A folder that cannot be read lists as empty rather than failing the sort.
/// Folder names are looked up without regard to case, as a case-insensitive disk (Windows, macOS,
/// most NAS shares) holds one folder however it is spelled.
/// </summary>
internal sealed class DestinationListing
{
    private readonly Dictionary<string, (Dictionary<string, string> Folders, string[] Files)> _listings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders in <paramref name="folder"/>, by name without regard to case, spelled as on disk.</summary>
    public Dictionary<string, string> Folders(string folder) => List(folder).Folders;

    /// <summary>The names of the files in <paramref name="folder"/>, in ordinal order.</summary>
    public string[] Files(string folder) => List(folder).Files;

    public List<string> AudioFiles(string folder) => FilesOfType(folder, SourceFileLocator.AudioExtensions);

    /// <summary>The files in <paramref name="folder"/> library tools take for a book's: its audio, and PDFs.</summary>
    public List<string> BookFiles(string folder) => FilesOfType(folder, [.. SourceFileLocator.AudioExtensions, ".pdf"]);

    /// <summary>Whether <paramref name="folder"/> holds other books' folders, as a series folder does, even one that has left the export.</summary>
    public bool HoldsBookFolders(string folder) => Folders(folder).Values.Any(name => AudioFiles(Path.Combine(folder, name)).Count > 0);

    /// <summary>A folder with audio in it and no book folders inside: one book's, not a series'.</summary>
    public bool IsBookFolder(string folder) => AudioFiles(folder).Count > 0 && !HoldsBookFolders(folder);

    /// <summary><paramref name="name"/> inside <paramref name="parent"/>, spelled as the folder on disk is when there is one.</summary>
    public string Spelled(string parent, string name) => Path.Combine(parent, Folders(parent).GetValueOrDefault(name) ?? name);

    private List<string> FilesOfType(string folder, string[] extensions)
    {
        return Files(folder)
            .Where(name => extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            .Select(name => Path.Combine(folder, name))
            .ToList();
    }

    private (Dictionary<string, string> Folders, string[] Files) List(string folder)
    {
        if (!_listings.TryGetValue(folder, out var listing))
        {
            var folders = Names(folder, Directory.EnumerateDirectories).DistinctBy(name => name, StringComparer.OrdinalIgnoreCase);
            listing = (folders.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase),
                       Names(folder, Directory.EnumerateFiles).Order(StringComparer.Ordinal).ToArray());
            _listings[folder] = listing;
        }

        return listing;
    }

    private static string[] Names(string folder, Func<string, IEnumerable<string>> enumerate)
    {
        try
        {
            return Directory.Exists(folder) ? enumerate(folder).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
