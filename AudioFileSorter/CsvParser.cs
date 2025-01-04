using System.Collections;
using System.Globalization;
using AudioFileSorter.Model;
using CsvHelper;
using CsvHelper.Configuration;
using System.Linq;


namespace AudioFileSorter;

public class CsvParser
{
    public async Task<List<OpenAudible>> ParseDataCsv(string? fullPath, CancellationToken token)
    {
        using var reader = new StreamReader(fullPath ?? throw new ArgumentNullException(nameof(fullPath)));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        try
        {
            var result = new List<OpenAudible>();
            csv.Context.RegisterClassMap<AudiobookMap>();
            await foreach (var openAudible in csv.GetRecordsAsync<OpenAudible>(token))
            {
                result.Add(openAudible);
            }
            return result;
        }
        catch (CsvHelperException ex)
        {
            Console.WriteLine($"CSV Parsing Error: {ex.Message}");
            throw;
        }
        finally
        {
            reader.Close();
        }
    }
}