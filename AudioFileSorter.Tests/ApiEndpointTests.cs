using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ManagerApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AudioFileSorter.Tests;

/// <summary>
/// Drives the real application host. Each test hands the app its own <see cref="ServerConfig"/>
/// instead of setting environment variables, so tests running side by side cannot see each other's.
/// </summary>
public class ApiEndpointTests
{
    [Fact]
    public async Task Settings_show_locked_paths_and_whether_each_can_be_found()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig
        {
            CsvPath = Path.Combine(workspace.Root, "missing.csv"),
            SourcePath = workspace.Source,
            // A subfolder of a working mount that nobody has made yet.
            DestinationPath = Path.Combine(workspace.Destination, "Audiobooks"),
            IsMappedFolder = folder => folder == workspace.Destination
        };
        await using var app = new ApiFactory(config);
        using var client = app.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.Equal(config.SourcePath, settings.GetProperty("sourcePath").GetString());
        Assert.True(settings.GetProperty("locks").GetProperty("paths").GetBoolean());
        Assert.False(settings.GetProperty("locks").GetProperty("schedule").GetBoolean());
        Assert.Equal("notFound", settings.GetProperty("pathStatus").GetProperty("csv").GetString());
        Assert.Equal("ok", settings.GetProperty("pathStatus").GetProperty("source").GetString());
        Assert.Equal("notFound", settings.GetProperty("pathStatus").GetProperty("destination").GetString());

