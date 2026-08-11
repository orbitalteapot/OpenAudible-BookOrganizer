using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

/// <summary>A throwaway source/destination pair on disk for the copy tests.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "oabo-tests", Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "source");
        Destination = Path.Combine(Root, "destination");
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    public string Root { get; }
    public string Source { get; }
    public string Destination { get; }

    /// <summary>Writes a file into the source folder and returns its full path.</summary>
    public string WriteSourceFile(string relativeName, string content = "audio")
    {
        var path = Path.Combine(Source, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteDestinationFile(string relativePath, string content)
    {
        var path = Path.Combine(Destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>All files under the destination, as paths relative to it, using '/' separators.</summary>
    public string[] DestinationFiles()
    {
        if (!Directory.Exists(Destination))
        {
            return [];
        }

        return Directory.GetFiles(Destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Destination, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public string[] DestinationDirectories()
    {
        if (!Directory.Exists(Destination))
        {
            return [];
        }

        return Directory.GetDirectories(Destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Destination, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Two equal-length contents that are identical in the first, middle and last 4 KB — the three
    /// windows the quick update check samples — and differ only in between. Stands in for an author
    /// re-issuing a book without changing its size.
    /// </summary>
    public static (string Original, string Edited) SameSizeEditedPair()
    {
        const int chunk = 4096;
        var head = new string('h', chunk);
        var middle = new string('m', chunk);
        var tail = new string('t', chunk);

        var original = head + new string('a', chunk * 4) + middle + new string('a', chunk * 4) + tail;
        var edited = head + new string('a', chunk * 2) + new string('b', chunk * 2) + middle + new string('a', chunk * 4) + tail;

        return (original, edited);
    }

    public static OpenAudible Book(
        string? title = "A Book",
        string? author = "An Author",
        string? filename = "a-book",
        string? shortTitle = null,
        string? seriesName = null,
        string? seriesSequence = null,
        string? m4b = "Yes",
        string? mp3 = null,
        string? pdf = null,
        string? filePaths = null)
    {
        return new OpenAudible
        {
            Title = title,
            Author = author,
            Filename = filename,
            ShortTitle = shortTitle,
            SeriesName = seriesName,
            SeriesSequence = seriesSequence,
            M4B = m4b,
            MP3 = mp3,
            PDF = pdf,
            FilePaths = filePaths
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }
}
