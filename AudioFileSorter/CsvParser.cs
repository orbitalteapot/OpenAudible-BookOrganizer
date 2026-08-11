using System.Globalization;
using System.Text;
using AudioFileSorter.Model;
using CsvHelper;
using CsvHelper.Configuration;

namespace AudioFileSorter;

/// <summary>Reads an OpenAudible book list export.</summary>
public class CsvParser
{
    private const int MaxReportedWarnings = 50;

    /// <summary>Columns that identify a file as an OpenAudible export.</summary>
    private static readonly string[] RecognisedHeaders =
        ["title", "author", "file name", "filename", "file paths", "asin", "short title"];

    /// <summary>Parses a CSV export, skipping rows that cannot be read.</summary>
    /// <exception cref="ArgumentException">No path was supplied.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not an OpenAudible export.</exception>
    public async Task<List<OpenAudible>> ParseDataCsv(string? fullPath, CancellationToken token)
    {
        var result = await ParseAsync(fullPath, token);
        return result.Books;
    }

    /// <summary>
    /// Parses a CSV export and reports what had to be skipped, so the caller can tell the
    /// difference between "empty library" and "nothing in this file could be read".
    /// </summary>
    public async Task<CsvParseResult> ParseAsync(string? fullPath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"CSV file not found: {fullPath}", fullPath);
        }

        var warnings = new List<string>();
        var skippedRows = 0;

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // OpenAudible has shipped comma and tab separated exports over the years.
            DetectDelimiter = true,
            DetectDelimiterValues = [",", "\t", ";", "|"],
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = args =>
                AddWarning(warnings, $"Malformed data on row {args.Context.Parser?.Row ?? 0} was read as-is.")
        };

        // A UTF-8 BOM in front of the first header would otherwise turn "Title" into "﻿Title"
        // and silently drop the column.
        using var reader = new StreamReader(fullPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, configuration);
        csv.Context.RegisterClassMap<AudiobookMap>();

        var books = new List<OpenAudible>();

        try
        {
            if (!await csv.ReadAsync() || !csv.ReadHeader())
            {
                throw new InvalidDataException("The CSV file is empty or has no header row.");
            }

            EnsureLooksLikeOpenAudibleExport(csv.HeaderRecord);

            while (await csv.ReadAsync())
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    books.Add(csv.GetRecord<OpenAudible>());
                }
                catch (Exception ex) when (ex is CsvHelperException or FormatException)
                {
                    // One unreadable row must not cost the user the other several hundred.
                    skippedRows++;
                    AddWarning(warnings, $"Row {csv.Context.Parser?.Row ?? 0} could not be read and was skipped: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CsvHelperException ex)
        {
            throw new InvalidDataException($"The CSV file could not be read: {ex.Message}", ex);
        }

        return new CsvParseResult
        {
            Books = books,
            SkippedRows = skippedRows,
            Warnings = warnings
        };
    }

    private static void EnsureLooksLikeOpenAudibleExport(string[]? headerRecord)
    {
        var headers = headerRecord ?? [];
        var recognised = headers.Any(header =>
            !string.IsNullOrWhiteSpace(header) &&
            RecognisedHeaders.Contains(header.Trim().ToLowerInvariant()));

        if (!recognised)
        {
            throw new InvalidDataException(
                "This file does not look like an OpenAudible export: none of the expected columns " +
                "(Title, Author, File name, File Paths) were found. Export your library from " +
                "OpenAudible with File > Export and try again.");
        }
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        if (warnings.Count < MaxReportedWarnings)
        {
            warnings.Add(message);
        }
    }
}

/// <summary>Outcome of reading a book list export.</summary>
public sealed class CsvParseResult
{
    public List<OpenAudible> Books { get; init; } = [];

    /// <summary>Rows that could not be read and were skipped.</summary>
    public int SkippedRows { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
