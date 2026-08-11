using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class BookNamingTests
{
    [Theory]
    [InlineData("Brandon Sanderson", "Brandon Sanderson")]
    [InlineData("Brandon Sanderson, Jane Doe", "Brandon Sanderson, Jane Doe")]
    [InlineData("Brandon Sanderson, Brandon Sanderson", "Brandon Sanderson")]
    [InlineData("Jane Doe - translator", "Unknown")]
    [InlineData("Brandon Sanderson, Jane Doe - translator", "Brandon Sanderson")]
    [InlineData("Brandon Sanderson, The Great Courses", "Brandon Sanderson")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    [InlineData("Unknown", "Unknown")]
    public void ResolveAuthor_strips_contributors_and_publisher_noise(string? input, string expected)
    {
        Assert.Equal(expected, BookNaming.ResolveAuthor(input));
    }

    [Fact]
    public void ResolveAuthor_stays_within_the_segment_budget()
    {
        var manyAuthors = string.Join(", ", Enumerable.Range(0, 50).Select(i => $"Author Number {i}"));

        var resolved = BookNaming.ResolveAuthor(manyAuthors);

        Assert.True(resolved.Length <= PathSanitizer.MaxSegmentLength);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("Book 3", "3")]
    [InlineData("book 3", "3")]
    [InlineData("3.5", "3.5")]
    [InlineData("2-3", "2-3")]
    [InlineData("Volume 7 of the saga", "7")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("N/A", null)]
    [InlineData("no numbers here", null)]
    public void ResolveSeriesSequence_extracts_the_numeric_part(string? input, string? expected)
    {
        Assert.Equal(expected, BookNaming.ResolveSeriesSequence(input));
    }

    [Fact]
    public void BuildPlan_prefers_short_title_for_the_file_name()
    {
        var plan = BookNaming.BuildPlan(TempWorkspace.Book(title: "Long Title: A Story", shortTitle: "Long Title"));

        Assert.Equal("Long Title", plan.FileStem);
    }

    [Fact]
    public void BuildPlan_falls_back_to_the_title_when_there_is_no_short_title()
    {
        var plan = BookNaming.BuildPlan(TempWorkspace.Book(title: "Only Title", shortTitle: null));

        Assert.Equal("Only Title", plan.FileStem);
    }

    [Fact]
    public void BuildPlan_falls_back_to_the_source_file_name_before_giving_up()
    {
        // An export without Title or Short Title columns used to name every single book
        // "Unknown", so each book overwrote the one before it.
        var plan = BookNaming.BuildPlan(TempWorkspace.Book(title: null, shortTitle: null, filename: "the-hobbit"));

        Assert.Equal("the-hobbit", plan.FileStem);
    }

    [Fact]
    public void BuildPlan_drops_a_sequence_that_has_no_series()
    {
        var plan = BookNaming.BuildPlan(TempWorkspace.Book(seriesName: null, seriesSequence: "3"));

        Assert.Null(plan.SeriesName);
        Assert.Null(plan.SeriesSequence);
    }

    [Fact]
    public void BuildPlan_does_not_modify_the_book_it_is_given()
    {
        // The parsed library is a shared singleton in the API; sorting must not rewrite it.
        var book = TempWorkspace.Book(
            title: "A Title: With Punctuation",
            author: "Jane Doe, John Roe - translator",
            seriesName: "The Series",
            seriesSequence: "Book 2",
            shortTitle: "A Title");

        BookNaming.BuildPlan(book);

        Assert.Equal("A Title: With Punctuation", book.Title);
        Assert.Equal("Jane Doe, John Roe - translator", book.Author);
        Assert.Equal("The Series", book.SeriesName);
        Assert.Equal("Book 2", book.SeriesSequence);
        Assert.Equal("A Title", book.ShortTitle);
    }

    [Fact]
    public void BuildPlan_never_returns_an_empty_author_or_file_stem()
    {
        var plan = BookNaming.BuildPlan(new OpenAudible());

        Assert.False(string.IsNullOrWhiteSpace(plan.Author));
        Assert.False(string.IsNullOrWhiteSpace(plan.FileStem));
    }
}
