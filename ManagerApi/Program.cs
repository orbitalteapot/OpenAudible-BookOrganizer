using AudioFileSorter.Model;
using ManagerApi.Services;

var builder = WebApplication.CreateBuilder(args);

var csvPath = Environment.GetEnvironmentVariable("CSV_PATH") ?? string.Empty;
var sourcePath = Environment.GetEnvironmentVariable("SOURCE_PATH") ?? string.Empty;
var destinationPath = Environment.GetEnvironmentVariable("DESTINATION_PATH") ?? string.Empty;

// COMPARISON_MODE is the server-wide default: a container can be set up to verify contents on
// every run, and a request that names a mode explicitly still wins.
var configuredComparisonMode = Environment.GetEnvironmentVariable("COMPARISON_MODE");
if (!SortOptions.TryParseComparisonMode(configuredComparisonMode, out var defaultComparisonMode))
{
    Console.Error.WriteLine(
        $"Ignoring COMPARISON_MODE=\"{configuredComparisonMode}\": expected \"quick\" or \"full\". Using \"quick\".");
    defaultComparisonMode = SortOptions.Default.ComparisonMode;
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddSingleton<SortService>();

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:5123");

var app = builder.Build();

var logger = app.Logger;

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config", () => Results.Ok(new
{
    csvPath,
    sourcePath,
    destinationPath,
    webMode = true,
    comparisonMode = SortOptions.ToWireValue(defaultComparisonMode),
    csvExists = !string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath),
    sourceExists = !string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath),
    destinationExists = !string.IsNullOrWhiteSpace(destinationPath) && Directory.Exists(destinationPath)
}));

app.MapPost("/api/books/parse", async (ParseRequest? request, SortService sortService, CancellationToken cancellationToken) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.CsvPath))
    {
        return Results.BadRequest(new { error = "CSV path is required" });
    }

    try
    {
        var result = await sortService.ParseBooks(request.CsvPath, cancellationToken);

        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("CSV import: {Warning}", warning);
        }

        return Results.Ok(new
        {
            books = result.Books,
            skippedRows = result.SkippedRows,
            warnings = result.Warnings
        });
    }
    catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidDataException or InvalidOperationException)
    {
        logger.LogWarning(ex, "Failed to parse {CsvPath}", request.CsvPath);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error parsing {CsvPath}", request.CsvPath);
        return Results.BadRequest(new { error = $"Could not read the CSV file: {ex.Message}" });
    }
});

app.MapGet("/api/books", (SortService sortService) => Results.Ok(sortService.GetBooks()));

app.MapPost("/api/sort/start", (SortRequest? request, SortService sortService) =>
{
    if (request is null ||
        string.IsNullOrWhiteSpace(request.CsvPath) ||
        string.IsNullOrWhiteSpace(request.SourcePath) ||
        string.IsNullOrWhiteSpace(request.DestinationPath))
    {
        return Results.BadRequest(new { error = "All paths are required" });
    }

    // An omitted mode means "whatever this server is configured for", so a container started with
    // COMPARISON_MODE=full verifies contents even for callers that never heard of the setting.
    var comparisonMode = defaultComparisonMode;
    if (!string.IsNullOrWhiteSpace(request.ComparisonMode) &&
        !SortOptions.TryParseComparisonMode(request.ComparisonMode, out comparisonMode))
    {
        return Results.BadRequest(new
        {
            error = $"Unknown comparison mode \"{request.ComparisonMode}\". Use \"quick\" or \"full\"."
        });
    }

    // Validate before starting so the user gets a real error instead of a run that reports
    // failure seconds later, or worse, never reports at all.
    if (!File.Exists(request.CsvPath))
    {
        return Results.BadRequest(new { error = $"CSV file not found: {request.CsvPath}" });
    }

    if (!Directory.Exists(request.SourcePath))
    {
        return Results.BadRequest(new { error = $"Source folder not found: {request.SourcePath}" });
    }

    try
    {
        Directory.CreateDirectory(request.DestinationPath);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Destination folder is not writable: {ex.Message}" });
    }

    if (PathsOverlap(request.SourcePath, request.DestinationPath))
    {
        return Results.BadRequest(new
        {
            error = "The destination folder cannot be the source folder or live inside it."
        });
    }

    // Checking IsSorting separately would leave a window where two requests both start a run.
    var options = new SortOptions { ComparisonMode = comparisonMode };
    if (!sortService.TryStartSort(request.CsvPath, request.SourcePath, request.DestinationPath, options, out var sortTask))
    {
        return Results.Conflict(new { error = "Sort already in progress" });
    }

    _ = sortTask.ContinueWith(
        t => logger.LogError(t.Exception, "Sort task faulted"),
        TaskContinuationOptions.OnlyOnFaulted);

    return Results.Ok(new
    {
        message = "Sort started",
        comparisonMode = SortOptions.ToWireValue(comparisonMode)
    });
});

app.MapGet("/api/sort/progress", (SortService sortService) => Results.Ok(sortService.GetProgress()));

app.MapPost("/api/sort/cancel", (SortService sortService) =>
{
    return sortService.CancelSort()
        ? Results.Ok(new { message = "Sort cancellation requested" })
        : Results.BadRequest(new { error = "No sort is currently running" });
});

app.MapFallbackToFile("index.html");

app.Run();

// Copying a library into itself (or into a subfolder of itself) makes the source grow while it is
// being read, which never terminates cleanly.
static bool PathsOverlap(string sourcePath, string destinationPath)
{
    try
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return string.Equals(source, destination, comparison) ||
               destination.StartsWith(source + Path.DirectorySeparatorChar, comparison);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return false;
    }
}

record ParseRequest(string CsvPath);

/// <param name="ComparisonMode">"quick" or "full". Omitted means the server default.</param>
record SortRequest(string CsvPath, string SourcePath, string DestinationPath, string? ComparisonMode = null);

/// <summary>Exposed so the integration tests can drive the real application host.</summary>
public partial class Program;
