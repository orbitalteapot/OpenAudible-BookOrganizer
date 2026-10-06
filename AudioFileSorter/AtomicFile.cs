namespace AudioFileSorter;

/// <summary>Replaces small files the app keeps (its settings, the library's manifest) without ever leaving half of one.</summary>
public static class AtomicFile
{
    /// <summary>
    /// Written beside the target, flushed to the disk and renamed over it, so a crash or a power cut
    /// leaves either the old file or the new one, never half of one.
    /// </summary>
    /// <param name="write">Writes the new content to the stream it is given.</param>
    /// <exception cref="IOException">The file could not be written; the old one is untouched.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be written; the old file is untouched.</exception>
    public static void Write(string path, Action<Stream> write)
    {
        var partialPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next write replaces it.
        }
    }
}
