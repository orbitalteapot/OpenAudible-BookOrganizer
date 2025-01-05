using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace AudioFileSorter.Model;

public sealed class AudiobookMap : ClassMap<OpenAudible>
{
    public AudiobookMap()
    {
        // Mandatory Fields
        Map(m => m.Key).Name("Key").Optional();
        Map(m => m.Title).Name("Title");
        Map(m => m.Author).Name("Author");
        Map(m => m.Filename).Name("File name");
        Map(m => m.FilePaths).Name("File Paths");
        Map(m => m.AudibleAAX).Name("Audible (AAX)").Optional();

        // Optional Fields
        Map(m => m.NarratedBy).Name("Narrated By").Optional();
        Map(m => m.PurchaseDate).Name("Purchase Date").TypeConverterOption.Format("yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy");
        Map(m => m.Duration).Name("Duration").Optional();
        Map(m => m.ReleaseDate).Name("Release Date").TypeConverterOption.Format("yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy");
        Map(m => m.AveRating).Name("Ave. Rating").Optional().Default(0.0);
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
        Map(m => m.AYCE).Name("AYCE").TypeConverter<BooleanConverter>().Optional().Default(false);
        Map(m => m.RatingCount).Name("Rating Count").TypeConverter<Int32Converter>().Optional().Default(0);

        // **Newly Added Field**
        Map(m => m.PDF).Name("PDF").Optional();
    }
}