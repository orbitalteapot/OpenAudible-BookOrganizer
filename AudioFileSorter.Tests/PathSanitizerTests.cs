namespace AudioFileSorter.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("Harry Potter", "Harry Potter")]
    [InlineData("  Harry   Potter  ", "Harry Potter")]
    [InlineData("Book: The Sequel", "Book The Sequel")]
    [InlineData("What?", "What")]
    [InlineData("A/B", "AB")]
    [InlineData("A\\B", "AB")]
    [InlineData("Star * Wars", "Star Wars")]
    [InlineData("Quote\"Test", "QuoteTest")]
    [InlineData("Pipe|Test", "PipeTest")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("Trailing space   ", "Trailing space")]
    public void SanitizeSegment_removes_characters_that_are_invalid_on_any_supported_platform(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeSegment(input));
    }

    [Theory]
    [InlineData("Line\nBreak", "Line Break")]
    [InlineData("Tab\tSeparated", "Tab Separated")]
    public void SanitizeSegment_turns_control_characters_into_a_single_space(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeSegment(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("N/A")]
    [InlineData("none")]
    [InlineData("null")]
    [InlineData("...")]
    [InlineData("///")]
    public void SanitizeSegment_returns_null_for_values_that_carry_no_information(string? input)
    {
        Assert.Null(PathSanitizer.SanitizeSegment(input));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../..")]
    [InlineData("..\\..")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32")]
    public void SanitizeSegment_never_produces_a_traversal_segment(string input)
    {
        var sanitized = PathSanitizer.SanitizeSegment(input);

        Assert.True(sanitized is null || (sanitized != ".." && sanitized != "." && !sanitized.Contains('/') && !sanitized.Contains('\\')));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("aux.txt")]
    public void SanitizeSegment_escapes_reserved_windows_device_names(string input)
    {
        var sanitized = PathSanitizer.SanitizeSegment(input);

        Assert.NotNull(sanitized);
        Assert.StartsWith("_", sanitized);
    }

    [Fact]
    public void SanitizeSegment_keeps_ordinary_names_that_merely_start_with_a_device_name()
    {
        Assert.Equal("Conan the Barbarian", PathSanitizer.SanitizeSegment("Conan the Barbarian"));
    }

    [Fact]
    public void SanitizeSegment_truncates_a_name_that_would_be_rejected_by_the_filesystem()
    {
        var sanitized = PathSanitizer.SanitizeSegment(new string('a', 500));

        Assert.NotNull(sanitized);
        Assert.Equal(PathSanitizer.MaxSegmentLength, sanitized!.Length);
    }

    [Fact]
    public void SanitizeSegment_respects_the_byte_budget_for_multi_byte_names()
    {
        // Each of these characters is three bytes in UTF-8.
        var sanitized = PathSanitizer.SanitizeSegment(new string('あ', 300));

        Assert.NotNull(sanitized);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sanitized!) <= PathSanitizer.MaxSegmentBytes);
    }

    [Fact]
    public void SanitizeSegment_does_not_split_a_surrogate_pair()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 200));

        var sanitized = PathSanitizer.SanitizeSegment(emoji);

        Assert.NotNull(sanitized);
        Assert.False(char.IsHighSurrogate(sanitized![^1]));
    }

    [Theory]
    [InlineData("J.K. Rowling", "JK Rowling")]
    [InlineData("J K Rowling", "jkrowling")]
    [InlineData("Brandon  Sanderson", "brandonsanderson")]
    public void NormalizeComparisonKey_ignores_punctuation_and_case(string first, string second)
    {
        Assert.Equal(PathSanitizer.NormalizeComparisonKey(first), PathSanitizer.NormalizeComparisonKey(second));
    }

    [Theory]
    [InlineData("The Wheel of Time", "Wheel of Time")]
    [InlineData("The Wheel of Time", "Wheel of Time Series")]
    [InlineData("A Song of Ice and Fire", "Song of Ice and Fire Saga")]
    public void NormalizeSeriesKey_ignores_articles_and_decorators(string first, string second)
    {
        Assert.Equal(PathSanitizer.NormalizeSeriesKey(first), PathSanitizer.NormalizeSeriesKey(second));
    }

    [Fact]
    public void NormalizeSeriesKey_keeps_distinct_series_distinct()
    {
        Assert.NotEqual(PathSanitizer.NormalizeSeriesKey("Mistborn"), PathSanitizer.NormalizeSeriesKey("Stormlight"));
    }

    [Fact]
    public void IsWithin_accepts_a_path_inside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "oabo-root");

        Assert.True(PathSanitizer.IsWithin(root, Path.Combine(root, "Author", "Book.m4b")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../sibling")]
    public void IsWithin_rejects_a_path_that_escapes_the_root(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "oabo-root");

        Assert.False(PathSanitizer.IsWithin(root, Path.Combine(root, relative)));
    }

    [Fact]
    public void IsWithin_rejects_the_root_itself()
    {
        var root = Path.Combine(Path.GetTempPath(), "oabo-root");

        Assert.False(PathSanitizer.IsWithin(root, root));
    }

    [Fact]
    public void IsWithin_rejects_a_sibling_with_the_same_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "library");

        Assert.False(PathSanitizer.IsWithin(root, Path.Combine(Path.GetTempPath(), "library-backup", "book.m4b")));
    }
}
