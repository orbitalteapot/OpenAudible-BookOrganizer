// See https://aka.ms/new-console-template for more information
using AudioFileSorter;

Console.WriteLine("OpenAudible Book Organizer!");
var fileSorter = new FileSorter();
var fileParser = new CsvParser();

Console.WriteLine("Will make a copy of your audiobooks in a new folder structure based on the metadata in the OpenAudible export file!");

Console.Write("Enter the full path to the source folder containing audio files: ");
var sourceFolder = Console.ReadLine();

Console.Write("Enter the full path to the OpenAudible book export file: ");
var csvFile = Console.ReadLine();

Console.Write("Enter the full path to the destination folder where you want the organized copy of your audiobooks: ");
var destinationFolder = Console.ReadLine();


var bookList = await fileParser.ParseDataCsv(csvFile, CancellationToken.None);
await fileSorter.SortAudioFiles(sourceFolder, destinationFolder, bookList);

Console.WriteLine("Done!");

