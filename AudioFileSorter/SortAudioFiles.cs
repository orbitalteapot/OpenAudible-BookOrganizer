using System.Collections;
using AudioFileSorter.Model;
using System.IO;
using System.Security.Cryptography;

namespace AudioFileSorter;

public class FileSorter
{
    /// <summary>
    /// Sorts Open Audible books into provided destination path
    /// </summary>
    /// <param name="destination"></param>
    /// <param name="openAudibles"></param>
    /// <param name="source"></param>
    public Task SortAudioFiles(string source, string destination, List<OpenAudible> openAudibles)
    {
        var progressCount = 0;
        var copyBooks = 0;
        openAudibles.ForEach(audioFile =>
        {
            audioFile.Author = SanitizeFileName(audioFile.Author);
            audioFile.SeriesName = SanitizeFileName(audioFile.SeriesName);
            audioFile.SeriesSequence = SanitizeFileName(audioFile.SeriesSequence);
            audioFile.ShortTitle = SanitizeFileName(audioFile.ShortTitle);
            audioFile.Title = SanitizeFileName(audioFile.Title);
            
            if (!string.IsNullOrEmpty(audioFile.Author))
            {
                var authorDirectoryPath = Path.Combine(destination, audioFile.Author);
                var directory = Directory.CreateDirectory(authorDirectoryPath);

                if (!string.IsNullOrEmpty(audioFile.SeriesName))
                {
                    var seriesDirectoryPath = Path.Combine(directory.FullName, audioFile.SeriesName);
                    directory = Directory.CreateDirectory(seriesDirectoryPath);

                    if (!string.IsNullOrEmpty(audioFile.SeriesSequence))
                    {
                        var sequenceDirectoryPath = Path.Combine(directory.FullName, $"Book {audioFile.SeriesSequence}");
                        directory = Directory.CreateDirectory(sequenceDirectoryPath);
                    }
                }

                if (!string.IsNullOrEmpty(audioFile.M4B))
                {
                    var destinationFile = Path.Combine(directory.FullName, audioFile.ShortTitle + ".m4b");
                    var sourceFile = Path.Combine(source, audioFile.Filename + ".m4b");
                    if (!File.Exists(destinationFile))
                    {
                        File.Copy(sourceFile, destinationFile);
                        copyBooks += 1;
                    }
                    else
                    {
                        if (!AreFilesSame(sourceFile, destinationFile))
                        {
                            File.Copy(sourceFile, destinationFile, true);
                            copyBooks += 1;
                        }
                    }
                    progressCount += 1;
                    
                }
                else if (!string.IsNullOrEmpty(audioFile.MP3))
                {
                    var destinationFile = Path.Combine(directory.FullName, audioFile.ShortTitle + ".mp3");
                    var sourceFile = Path.Combine(source, audioFile.Filename + ".mp3");
                    if (!File.Exists(destinationFile))
                    {
                        File.Copy(sourceFile, destinationFile);
                        copyBooks += 1;
                    }
                    else
                    {
                        if (!AreFilesSame(sourceFile, destinationFile))
                        {
                            File.Copy(sourceFile, destinationFile, true);
                            copyBooks += 1;
                        }
                    }
                    progressCount += 1;
                }
                
            }
            else
            {
                Console.WriteLine($"Missing data {audioFile.Filename}");
            }
            Console.Write($"\n\r{Math.Round((decimal)progressCount/openAudibles.Count*100,2)}/100% ({progressCount} of {openAudibles.Count}) Transferred: {copyBooks} =>  {audioFile.Title}");
        });
        Console.WriteLine();
        return Task.CompletedTask;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var invalidCharsRemoved = new string(fileName
            .Where(ch => !invalidChars.Contains(ch))
            .ToArray());
        invalidCharsRemoved = invalidCharsRemoved.TrimEnd('.', ' ');
        return invalidCharsRemoved;
    }
    
    private static bool AreFilesSame(string filePath1, string filePath2)
    {
        var fileInfo1 = new FileInfo(filePath1);
        var fileInfo2 = new FileInfo(filePath2);
        if (fileInfo1.Length != fileInfo2.Length)
        {
            return false;
        }

        return true;
    }
}