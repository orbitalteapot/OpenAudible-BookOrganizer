using System.Collections;
using System.Globalization;
using AudioFileSorter.Model;
using CsvHelper;
using CsvHelper.Configuration;

namespace AudioFileSorter;

public class CsvParser
{
    public async Task<List<OpenAudible>> ParseDataCsv(string fullPath, CancellationToken token)
    {
        using var reader = new StreamReader(fullPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        try
        {
            csv.Context.RegisterClassMap<AudiobookMap>();
            return await csv.GetRecordsAsync<OpenAudible>(token).ToListAsync(token);
        }
        finally
        {
            reader.Close();
        }
    }
}