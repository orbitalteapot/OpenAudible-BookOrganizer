using System.Text.Json;
using System.Text.Json.Serialization;
using AudioFileSorter;
using AudioFileSorter.Model;
using ManagerApi.Services;
using Microsoft.AspNetCore.HostFiltering;

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

// The framework logs six lines at Information for every request, and the page polls several times
// a second during a sort: that buried the startup summary and the run results the README says to
// read in "docker logs", and grew the log without end. Its warnings and errors still get through.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Read once, here. Everything else takes it from the container, so a test can swap in its own.
var serverConfig = ServerConfig.FromEnvironment();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(LocalRequestGuard.DevServerOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Enums go over the wire as the page spells them: "running", "scheduled", "notFound".
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton(serverConfig);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new SettingsStore(
    services.GetRequiredService<ServerConfig>().SettingsPath,
    services.GetRequiredService<ILogger<SettingsStore>>()));
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<SortService>();
builder.Services.AddSingleton<SortScheduler>();
builder.Services.AddHostedService(services => services.GetRequiredService<SortScheduler>());
builder.Services.AddHostedService<ParentProcessWatch>();

// The desktop backend only answers to its own names, so a web site cannot rebind one of its own
// to 127.0.0.1 and read the library. Read from the registered ServerConfig, so a test's own applies.
builder.Services.AddOptions<HostFilteringOptions>().Configure<ServerConfig>((options, config) =>
{
    if (config.IsLoopbackOnly)
    {
        options.AllowedHosts = LocalRequestGuard.AllowedHosts;
    }
});

builder.WebHost.UseUrls(serverConfig.BindUrl);

var app = builder.Build();

var logger = app.Logger;
var config = app.Services.GetRequiredService<ServerConfig>();
config.Log(logger);

using var settingsLock = SettingsFileLock.Acquire(config.SettingsPath, logger);
if (settingsLock is null)
{
    return SettingsFileLock.InUseExitCode;
}

app.UseCors();

app.Use(async (context, next) =>
{
    if (LocalRequestGuard.Refusal(context.Request, checkOrigin: config.IsLoopbackOnly) is { } refusal)
    {
        context.Response.StatusCode = refusal.Status;
        await context.Response.WriteAsJsonAsync(new { error = refusal.Error });
        return;
    }

    await next(context);
});

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

app.MapGet("/api/settings", (SettingsService settings) => Results.Ok(SettingsResponse.From(settings)));

app.MapPut("/api/settings", (AppSettingsPatch? patch, SettingsService settings) =>
{
    if (patch is null)
    {
        return Results.BadRequest(new { error = "Send the settings to change.", field = (string?)null });
    }

    return settings.TryUpdate(patch, out var error)
        ? Results.Ok(SettingsResponse.From(settings))
        : Results.BadRequest(new { error = error!.Message, field = error.Field, code = error.Code });
});

app.MapPost("/api/books/parse", async (SortService sortService, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await sortService.ParseBooks(cancellationToken);

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
    catch (SortPathException ex)
    {
        return Results.BadRequest(RunErrors.Body(ex));
    }
    catch (FileNotFoundException ex)
    {
        return CsvError(ex.Message, RunErrors.CsvNotFound);
    }
    catch (InvalidDataException ex)
    {
        return CsvError(ex.Message, RunErrors.CsvInvalid);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error reading the library export");
        return CsvError($"Could not read the CSV file: {ex.Message}", RunErrors.CsvInvalid);
    }
});

app.MapGet("/api/books", (SortService sortService) => Results.Ok(sortService.GetBooks()));

app.MapPost("/api/sort/start", (StartSortRequest? request, SortService sortService, SettingsService settings) =>
{
    // An omitted mode means the saved one, so a run started from anywhere does what the page shows.
    FileComparisonMode? comparisonMode = null;
    if (!string.IsNullOrWhiteSpace(request?.ComparisonMode))
    {
        if (!SortOptions.TryParseComparisonMode(request.ComparisonMode, out var requested))
        {
            return Results.BadRequest(new
            {
                error = $"Unknown update check \"{request.ComparisonMode}\". Use \"quick\" or \"full\".",
                code = RunErrors.InvalidComparisonMode,
                field = "comparisonMode"
            });
        }

        comparisonMode = requested;
    }

    // A person pressed Start, so a missing destination is created — but only once they have been
    // asked: the page sends createDestination after "The destination folder doesn't exist" is confirmed.
    var options = settings.SortOptionsFor(request?.CreateDestination ?? false, comparisonMode);
    try
    {
        return sortService.TryStartSort(RunTrigger.Manual, options, out _)
            ? Results.Accepted(value: sortService.GetStatus())
            : Results.Conflict(new { error = "A sort is already running.", code = RunErrors.AlreadyRunning });
    }
    catch (SortPathException ex)
    {
        return Results.BadRequest(RunErrors.Body(ex));
    }
});

app.MapGet("/api/sort/progress", (SortService sortService) => Results.Ok(sortService.GetStatus()));

app.MapPost("/api/sort/cancel", (SortService sortService) =>
{
    return sortService.CancelSort()
        ? Results.Ok(new { message = "Sort cancellation requested" })
        : Results.BadRequest(new { error = "No sort is currently running" });
});

app.MapGet("/api/schedule", (SortScheduler scheduler) => Results.Ok(scheduler.GetStatus()));

if (servesWebUi)
{
    app.MapFallbackToFile("index.html");
}

app.Run();
return 0;

static IResult CsvError(string message, string code)
{
    return Results.BadRequest(new { error = message, code, field = RunErrors.CsvPathField });
}

/// <param name="ComparisonMode">"quick" or "full" for this run only. Omitted means the saved setting.</param>
/// <param name="CreateDestination">Create a missing destination folder; sent once the user has agreed to it.</param>
record StartSortRequest(string? ComparisonMode = null, bool CreateDestination = false);

/// <summary>Exposed so the integration tests can drive the real application host.</summary>
public partial class Program;
