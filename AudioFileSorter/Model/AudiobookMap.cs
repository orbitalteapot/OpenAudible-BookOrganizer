using CsvHelper.Configuration;

namespace AudioFileSorter.Model;

/// <summary>
/// Maps OpenAudible export columns onto <see cref="OpenAudible"/>.
///
/// Every column is optional. Exports vary between OpenAudible versions and platforms, and a
/// missing column should degrade the result for that one field rather than fail the import; the
/// parser checks separately that the file looks like a book list at all.
/// </summary>
public sealed class AudiobookMap : ClassMap<OpenAudible>
{
    public AudiobookMap()
    {
        Map(m => m.Key).Name("Key").Optional();
        Map(m => m.Title).Name("Title").Optional();
        Map(m => m.Author).Name("Author").Optional();
        Map(m => m.Filename).Name("File name", "Filename", "File Name").Optional();
        Map(m => m.FilePaths).Name("File Paths", "File paths", "FilePaths").Optional();
        Map(m => m.AudibleAAX).Name("Audible (AAX)").Optional();

        Map(m => m.NarratedBy).Name("Narrated By").Optional();
        Map(m => m.Duration).Name("Duration").Optional();
        Map(m => m.Genre).Name("Genre").Optional();
        Map(m => m.SeriesName).Name("Series Name").Optional();
        Map(m => m.SeriesSequence).Name("Series Sequence").Optional();
        Map(m => m.ProductID).Name("Product ID").Optional();
        Map(m => m.ASIN).Name("ASIN").Optional();
        Map(m => m.BookURL).Name("Book URL").Optional();
        Map(m => m.Summary).Name("Summary").Optional();
        Map(m => m.Description).Name("Description").Optional();
        Map(m => m.Publisher).Name("Publisher").Optional();
        Map(m => m.ShortTitle).Name("Short Title").Optional();
        Map(m => m.Copyright).Name("Copyright").Optional();
        Map(m => m.AuthorURL).Name("Author URL").Optional();
        Map(m => m.SeriesURL).Name("Series URL").Optional();
        Map(m => m.Abridged).Name("Abridged").Optional();
        Map(m => m.Language).Name("Language").Optional();
        Map(m => m.PDFURL).Name("PDF URL").Optional();
        Map(m => m.ImageURL).Name("Image URL").Optional();
        Map(m => m.Region).Name("Region").Optional();
        Map(m => m.ReadStatus).Name("Read Status").Optional();
        Map(m => m.UserID).Name("User ID").Optional();
        Map(m => m.Image).Name("Image").Optional();
        Map(m => m.M4B).Name("M4B").Optional();
        Map(m => m.MP3).Name("MP3").Optional();
        Map(m => m.PDF).Name("PDF").Optional();

        // Typed columns get converters that fall back to a default instead of throwing.
        Map(m => m.PurchaseDate).Name("Purchase Date").TypeConverter<LenientDateTimeConverter>().Optional();
        Map(m => m.ReleaseDate).Name("Release Date").TypeConverter<LenientDateTimeConverter>().Optional();
        Map(m => m.AveRating).Name("Ave. Rating", "Ave Rating", "Average Rating").TypeConverter<LenientDoubleConverter>().Optional();
        Map(m => m.RatingCount).Name("Rating Count").TypeConverter<LenientInt32Converter>().Optional();
        Map(m => m.AYCE).Name("AYCE").TypeConverter<LenientBooleanConverter>().Optional();
    }
}
