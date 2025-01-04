using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AudioFileSorter.Model;

namespace AudioFileSorter;

public class FileSorter
{
    private static readonly object ConsoleLock = new();
    /// <summary>
    /// Sorts Open Audible books into the provided destination path in parallel.
    /// </summary>
    /// <param name="source">Source folder containing audio files.</param>
    /// <param name="destination">Destination folder to sort files into.</param>
    /// <param name="openAudibles">List of audiobook metadata.</param>
    public async Task SortAudioFiles(string? source, string? destination, List<OpenAudible> openAudibles)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            Console.WriteLine("Error: Source or destination path is missing.");
            return;
        }

        var progressCount = 0;
        var copyBooks = 0;
        var totalBooks = openAudibles.Count;
        var maxLineLength = 0;
        
        var maxParallelism = Math.Max(1, Environment.ProcessorCount / 4); // speed up transfers for high end cpus
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism };
        
        
        // Run parallel sorting operations
        await Parallel.ForEachAsync(openAudibles, parallelOptions, async (audioFile, _) =>
        {
            try
            {
                var copied = await ProcessAudioFile(audioFile, source, destination);
                if (copied) Interlocked.Increment(ref copyBooks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError processing {audioFile.Filename}: {ex.Message}");
            }

            var currentProgress = Interlocked.Increment(ref progressCount);
            UpdateProgress(currentProgress, totalBooks, copyBooks, audioFile.Title, ref maxLineLength);
        });

        Console.WriteLine("\nSorting complete.");
    }

    private static async Task<bool> ProcessAudioFile(OpenAudible audioFile, string source, string destination)
    {
        // Sanitize file names to prevent invalid path issues
        audioFile.Author = SanitizeFileName(audioFile.Author);
        audioFile.SeriesName = SanitizeFileName(audioFile.SeriesName);
        audioFile.SeriesSequence = SanitizeFileName(audioFile.SeriesSequence);
        audioFile.ShortTitle = SanitizeFileName(audioFile.ShortTitle);
        audioFile.Title = SanitizeFileName(audioFile.Title);

        if (string.IsNullOrWhiteSpace(audioFile.Author))
        {
            Console.WriteLine($"\nWarning: Missing author for {audioFile.Filename}");
            return false;
        }

        var directory = CreateTargetDirectory(destination, audioFile);
        return await CopyAudioFileAsync(audioFile, source, directory);
    }

    private static string CreateTargetDirectory(string destination, OpenAudible audioFile)
    {
        var authorPath = Path.Combine(destination, audioFile.Author ?? throw new InvalidOperationException());
        var directory = Directory.CreateDirectory(authorPath).FullName;

        if (!string.IsNullOrWhiteSpace(audioFile.SeriesName))
        {
            directory = Path.Combine(directory, audioFile.SeriesName);
            Directory.CreateDirectory(directory);

            if (!string.IsNullOrWhiteSpace(audioFile.SeriesSequence))
            {
                directory = Path.Combine(directory, $"Book {audioFile.SeriesSequence}");
                Directory.CreateDirectory(directory);
            }
        }

        return directory;
    }


    private static async Task<bool> CopyAudioFileAsync(OpenAudible audioFile, string source, string targetDirectory)
    {
        var fileExtension = GetAudioFileExtension(audioFile);
        if (fileExtension == null) return false;

        var sourceFile = Path.Combine(source, $"{audioFile.Filename}{fileExtension}");
        var destinationFile = Path.Combine(targetDirectory, $"{audioFile.ShortTitle}{fileExtension}");

        if (!File.Exists(sourceFile))
        {
            Console.WriteLine($"\nWarning: Source file missing: {sourceFile}");
            return false;
        }

        try
        {
            if (!File.Exists(destinationFile) || !await AreFilesSameAsync(sourceFile, destinationFile))
            {
                await CopyFileAsync(sourceFile, destinationFile);
                // File.Copy(sourceFile, destinationFile, true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError copying {sourceFile}: {ex.Message}");
        }

        return false;
    }

    private static string? GetAudioFileExtension(OpenAudible audioFile)
    {
        if (!string.IsNullOrWhiteSpace(audioFile.M4B)) return ".m4b";
        if (!string.IsNullOrWhiteSpace(audioFile.MP3)) return ".mp3";
        return null;
    }

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "";
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(ch => !invalidChars.Contains(ch)).ToArray());
        return sanitized.TrimEnd('.', ' ');
    }

    private static async Task<bool> AreFilesSameAsync(string filePath1, string filePath2)
    {
        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            // 🔹 Fastest check: Compare file size first
            if (fileInfo1.Length != fileInfo2.Length) return false;

            const int chunkSize = 4096; // 4KB buffer for speed
            var buffer1 = new byte[chunkSize];
            var buffer2 = new byte[chunkSize];

            // 🔹 Open files in `FileShare.ReadWrite` mode to prevent locking issues
            await using var stream1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);
            await using var stream2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);

            // 🔹 Compare first chunk
            var bytesRead1 = await stream1.ReadAsync(buffer1, 0, chunkSize);
            var bytesRead2 = await stream2.ReadAsync(buffer2, 0, chunkSize);
            if (bytesRead1 != bytesRead2 || !buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)))
                return false;

            // 🔹 Compare last chunk if file is larger than chunk size
            if (fileInfo1.Length > chunkSize)
            {
                stream1.Seek(-chunkSize, SeekOrigin.End);
                stream2.Seek(-chunkSize, SeekOrigin.End);

                bytesRead1 = await stream1.ReadAsync(buffer1, 0, chunkSize);
                bytesRead2 = await stream2.ReadAsync(buffer2, 0, chunkSize);
                if (bytesRead1 != bytesRead2 || !buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead2)))
                    return false;
            }

            return true;
        }
        catch
        {
            return false; // Assume files are different if any error occurs
        }
    }


    private static void UpdateProgress(int currentProgress, int totalBooks, int copyBooks, string? title, ref int maxLineLength)
    {
        var message = $"{Math.Round((decimal)currentProgress / totalBooks * 100, 2)}% ({currentProgress}/{totalBooks}) Transferred: {copyBooks} => {title}";

        lock (ConsoleLock)
        {
            // Clear previous message with spaces to prevent text corruption
            Console.Write("\r" + new string(' ', maxLineLength) + "\r");
            Console.Write(message);

            // Track the longest message to properly clear on next update
            maxLineLength = Math.Max(maxLineLength, message.Length);
        }
    }
    private static async Task CopyFileAsync(string sourceFile, string destinationFile)
    {
        const int bufferSize = 81920; // 80KB buffer for efficiency

        await using var sourceStream = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destinationStream = new FileStream(
            destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await sourceStream.CopyToAsync(destinationStream);
    }

}