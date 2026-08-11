using System.Text.RegularExpressions;
using AudioFileSorter.Model;

namespace AudioFileSorter;

/// <summary>
/// Derives the folder and file names for a book from its OpenAudible metadata.
/// Pure functions only: nothing here touches the filesystem and nothing mutates the
/// <see cref="OpenAudible"/> instance it is given, so the parsed library stays exactly as it was
/// read from the CSV even after a sort has run.
/// </summary>
public static class BookNaming
{
    private static readonly string[] ContributorDescriptors =
    [
        "foreword", "afterword", "editor", "contributor", "adaptation", "music",
        "translator", "translatoreditor", "introduction", "preface", "illustrator"
    ];

    private static readonly string[] KnownNonAuthorSegments =
        ["the great courses", "crystal lake publishing", "crystal lake audio"];

    private static readonly Regex SequenceValueRegex =
        new(@"(?<value>\d+(?:\.\d+)?(?:-\d+(?:\.\d+)?)?)", RegexOptions.Compiled);

    public const string UnknownAuthor = "Unknown";
    public const string UnknownTitle = "Unknown";

    /// <summary>Builds the naming plan for a book without modifying the book itself.</summary>
    public static BookSortPlan BuildPlan(OpenAudible book)
    {
        ArgumentNullException.ThrowIfNull(book);

        var seriesName = PathSanitizer.SanitizeSegment(book.SeriesName);

        return new BookSortPlan
        {
            Author = ResolveAuthor(book.Author),
            SeriesName = seriesName,
            // A sequence without a series has nowhere to live, so drop it rather than creating
            // a stray "Book 3" folder directly under the author.
            SeriesSequence = seriesName is null ? null : ResolveSeriesSequence(book.SeriesSequence),
            FileStem = ResolveFileStem(book)
        };
    }

    /// <summary>
    /// Cleans an author cell, which in OpenAudible exports frequently carries contributors
    /// ("Jane Doe, John Roe - translator") and publisher noise alongside the real author.
    /// </summary>
    public static string ResolveAuthor(string? rawAuthor)
    {
        var sanitized = PathSanitizer.SanitizeSegment(rawAuthor);
        if (sanitized is null)
        {
            return UnknownAuthor;
        }

        var authorSegments = new List<string>();
        foreach (var rawSegment in sanitized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment.Trim();
            var namePart = segment;
            string? descriptorPart = null;

            var separatorIndex = segment.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                namePart = segment[..separatorIndex].Trim();
                descriptorPart = segment[(separatorIndex + 3)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(descriptorPart) && IsContributorDescriptor(descriptorPart))
            {
                if (authorSegments.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (authorSegments.Count > 0 && IsKnownNonAuthorSegment(namePart))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(namePart))
            {
                authorSegments.Add(namePart);
            }
        }

        if (authorSegments.Count == 0)
        {
            return UnknownAuthor;
        }

        var joined = string.Join(", ", authorSegments.Distinct(StringComparer.OrdinalIgnoreCase));

        // Joining segments can push the value back over the segment budget.
        return PathSanitizer.SanitizeSegment(joined) ?? UnknownAuthor;
    }

    /// <summary>Extracts the numeric part of a series sequence ("Book 3", "3.5", "2-3").</summary>
    public static string? ResolveSeriesSequence(string? rawSequence)
    {
        var sanitized = PathSanitizer.SanitizeSegment(rawSequence);
        if (sanitized is null)
        {
            return null;
        }

        if (sanitized.StartsWith("Book ", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized[5..].Trim();
        }

        var match = SequenceValueRegex.Match(sanitized);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>
    /// Picks the base name for the copied file. Falling through Short Title, Title and the
    /// source file name matters: when an export has no "Short Title" column every book would
    /// otherwise be written as "Unknown", and each one would overwrite the last.
    /// </summary>
    public static string ResolveFileStem(OpenAudible book)
    {
        ArgumentNullException.ThrowIfNull(book);

        var fileNameStem = string.IsNullOrWhiteSpace(book.Filename)
            ? null
            : Path.GetFileNameWithoutExtension(book.Filename.Trim());

        return PathSanitizer.SanitizeSegmentOrFallback(
            UnknownTitle,
            book.ShortTitle,
            book.Title,
            fileNameStem,
            book.ASIN);
    }

    private static bool IsContributorDescriptor(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return ContributorDescriptors.Any(normalized.Contains);
    }

    private static bool IsKnownNonAuthorSegment(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return Array.IndexOf(KnownNonAuthorSegments, normalized) >= 0;
    }
}
