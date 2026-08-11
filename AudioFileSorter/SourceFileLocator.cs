using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Finds the files belonging to a book inside the source folder. OpenAudible exports describe the
/// same file in several ways depending on version and platform, so every plausible spelling is
/// tried before a book is reported as missing.
/// </summary>
public static class SourceFileLocator
{
    private static readonly string[] AudioExtensions = [".m4b", ".mp3", ".m4a"];

    /// <summary>Locates the audio file for a book, or null when none of the candidates exist.</summary>
    public static string? FindAudioFile(OpenAudible book, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(book);

        foreach (var candidate in GetAudioCandidates(book, sourceRoot))
        {
            if (FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Locates the companion PDF for a book, or null when there is none.</summary>
    public static string? FindPdfFile(OpenAudible book, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(book);

        foreach (var candidate in GetPdfCandidates(book, sourceRoot))
        {
            if (FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetAudioCandidates(OpenAudible book, string sourceRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The M4B/MP3 columns say which format was downloaded, so honour them first to keep the
        // extension of an already-organised library stable.
        foreach (var (marker, extension) in new[] { (book.M4B, ".m4b"), (book.MP3, ".mp3") })
        {
            if (string.IsNullOrWhiteSpace(marker) || string.IsNullOrWhiteSpace(book.Filename))
            {
                continue;
            }

            foreach (var candidate in ExpandSourceCandidates($"{book.Filename.Trim()}{extension}", sourceRoot))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var filePath in EnumerateFilePaths(book.FilePaths))
        {
            if (!IsAudioExtension(Path.GetExtension(filePath)))
            {
                continue;
            }

            foreach (var candidate in ExpandSourceCandidates(filePath, sourceRoot))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        // Last resort: probe the source folder directly. Exports produced by older OpenAudible
        // versions leave the format columns empty even though the file is right there.
        if (!string.IsNullOrWhiteSpace(book.Filename))
        {
            foreach (var extension in AudioExtensions)
            {
                foreach (var candidate in ExpandSourceCandidates($"{book.Filename.Trim()}{extension}", sourceRoot))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> GetPdfCandidates(OpenAudible book, string sourceRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in EnumerateFilePaths(book.FilePaths))
        {
            if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in ExpandSourceCandidates(filePath, sourceRoot))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var rawValue in new[] { book.PDF, book.Filename })
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            foreach (var candidate in ExpandSourceCandidates(value, sourceRoot))
            {
                if (seen.Add(candidate) && string.Equals(Path.GetExtension(candidate), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }

            if (string.Equals(Path.GetExtension(value), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in ExpandSourceCandidates($"{value}.pdf", sourceRoot))
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilePaths(string? rawFilePaths)
    {
        if (string.IsNullOrWhiteSpace(rawFilePaths))
        {
            yield break;
        }

        var entries = rawFilePaths.Split(
            ['|', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var candidate = entry.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandSourceCandidates(string value, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var trimmed = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        if (Path.IsPathRooted(trimmed))
        {
            yield return trimmed;
        }
        else
        {
            yield return Path.Combine(sourceRoot, trimmed);
        }

        // The recorded path is where the file was when the export was written, which is very
        // often not where it is now: a different machine, a different drive letter, or a path
        // written on Windows and read on Linux. Fall back to the bare file name inside the
        // source folder the user actually chose.
        var fileName = GetPortableFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, trimmed, StringComparison.Ordinal))
        {
            yield return Path.Combine(sourceRoot, fileName);
        }
    }

    private static string GetPortableFileName(string value)
    {
        var separatorIndex = value.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? value[(separatorIndex + 1)..] : value;
    }

    private static bool IsAudioExtension(string? extension)
    {
        return !string.IsNullOrEmpty(extension) &&
               AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // A malformed candidate is simply not a match; it must never abort the whole sort.
            return false;
        }
    }
}
