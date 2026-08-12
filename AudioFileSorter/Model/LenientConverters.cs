using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace AudioFileSorter.Model;

/// <summary>
/// Converters that treat an unparseable cell as "no value" rather than as a reason to abandon the
/// import. OpenAudible exports routinely contain blank ratings, blank dates and localised date
/// formats, and losing a whole library to one odd cell is the single most common import failure.
/// </summary>
internal static class LenientConverters
{
    internal static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd.MM.yyyy"
    ];
}

internal sealed class LenientDateTimeConverter : DefaultTypeConverter
{
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        if (DateTime.TryParseExact(value, LenientConverters.DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var localParsed)
            ? localParsed
            : null;
    }
}

internal sealed class LenientDoubleConverter : DefaultTypeConverter
{
    public override object ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        var value = text.Trim();

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return double.IsFinite(parsed) ? parsed : 0d;
        }

        return 0d;
    }
}

internal sealed class LenientInt32Converter : DefaultTypeConverter
{
    public override object ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var value = text.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble) &&
               asDouble is >= int.MinValue and <= int.MaxValue
            ? (int)asDouble
            : 0;
    }
}

internal sealed class LenientBooleanConverter : DefaultTypeConverter
{
    private static readonly string[] TruthyValues = ["true", "yes", "y", "1", "x"];

    public override object ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Array.IndexOf(TruthyValues, text.Trim().ToLowerInvariant()) >= 0;
    }
}
