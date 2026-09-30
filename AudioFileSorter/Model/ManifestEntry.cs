namespace AudioFileSorter.Model;

/// <summary>Where the organizer filed one book, as recorded in the <see cref="LibraryManifest"/>.</summary>
/// <param name="Folder">The book's folder, as a full path inside the destination.</param>
/// <param name="Files">The names of the book's files in <paramref name="Folder"/>: its audio, and its PDF when it has one.</param>
/// <param name="Title">The book as a person would name it, so someone reading the file can tell which book an entry is.</param>
public sealed record ManifestEntry(string Folder, IReadOnlyList<string> Files, string Title);
