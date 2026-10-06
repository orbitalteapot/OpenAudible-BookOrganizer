using System.Collections.Immutable;

namespace AudioFileSorter.Model;

/// <summary>Where the organizer filed one book, as recorded in the <see cref="LibraryManifest"/>.</summary>
/// <param name="Folder">The book's folder, as a full path inside the destination.</param>
/// <param name="Files">The names of the book's files in <paramref name="Folder"/>: its audio, and its PDF when it has one.</param>
/// <param name="Title">The book as a person would name it, so someone reading the file can tell which book an entry is.</param>
public sealed record ManifestEntry(string Folder, IReadOnlyList<string> Files, string Title)
{
    /// <summary>
    /// What each of <see cref="Files"/> held when it went on record, by name. A name alone proves nothing:
    /// a record a sort could not save stays on disk as it was, and by the time it is read again another
    /// book's file may be under that name. A recorded file is the book's only while it still holds this
    /// (see <see cref="LibraryManifest.IsRecorded"/>). <see cref="LibraryManifest.Set"/> takes what a
    /// file holds at the time for each file it is given no stamp for.
    /// </summary>
    public IReadOnlyDictionary<string, ContentStamp> Stamps { get; init; } = ImmutableDictionary<string, ContentStamp>.Empty;
}

/// <summary>
/// What a file holds as the quick content check sees it (see <see cref="FileComparison.AreSameQuick"/>):
/// its size and a hash of the chunks that check reads. Two files with the same stamp are the same to it.
/// </summary>
/// <param name="Size">The file's length in bytes.</param>
/// <param name="Sample">A SHA-256 of the sampled chunks, in lower-case hex.</param>
public readonly record struct ContentStamp(long Size, string Sample);
