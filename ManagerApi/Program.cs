using AudioFileSorter;
using AudioFileSorter.Model;
using ManagerApi.Services;

var builder = WebApplication.CreateBuilder(args);

var csvPath = Environment.GetEnvironmentVariable("CSV_PATH") ?? string.Empty;
var sourcePath = Environment.GetEnvironmentVariable("SOURCE_PATH") ?? string.Empty;
var destinationPath = Environment.GetEnvironmentVariable("DESTINATION_PATH") ?? string.Empty;

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

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config", () => Results.Ok(new
{
    csvPath,
    sourcePath,
    destinationPath,
    webMode = true
}));

app.MapPost("/api/books/parse", async (ParseRequest request, SortService sortService) =>
{
    if (string.IsNullOrWhiteSpace(request.CsvPath))
        return Results.BadRequest(new { error = "CSV path is required" });

    if (!File.Exists(request.CsvPath))
        return Results.BadRequest(new { error = "CSV file not found" });

    try
    {
        var books = await sortService.ParseBooks(request.CsvPath);
        return Results.Ok(books);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/books", (SortService sortService) =>
{
    return Results.Ok(sortService.GetBooks());
});

app.MapPost("/api/sort/start", (SortRequest request, SortService sortService) =>
{
    if (string.IsNullOrWhiteSpace(request.CsvPath) ||
        string.IsNullOrWhiteSpace(request.SourcePath) ||
        string.IsNullOrWhiteSpace(request.DestinationPath))
        return Results.BadRequest(new { error = "All paths are required" });

    if (sortService.IsSorting)
        return Results.Conflict(new { error = "Sort already in progress" });

    _ = Task.Run(() => sortService.StartSort(request.CsvPath, request.SourcePath, request.DestinationPath));
    return Results.Ok(new { message = "Sort started" });
});

app.MapGet("/api/sort/progress", (SortService sortService) =>
{
    return Results.Ok(sortService.GetProgress());
});

app.MapPost("/api/sort/cancel", (SortService sortService) =>
{
    if (!sortService.IsSorting)
        return Results.BadRequest(new { error = "No sort is currently running" });

    var canceled = sortService.CancelSort();
    if (!canceled)
        return Results.BadRequest(new { error = "No sort is currently running" });

    return Results.Ok(new { message = "Sort cancellation requested" });
});

app.MapFallbackToFile("index.html");

app.Run();

record ParseRequest(string CsvPath);
record SortRequest(string CsvPath, string SourcePath, string DestinationPath);
