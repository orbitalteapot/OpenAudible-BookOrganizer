using System.CommandLine;
using AudioFileSorter;

var sourceOption = new Option<string?>(
    name: "--source",
    description: "Full path to the source folder containing audio files");

var csvOption = new Option<string?>(
    name: "--csv",
    description: "Full path to the OpenAudible book export file (.csv)");

var destinationOption = new Option<string?>(
    name: "--destination",
    description: "Full path to the destination folder for organized audiobooks");

var rootCommand = new RootCommand("OpenAudible Book Organizer - Sorts audiobooks into a structured format based on metadata.")
{
    sourceOption,
    csvOption,
    destinationOption
};

// Enable help option (-h / --help)
rootCommand.Description = "Organizes audiobooks into a structured format using OpenAudible metadata.\n\n" +
                          "Usage:\n" +
                          "  OpenAudibleOrganizer --source \"C:\\Audiobooks\" --csv \"C:\\mybooks.csv\" --destination \"C:\\SortedAudiobooks\"\n\n" +
                          "Options:\n" +
                          "  --source       Path to the source audiobook folder\n" +
                          "  --csv          Path to the OpenAudible export CSV file\n" +
                          "  --destination  Path to the destination folder for organized audiobooks\n" +
                          "  -h, --help     Show help information";

rootCommand.SetHandler(async (string? source, string? csv, string? destination) =>
{
    Console.WriteLine("\nOpenAudible Book Organizer\n");

    // Prompt for missing values
    source = EnsureValue(source, "Enter the full path to the source folder: ");
    csv = EnsureValue(csv, "Enter the full path to the OpenAudible book export file: ");
    destination = EnsureValue(destination, "Enter the full path to the destination folder: ");

    // Validate input paths
    if (!Directory.Exists(source))
    {
        Console.WriteLine($"Error: Source folder '{source}' does not exist.");
        return;
    }
    if (!File.Exists(csv))
    {
        Console.WriteLine($"Error: CSV file '{csv}' not found.");
        return;
    }
    if (!Directory.Exists(destination))
    {
        Directory.CreateDirectory(destination);
    }

    var fileSorter = new FileSorter();
    var fileParser = new CsvParser();

    Console.WriteLine("\nParsing book metadata...");
    var bookList = await fileParser.ParseDataCsv(csv, CancellationToken.None);
    Console.WriteLine($"Loaded {bookList.Count} audiobooks.");

    Console.WriteLine("\nSorting audiobooks...");
    await fileSorter.SortAudioFiles(source, destination, bookList);

}, sourceOption, csvOption, destinationOption);

// Run command parsing
return await rootCommand.InvokeAsync(args);

// Utility function for interactive mode
static string EnsureValue(string? value, string prompt)
{
    if (!string.IsNullOrWhiteSpace(value)) return value;
    Console.Write(prompt);
    return Console.ReadLine()?.Trim() ?? "";
}
