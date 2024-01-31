using System.IO;
using AudioFileSorter;

namespace AudioBookService;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private static DateTime lastRead = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var environmentVars = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process);
            var exportFileWatcher = new FileSystemWatcher(environmentVars["export"]?.ToString() ?? "D:\\mybooklist\\",
                environmentVars["filter"]?.ToString() ?? "*.csv");
            var source = environmentVars["source"]?.ToString() ?? "D:\\AudioBooks\\books";
            var destination = environmentVars["destination"]?.ToString() ?? "D:\\newtest";
            var fileSorter = new FileSorter();
            var fileParser = new CsvParser();

            var progress = new Progress<int>(report => { Console.Write($"\rSorting {report}/100%"); });

            exportFileWatcher.EnableRaisingEvents = true;
            exportFileWatcher.NotifyFilter = NotifyFilters.LastWrite;

            exportFileWatcher.Changed += OnExportFileChange;

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            async void OnExportFileChange(object sender, FileSystemEventArgs args)
            {
                var lastWriteTime = File.GetLastWriteTime(args.FullPath);
                if ((lastWriteTime - lastRead).TotalSeconds > 1)
                {
                    lastRead = lastWriteTime;
                    logger.LogInformation($"The file: {args.Name} changed! Starting sorting from {environmentVars["source"]} to {environmentVars["destination"]}!");
                    var bookList = await fileParser.ParseDataCsv(args.FullPath, stoppingToken);
                    await fileSorter.SortAudioFiles(source, destination, bookList);
                    logger.LogInformation($"Sorting complete");
                }
            }
        }
        catch (ArgumentException e)
        {
            logger.LogError(e, "Unable to find path");
        }
        catch (Exception e)
        {
            logger.LogError(e, e?.Message ?? "Unknown Error");
        }
    }
}