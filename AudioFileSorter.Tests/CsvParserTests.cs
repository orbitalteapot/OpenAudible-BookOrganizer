using System.Text;

namespace AudioFileSorter.Tests;

public class CsvParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oabo-csv-tests", Guid.NewGuid().ToString("N"));

    public CsvParserTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Parses_a_standard_export()
    {
        var path = WriteCsv(
            "Title,Author,File name,File Paths,Series Name,Series Sequence,Short Title,M4B",
            "The Hobbit,J.R.R. Tolkien,the-hobbit,,Middle Earth,1,The Hobbit,Yes");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Equal("J.R.R. Tolkien", book.Author);
        Assert.Equal("the-hobbit", book.Filename);
        Assert.Equal("Middle Earth", book.SeriesName);
        Assert.Equal(0, result.SkippedRows);
    }

    [Fact]
    public async Task Parses_an_export_with_only_the_minimum_columns()
    {
        // Older exports and hand-trimmed files omit most columns; that used to throw a header
        // validation error and lose the whole library.
        var path = WriteCsv(
            "Title,Author",
            "The Hobbit,J.R.R. Tolkien");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Single(result.Books);
        Assert.Equal("The Hobbit", result.Books[0].Title);
    }

    [Fact]
    public async Task Parses_an_export_with_empty_numeric_and_date_cells()
    {
        var path = WriteCsv(
            "Title,Author,File name,Purchase Date,Release Date,Ave. Rating,Rating Count,AYCE",
            "The Hobbit,Tolkien,the-hobbit,,,,,");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Null(book.PurchaseDate);
        Assert.Equal(0, book.AveRating);
        Assert.Equal(0, book.RatingCount);
        Assert.False(book.AYCE);
    }

    [Fact]
    public async Task Parses_an_export_with_unparseable_numeric_and_date_cells()
    {
        var path = WriteCsv(
            "Title,Author,File name,Purchase Date,Ave. Rating,Rating Count",
            "The Hobbit,Tolkien,the-hobbit,not-a-date,not-a-number,lots");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Null(book.PurchaseDate);
        Assert.Equal(0, book.AveRating);
        Assert.Equal(0, book.RatingCount);
    }

    [Theory]
    [InlineData("4.6", 4.6)]
    [InlineData("4,6", 4.6)]   // written the European way, as a spreadsheet on a European locale saves it
    [InlineData("4,65", 4.65)]
    [InlineData("5", 5.0)]
    [InlineData("0", 0.0)]
    [InlineData("", 0.0)]
    [InlineData("n/a", 0.0)]
    [InlineData("1,234", 0.0)] // three trailing digits is a group separator, not a decimal
    [InlineData("1,2,3", 0.0)]
    [InlineData(",6", 0.0)]
    [InlineData("4,", 0.0)]
    public async Task Reads_a_rating_however_the_decimal_point_is_written(string cell, double expected)
    {
        // The Rating column reading as zero shows every book as "—" and sinks the lot to the
        // bottom of that column, which looks like a sorting bug rather than an import one.
        var path = WriteCsv(
            "Title,Author,File name,Ave. Rating",
            $"The Hobbit,Tolkien,the-hobbit,\"{cell}\"");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(result.Books).AveRating);
    }

    [Fact]
    public async Task Reads_a_european_export_end_to_end()
    {
        // Semicolon separated with comma decimals: what a European spreadsheet writes.
        var path = WriteRaw(
            "Title;Author;File name;Ave. Rating;Rating Count\n" +
            "The Hobbit;Tolkien;the-hobbit;4,6;1234\n");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Equal(4.6, book.AveRating);
        Assert.Equal(1234, book.RatingCount);
    }

    [Theory]
    [InlineData("2020-05-04")]
    [InlineData("05/04/2020")]
    [InlineData("5/4/2020")]
    public async Task Accepts_the_date_formats_openaudible_has_shipped(string date)
    {
        var path = WriteCsv(
            "Title,Author,File name,Purchase Date",
            $"The Hobbit,Tolkien,the-hobbit,{date}");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.NotNull(Assert.Single(result.Books).PurchaseDate);
    }

    [Fact]
    public async Task Keeps_the_good_rows_when_one_row_is_short()
    {
        var path = WriteCsv(
            "Title,Author,File name,Series Name",
            "Book One,Author One,book-one,Series",
            "Book Two,Author Two",
            "Book Three,Author Three,book-three,Series");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(3, result.Books.Count);
        Assert.Equal("Book Two", result.Books[1].Title);
    }

    [Fact]
    public async Task Reads_a_tab_separated_export()
    {
        var path = WriteCsv(
            "Title\tAuthor\tFile name",
            "The Hobbit\tTolkien\tthe-hobbit");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal("The Hobbit", Assert.Single(result.Books).Title);
    }

    [Fact]
    public async Task Reads_a_file_that_starts_with_a_utf8_byte_order_mark()
    {
        var path = Path.Combine(_directory, "bom.csv");
        await File.WriteAllTextAsync(
            path,
            "Title,Author,File name\nThe Hobbit,Tolkien,the-hobbit\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal("The Hobbit", Assert.Single(result.Books).Title);
    }

    [Fact]
    public async Task Reads_quoted_fields_that_span_lines()
    {
        var path = WriteCsv(
            "Title,Author,File name,Summary",
            "\"The Hobbit\",Tolkien,the-hobbit,\"A long\nsummary, with a comma\"");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Contains("with a comma", book.Summary);
    }

    [Fact]
    public async Task Returns_an_empty_library_for_a_header_only_export()
    {
        var path = WriteCsv("Title,Author,File name");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Empty(result.Books);
    }

    [Fact]
    public async Task Rejects_a_file_that_is_not_an_openaudible_export()
    {
        var path = WriteCsv("alpha,beta,gamma", "1,2,3");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CsvParser().ParseAsync(path, CancellationToken.None));

        Assert.Contains("does not look like an OpenAudible export", exception.Message);
    }

    [Fact]
    public async Task Rejects_an_empty_file_with_a_clear_message()
    {
        var path = WriteCsv();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new CsvParser().ParseAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_a_missing_file_as_a_missing_file()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new CsvParser().ParseAsync(Path.Combine(_directory, "nope.csv"), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Reports_a_missing_path_as_an_argument_error(string? path)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => new CsvParser().ParseAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_an_export_with_windows_line_endings()
    {
        var path = WriteRaw("Title,Author\r\nThe Hobbit,J.R.R. Tolkien\r\nDune,Frank Herbert\r\n");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(2, result.Books.Count);
        Assert.Equal("J.R.R. Tolkien", result.Books[0].Author);
    }

    [Fact]
    public async Task Reads_a_semicolon_separated_export()
    {
        var path = WriteRaw("Title;Author;File name\nThe Hobbit;J.R.R. Tolkien;the-hobbit\n");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Equal("J.R.R. Tolkien", book.Author);
    }

    [Fact]
    public async Task Reads_headers_whatever_their_casing_and_spacing()
    {
        var path = WriteCsv(
            "  TITLE  ,  author ,  File Name  ",
            "The Hobbit,J.R.R. Tolkien,the-hobbit");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Equal("J.R.R. Tolkien", book.Author);
        Assert.Equal("the-hobbit", book.Filename);
    }

    [Fact]
    public async Task Keeps_the_good_rows_when_one_row_has_extra_fields()
    {
        var path = WriteCsv(
            "Title,Author",
            "The Hobbit,J.R.R. Tolkien",
            "Dune,Frank Herbert,stray,extra,fields",
            "Neuromancer,William Gibson");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(3, result.Books.Count);
        Assert.Equal(["The Hobbit", "Dune", "Neuromancer"], result.Books.Select(b => b.Title));
    }

    [Fact]
    public async Task Reads_a_utf16_export()
    {
        // Exported from a Windows tool that writes UTF-16; the byte order mark identifies it.
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "Title,Author\nHavamal,Snorri Sturluson\n", new UnicodeEncoding(false, true));

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        var book = Assert.Single(result.Books);
        Assert.Equal("Havamal", book.Title);
    }

    [Fact]
    public async Task Keeps_non_ascii_titles_intact()
    {
        var path = WriteCsv(
            "Title,Author,File name",
            "\"日本語のタイトル\",\"著者\",jp-1",
            "\"عنوان عربي\",\"مؤلف\",ar-1",
            "\"Émile 🎧\",\"Zola\",fr-1");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(3, result.Books.Count);
        Assert.Equal("日本語のタイトル", result.Books[0].Title);
        Assert.Equal("عنوان عربي", result.Books[1].Title);
        Assert.Equal("Émile 🎧", result.Books[2].Title);
    }

    [Fact]
    public async Task Reads_an_export_with_no_trailing_newline()
    {
        var path = WriteRaw("Title,Author\nThe Hobbit,J.R.R. Tolkien");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Single(result.Books);
    }

    [Fact]
    public async Task Skips_blank_lines_in_the_middle_of_an_export()
    {
        var path = WriteRaw("Title,Author\nThe Hobbit,J.R.R. Tolkien\n\n\nDune,Frank Herbert\n");

        var result = await new CsvParser().ParseAsync(path, CancellationToken.None);

        Assert.Equal(2, result.Books.Count);
        Assert.Equal(0, result.SkippedRows);
    }

    private string WriteRaw(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Join("\n", lines) + (lines.Length > 0 ? "\n" : string.Empty));
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder cleanup is best effort.
        }
    }
}
