using AudioFileSorter.Model;
using ManagerApi.Services;

// Pin the content root to where the binary actually lives. The default is the current working
// directory, which is fine for the container (WORKDIR is the app) but arbitrary for the desktop
// app, where the backend is spawned as a child process and inherits whatever directory the user
// happened to launch from. That made "which files does the server serve" depend on how it was
// started.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

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

// SORT_INTERVAL ("6h", "1d", ...) sorts on a timer using the three paths above. Setting it fixes
// the schedule for the container; leaving it unset lets the Sort page set one instead.
var configuredInterval = Environment.GetEnvironmentVariable("SORT_INTERVAL");
SortSchedule? serverSchedule = null;
if (!string.IsNullOrWhiteSpace(configuredInterval))
{
    if (SortSchedule.TryParseInterval(configuredInterval, out var intervalMinutes))
    {
        serverSchedule = new SortSchedule
        {
            IntervalMinutes = intervalMinutes,
            CsvPath = csvPath,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            ComparisonMode = defaultComparisonMode
        };
    }
    else
    {
        Console.Error.WriteLine(
            $"Ignoring SORT_INTERVAL=\"{configuredInterval}\": expected something like \"6h\", \"1d\" or \"off\", " +
            $"at least {SortSchedule.MinimumIntervalMinutes} minutes. Automatic sorting is off.");
    }
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
builder.Services.AddSingleton(services => new SortScheduler(
    services.GetRequiredService<SortService>(),
    Environment.GetEnvironmentVariable("OABO_SETTINGS_PATH"),
    serverSchedule,
    services.GetRequiredService<ILogger<SortScheduler>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<SortScheduler>());

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:5123");

var app = builder.Build();

var logger = app.Logger;

app.UseCors();

// The desktop app ships the backend without a wwwroot: its window loads the interface straight
// off disk, and the backend is only an API. Only wire up static hosting when there is something
// to host, rather than logging "the WebRootPath was not found" on every desktop launch and
// answering unknown routes with an index.html that does not exist.
var servesWebUi = Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
if (servesWebUi)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
else
{
    logger.LogInformation("No wwwroot alongside the backend: serving the API only.");
}

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
    var pathError = SortService.ValidatePaths(request.CsvPath, request.SourcePath, request.DestinationPath);
    if (pathError is not null)
    {
        return Results.BadRequest(new { error = pathError });
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

app.MapGet("/api/schedule", (SortScheduler scheduler) => Results.Ok(ToScheduleResponse(scheduler)));

app.MapPut("/api/schedule", (ScheduleRequest? request, SortScheduler scheduler) =>
{
    if (request is null)
    {
        return Results.BadRequest(new { error = "A schedule is required" });
    }

    if (scheduler.IsManagedByServer)
    {
        return Results.Conflict(new { error = "Automatic sorting is set by the server's SORT_INTERVAL setting." });
    }

    if (request.IntervalMinutes is < SortSchedule.MinimumIntervalMinutes)
    {
        return Results.BadRequest(new { error = $"Sort at most every {SortSchedule.MinimumIntervalMinutes} minutes." });
    }

    if (!SortOptions.TryParseComparisonMode(request.ComparisonMode, out var comparisonMode))
    {
        return Results.BadRequest(new { error = $"Unknown comparison mode \"{request.ComparisonMode}\". Use \"quick\" or \"full\"." });
    }

    // Turning automatic sorting on with paths that cannot work should fail now, while the user is
    // looking, not silently at three in the morning.
    if (request.IntervalMinutes is not null)
    {
        if (string.IsNullOrWhiteSpace(request.CsvPath) ||
            string.IsNullOrWhiteSpace(request.SourcePath) ||
            string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return Results.BadRequest(new { error = "Choose all three paths before turning on automatic sorting" });
        }

        var pathError = SortService.ValidatePaths(request.CsvPath, request.SourcePath, request.DestinationPath);
        if (pathError is not null)
        {
            return Results.BadRequest(new { error = pathError });
        }
    }

    scheduler.Update(new SortSchedule
    {
        IntervalMinutes = request.IntervalMinutes,
        CsvPath = request.CsvPath,
        SourcePath = request.SourcePath,
        DestinationPath = request.DestinationPath,
        ComparisonMode = comparisonMode
    });

    return Results.Ok(ToScheduleResponse(scheduler));
});

if (servesWebUi)
{
    app.MapFallbackToFile("index.html");
}

app.Run();

static object ToScheduleResponse(SortScheduler scheduler)
{
    var schedule = scheduler.Current;
    return new
    {
        intervalMinutes = schedule.IntervalMinutes,
        csvPath = schedule.CsvPath,
        sourcePath = schedule.SourcePath,
        destinationPath = schedule.DestinationPath,
        comparisonMode = SortOptions.ToWireValue(schedule.ComparisonMode),
        lastRunUtc = schedule.LastRunUtc,
        lastResult = schedule.LastResult,
        nextRunUtc = schedule.NextRunUtc(DateTime.UtcNow),
        managedByServer = scheduler.IsManagedByServer
    };
}

record ParseRequest(string CsvPath);

/// <param name="ComparisonMode">"quick" or "full". Omitted means the server default.</param>
record SortRequest(string CsvPath, string SourcePath, string DestinationPath, string? ComparisonMode = null);

/// <param name="IntervalMinutes">Minutes between automatic sorts. Null turns them off.</param>
record ScheduleRequest(
    int? IntervalMinutes,
    string? CsvPath,
    string? SourcePath,
    string? DestinationPath,
    string? ComparisonMode = null);

/// <summary>Exposed so the integration tests can drive the real application host.</summary>
public partial class Program;
