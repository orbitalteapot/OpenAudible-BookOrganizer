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
    /// Layout of <see cref="SameSizeEditedPair"/>, in 4 KB blocks. The quick update check samples
    /// the first block, the block straddling the midpoint, and the last block:
    ///
    ///   block:   0      1  2  3  4      5       6  7  8  9     10
    ///   content: head   a  a  a  a      middle  a  a  a  a     tail
    ///   sampled: yes                    yes                    yes
    ///
    /// Total length is 11 * 4096 = 45056, so the middle sample covers bytes 20480..24575 — exactly
    /// block 5. Blocks 1..4 and 6..9 are never read by the quick check.
    /// </summary>
    public enum SampleWindow
    {
        /// <summary>Block 0, bytes 0..4095.</summary>
        Head,

        /// <summary>Block 5, bytes 20480..24575.</summary>
        Middle,

        /// <summary>Block 10, bytes 40960..45055.</summary>
        Tail
    }

    private const int SampleChunk = 4096;

    /// <summary>
    /// Two equal-length contents that are identical in the first, middle and last 4 KB — the three
    /// windows the quick update check samples — and differ only in between. Stands in for an author
    /// re-issuing a book without changing its size.
    /// </summary>
    public static (string Original, string Edited) SameSizeEditedPair()
    {
        var blocks = OriginalBlocks();

        // Edit blocks 3 and 4: inside the untouched span, and two whole blocks clear of both the
        // head sample (block 0) and the middle sample (block 5).
        var edited = (string[])blocks.Clone();
        edited[3] = new string('b', SampleChunk);
        edited[4] = new string('b', SampleChunk);

        return (string.Concat(blocks), string.Concat(edited));
    }

    /// <summary>
    /// The same pair, but with the edit placed inside one of the windows the quick check samples,
    /// so quick mode is expected to notice it.
    /// </summary>
    public static (string Original, string Edited) SameSizeEditInWindow(SampleWindow window)
    {
        var blocks = OriginalBlocks();
        var edited = (string[])blocks.Clone();

        var index = window switch
        {
            SampleWindow.Head => 0,
            SampleWindow.Middle => 5,
            SampleWindow.Tail => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(window))
        };

        edited[index] = new string('b', SampleChunk);
        return (string.Concat(blocks), string.Concat(edited));
    }

    private static string[] OriginalBlocks()
    {
        return
        [
            new string('h', SampleChunk),
            new string('a', SampleChunk), new string('a', SampleChunk),
            new string('a', SampleChunk), new string('a', SampleChunk),
            new string('m', SampleChunk),
            new string('a', SampleChunk), new string('a', SampleChunk),
            new string('a', SampleChunk), new string('a', SampleChunk),
            new string('t', SampleChunk)
        ];
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