        // Each missing server-set path in the words a refused start and the schedule card use.
        var messages = settings.GetProperty("pathMessages");
        Assert.StartsWith($"The library export {config.CsvPath} was not found inside the container", messages.GetProperty("csv").GetString());
        Assert.Equal(JsonValueKind.Null, messages.GetProperty("source").ValueKind);
        Assert.StartsWith($"The folder Audiobooks does not exist inside {workspace.Destination}", messages.GetProperty("destination").GetString());
        Assert.Equal("quick", settings.GetProperty("comparisonMode").GetString());
        Assert.Equal("normal", settings.GetProperty("copySpeed").GetString());
    }

    [Fact]
    public async Task A_locked_path_cannot_be_changed_from_the_page()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(new ServerConfig { CsvPath = "/data/books.csv" });
        using var client = app.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings", new { csvPath = "/elsewhere.csv" });

        var body = await ExpectStatus(response, HttpStatusCode.BadRequest);
        Assert.Equal("csvPath", body.GetProperty("field").GetString());
        Assert.Contains("CSV_PATH", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Saving_settings_returns_what_is_now_in_force()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            sourcePath = workspace.Source,
            destinationPath = Path.Combine(workspace.Root, "not-yet"),
            copySpeed = "gentle"
        });

        var body = await ExpectStatus(response, HttpStatusCode.OK);
        Assert.Equal(workspace.Source, body.GetProperty("sourcePath").GetString());
        Assert.Equal("gentle", body.GetProperty("copySpeed").GetString());
        Assert.Equal("notFound", body.GetProperty("pathStatus").GetProperty("destination").GetString());
        Assert.Equal("notSet", body.GetProperty("pathStatus").GetProperty("csv").GetString());
        // Paths the page chose: "Folder not found" says it all, so there is no server wording.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pathMessages").GetProperty("destination").ValueKind);

        var reloaded = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("gentle", reloaded.GetProperty("copySpeed").GetString());
    }

    [Fact]
    public async Task An_interval_below_the_minimum_is_refused()
    {
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings", new { scheduleIntervalMinutes = 5 });

        var body = await ExpectStatus(response, HttpStatusCode.BadRequest);
        Assert.Equal("scheduleIntervalMinutes", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Automatic_sorting_needs_paths_that_work_before_it_can_be_turned_on()
    {
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings", new { scheduleIntervalMinutes = 360 });

        var body = await ExpectStatus(response, HttpStatusCode.BadRequest);
        Assert.Equal("csvPath", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Sort_interval_off_leaves_automatic_sorting_to_the_page()
    {
        using var workspace = new TempWorkspace();
        var config = ServerConfig.FromEnvironment(name => name == "SORT_INTERVAL" ? "off" : null);
        await using var app = new ApiFactory(config);
        using var client = app.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(settings.GetProperty("locks").GetProperty("schedule").GetBoolean());

        var response = await client.PutAsJsonAsync("/api/settings", new
        {
            csvPath = workspace.WriteCsv(),
            sourcePath = workspace.Source,
            destinationPath = workspace.Destination,
            scheduleIntervalMinutes = 720
        });
        await ExpectStatus(response, HttpStatusCode.OK);

        var schedule = await client.GetFromJsonAsync<JsonElement>("/api/schedule");
        Assert.Equal(720, schedule.GetProperty("intervalMinutes").GetInt32());
        Assert.False(schedule.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("blockedReason").ValueKind);
    }

    [Fact]
    public async Task An_unreadable_sort_interval_is_shown_as_a_server_warning()
    {
        var config = ServerConfig.FromEnvironment(name => name == "SORT_INTERVAL" ? "6x" : null);
        await using var app = new ApiFactory(config);
        using var client = app.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.False(settings.GetProperty("locks").GetProperty("schedule").GetBoolean());
        var warning = Assert.Single(settings.GetProperty("serverWarnings").EnumerateArray());
        Assert.StartsWith("SORT_INTERVAL=\"6x\" was ignored", warning.GetString());
    }

    [Fact]
    public async Task A_started_sort_is_running_and_then_finished()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteLargeLibrary(20, 10_000)));
        using var client = app.CreateClient();

        // No body at all: every field of a start request is optional.
        var response = await client.PostAsync("/api/sort/start", EmptyJson());

        var started = await ExpectStatus(response, HttpStatusCode.Accepted);
        Assert.Equal("running", started.GetProperty("state").GetString());
        Assert.Equal("manual", started.GetProperty("trigger").GetString());

        var finished = await WaitForFinish(client);
        Assert.Equal(20, finished.GetProperty("counts").GetProperty("new").GetInt32());
        Assert.Equal(0, finished.GetProperty("counts").GetProperty("upToDate").GetInt32());
        Assert.Equal(100, finished.GetProperty("percentage").GetDouble());
        Assert.False(finished.GetProperty("isCanceled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Starting_while_a_sort_runs_is_a_conflict_and_cancel_stops_it()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteLargeLibrary(400)));
        using var client = app.CreateClient();

        // One book at a time, so the run is still going when the next requests arrive.
        await ExpectStatus(await client.PutAsJsonAsync("/api/settings", new { copySpeed = "gentle" }), HttpStatusCode.OK);
        await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.Accepted);

        var second = await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.Conflict);
        Assert.Equal("alreadyRunning", second.GetProperty("code").GetString());

        await ExpectStatus(await client.PostAsync("/api/sort/cancel", EmptyJson()), HttpStatusCode.OK);

        var finished = await WaitForFinish(client);
        Assert.True(finished.GetProperty("isCanceled").GetBoolean());
        await ExpectStatus(await client.PostAsync("/api/sort/cancel", EmptyJson()), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_cancel_sent_by_the_quitting_desktop_app_says_the_app_closed()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteLargeLibrary(400)));
        using var client = app.CreateClient();
        await ExpectStatus(await client.PutAsJsonAsync("/api/settings", new { copySpeed = "gentle" }), HttpStatusCode.OK);
        await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.Accepted);

        await ExpectStatus(await client.PostAsJsonAsync("/api/sort/cancel", new { reason = "appClosing" }), HttpStatusCode.OK);

        var finished = await WaitForFinish(client);
        Assert.True(finished.GetProperty("isCanceled").GetBoolean());
        Assert.Equal("Canceled because the app closed.", finished.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_missing_destination_is_reported_and_only_created_once_confirmed()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var missing = Path.Combine(workspace.Root, "new-destination");
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();
        await ExpectStatus(
            await client.PutAsJsonAsync("/api/settings", new
            {
                csvPath = workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"),
                sourcePath = workspace.Source,
                destinationPath = missing
            }),
            HttpStatusCode.OK);

        var refused = await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.BadRequest);
        Assert.Equal("destinationMissing", refused.GetProperty("code").GetString());
        Assert.Equal("destinationPath", refused.GetProperty("field").GetString());
        Assert.False(Directory.Exists(missing));

        await ExpectStatus(
            await client.PostAsJsonAsync("/api/sort/start", new { createDestination = true }), HttpStatusCode.Accepted);

        var finished = await WaitForFinish(client);
        Assert.Equal(1, finished.GetProperty("counts").GetProperty("new").GetInt32());
        Assert.True(Directory.Exists(missing));
    }

    [Fact]
    public async Task A_destination_set_by_the_server_is_never_created_whoever_asks()
    {
        // In a container that is a forgotten mount; creating it would copy the library into the
        // container, to be lost when it is recreated.
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        var missing = Path.Combine(workspace.Root, "unmounted", "Audiobooks");
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit"), missing));
        using var client = app.CreateClient();

        var refused = await ExpectStatus(
            await client.PostAsJsonAsync("/api/sort/start", new { createDestination = true }), HttpStatusCode.BadRequest);

        Assert.Equal("destinationMissing", refused.GetProperty("code").GetString());
        Assert.False(Directory.Exists(missing));

        // Worded for the container's admin, not for someone with a USB drive.
        Assert.Equal(
            $"The destination folder {missing} was not found inside the container. Check the volume mapping for DESTINATION_PATH.",
            refused.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Start_sorting_asks_before_filling_a_destination_that_has_lost_its_marker()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSourceFile("the-hobbit.m4b");
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit")));
        using var client = app.CreateClient();
        await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.Accepted);
        await WaitForFinish(client);

        // After a reboot the drive is not mounted, and an empty folder stands at the same path.
        Directory.Delete(workspace.Destination, recursive: true);
        Directory.CreateDirectory(workspace.Destination);

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("unmounted", settings.GetProperty("pathStatus").GetProperty("destination").GetString());
        Assert.Contains(SortPathValidator.MarkerFileName, settings.GetProperty("pathMessages").GetProperty("destination").GetString());

        var refused = await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.BadRequest);
        Assert.Equal("destinationUnmounted", refused.GetProperty("code").GetString());
        Assert.Empty(workspace.DestinationFiles());

        await ExpectStatus(
            await client.PostAsJsonAsync("/api/sort/start", new { confirmUnmounted = true }), HttpStatusCode.Accepted);
        await WaitForFinish(client);
        Assert.Equal(["Tolkien/The Hobbit/The Hobbit.m4b"], workspace.DestinationFiles());
    }

    [Fact]
    public async Task Problems_are_listed_with_their_kind_once_the_run_is_finished()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit")));
        using var client = app.CreateClient();

        await ExpectStatus(await client.PostAsJsonAsync("/api/sort/start", new { }), HttpStatusCode.Accepted);

        var finished = await WaitForFinish(client);
        var problem = Assert.Single(finished.GetProperty("problems").EnumerateArray());
        Assert.Equal("notFound", problem.GetProperty("kind").GetString());
        Assert.Contains("The Hobbit", problem.GetProperty("book").GetString());
        Assert.Equal(1, finished.GetProperty("counts").GetProperty("notFound").GetInt32());
    }

    [Fact]
    public async Task Loading_the_library_reads_the_export_in_the_settings()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        var missing = await ExpectStatus(await client.PostAsync("/api/books/parse", EmptyJson()), HttpStatusCode.BadRequest);
        Assert.Equal("csvPath", missing.GetProperty("field").GetString());
        Assert.Equal("notSet", missing.GetProperty("code").GetString());

        await ExpectStatus(
            await client.PutAsJsonAsync("/api/settings", new { csvPath = workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit") }),
            HttpStatusCode.OK);

        var parsed = await ExpectStatus(await client.PostAsync("/api/books/parse", EmptyJson()), HttpStatusCode.OK);
        Assert.Equal(1, parsed.GetProperty("books").GetArrayLength());
        var books = await client.GetFromJsonAsync<JsonElement>("/api/books");
        Assert.Equal(1, books.GetArrayLength());
    }

    [Fact]
    public async Task The_old_config_and_schedule_endpoints_are_gone()
    {
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/config")).StatusCode);
        Assert.Equal(
            HttpStatusCode.MethodNotAllowed,
            (await client.PutAsJsonAsync("/api/schedule", new { intervalMinutes = 60 })).StatusCode);
    }

    private static ServerConfig Locked(TempWorkspace workspace, string csvPath, string? destination = null) => new()
    {
        CsvPath = csvPath,
        SourcePath = workspace.Source,
        DestinationPath = destination ?? workspace.Destination
    };

    private static async Task<JsonElement> ExpectStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForFinish(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/api/sort/progress");
            if (status.GetProperty("state").GetString() == "finished")
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sort to finish.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task The_desktop_backend_refuses_requests_from_web_sites()
    {
        using var workspace = new TempWorkspace();
        await using var app = new ApiFactory(Locked(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit")));
        using var client = app.CreateClient();

        // A page on any site can send a simple POST to 127.0.0.1; CORS only hides the answer.
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/sort/start") { Content = EmptyJson() };
        crossSite.Headers.Add("Origin", "https://evil.example");
        await ExpectStatus(await client.SendAsync(crossSite), HttpStatusCode.Forbidden);

        // Without JSON a browser sends it cross-site with no preflight, so it is refused as well.
        await ExpectStatus(await client.PostAsync("/api/sort/start", null), HttpStatusCode.UnsupportedMediaType);

        // A name rebound to 127.0.0.1 must not be able to read the library.
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        rebound.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(rebound)).StatusCode);

        Assert.Equal("idle", (await client.GetFromJsonAsync<JsonElement>("/api/sort/progress")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_page_of_the_dev_server_and_of_the_desktop_window_may_send_changes()
    {
        await using var app = new ApiFactory(new ServerConfig());
        using var client = app.CreateClient();

        foreach (var origin in new[] { "http://localhost:5173", "null", "file://" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(new { copySpeed = "gentle" }) };
            request.Headers.Add("Origin", origin);
            await ExpectStatus(await client.SendAsync(request), HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_container_refuses_changes_a_web_site_can_send_without_a_preflight()
    {
        using var workspace = new TempWorkspace();
        var config = Locked(workspace, workspace.WriteCsv("The Hobbit,Tolkien,the-hobbit")) with { BindUrl = "http://0.0.0.0:5123" };
        await using var app = new ApiFactory(config);
        using var client = app.CreateClient();

        // fetch(..., { method: 'POST', mode: 'no-cors' }) from any page: no body, no preflight.
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/sort/start");
        crossSite.Headers.Add("Origin", "https://evil.example");
        await ExpectStatus(await client.SendAsync(crossSite), HttpStatusCode.UnsupportedMediaType);
        await ExpectStatus(await client.PostAsync("/api/sort/cancel", null), HttpStatusCode.UnsupportedMediaType);

        Assert.Equal("idle", (await client.GetFromJsonAsync<JsonElement>("/api/sort/progress")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_containers_own_page_may_send_changes_through_a_proxy_that_changes_its_address()
    {
        await using var app = new ApiFactory(new ServerConfig { BindUrl = "http://0.0.0.0:5123" });
        using var client = app.CreateClient();

        // TLS ends at the proxy, which forwards plain http under another name.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(new { copySpeed = "gentle" }) };
        request.Headers.Add("Origin", "https://books.example.com");
        await ExpectStatus(await client.SendAsync(request), HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_container_answers_to_any_name()
    {
        await using var app = new ApiFactory(new ServerConfig { BindUrl = "http://0.0.0.0:5123" });
        using var client = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        request.Headers.Host = "nas.local";
        await ExpectStatus(await client.SendAsync(request), HttpStatusCode.OK);
    }

    /// <summary>A POST with no body, sent as JSON the way the page sends it.</summary>
    private static StringContent EmptyJson() => new(string.Empty, System.Text.Encoding.UTF8, "application/json");

    /// <summary>The real app, with the given configuration in place of whatever the environment says.</summary>
    private sealed class ApiFactory(ServerConfig config) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services => services.AddSingleton(config));
        }
    }
}
