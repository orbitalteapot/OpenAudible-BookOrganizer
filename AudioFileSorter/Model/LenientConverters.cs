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

        // A rating written the European way — "4,6". Neither parse above accepts it: NumberStyles
        // .Float does not allow a group separator, and the server's culture is whatever the
        // container happens to run with. Left alone, the whole Rating column of a European export
        // silently reads as zero, showing as "—" and sorting to the bottom.
        if (LooksLikeACommaDecimal(value) &&
            double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var commaDecimal))
        {
            return double.IsFinite(commaDecimal) ? commaDecimal : 0d;
        }

        return 0d;
    }

    /// <summary>
    /// One comma, no dot, a digit before it and one or two digits after — "4,6" and "4,65". Three
    /// trailing digits is the shape of a group separator ("1,234"), not of a decimal, and "1,2,3"
    /// is not a number at all; both read as no rating rather than as a wrong one.
    /// </summary>
    private static bool LooksLikeACommaDecimal(string value)
    {
        if (value.Contains('.') || value.Count(character => character == ',') != 1)
        {
            return false;
        }

        var comma = value.IndexOf(',');
        if (comma <= 0 || !char.IsAsciiDigit(value[comma - 1]))
        {
            return false;
        }

        var fraction = value[(comma + 1)..];
        return fraction.Length is 1 or 2 && fraction.All(char.IsAsciiDigit);
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
