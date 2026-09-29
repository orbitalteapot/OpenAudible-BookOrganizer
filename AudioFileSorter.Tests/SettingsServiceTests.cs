using System.Text.Json.Nodes;
using AudioFileSorter.Model;
using ManagerApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AudioFileSorter.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Settings_are_saved_and_read_back_after_a_restart()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig { SettingsPath = Path.Combine(workspace.Root, "settings.json") };

        using (var first = new TestBackend(config))
        {
            Assert.True(first.Settings.TryUpdate(new AppSettingsPatch
            {
                SourcePath = workspace.Source,
                ComparisonMode = "full",
                CopySpeed = "gentle",
                KeepRunningInBackground = true
            }, out var error), error?.Message);
        }

        using var restarted = new TestBackend(config);
        var settings = restarted.Settings.Effective;

        Assert.Equal(workspace.Source, settings.SourcePath);
        Assert.Equal(FileComparisonMode.Full, settings.ComparisonMode);
        Assert.Equal(CopySpeed.Gentle, settings.CopySpeed);
        Assert.True(settings.KeepRunningInBackground);
        Assert.False(File.Exists(config.SettingsPath + ".tmp"));
    }

    [Fact]
    public void The_settings_file_is_versioned_and_spells_choices_as_words()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig { SettingsPath = Path.Combine(workspace.Root, "settings.json") };
        using var backend = new TestBackend(config);

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out _));

        var file = JsonNode.Parse(File.ReadAllText(config.SettingsPath!))!;
        Assert.Equal(1, file["version"]!.GetValue<int>());
        Assert.Equal("gentle", file["settings"]!["copySpeed"]!.GetValue<string>());
        // Nobody picked an update check, so none is saved and COMPARISON_MODE keeps deciding.
        Assert.Null(file["settings"]!["comparisonMode"]);
        Assert.NotNull(file["schedule"]);
    }

    [Fact]
    public void A_value_in_the_file_that_makes_no_sense_falls_back_on_its_own()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, """
            { "version": 1,
              "settings": { "sourcePath": "/books", "comparisonMode": "thorough", "copySpeed": "gentle", "scheduleIntervalMinutes": 5 },
              "schedule": { "lastAttemptUtc": "2026-01-01T00:00:00Z" } }
            """);

        var state = new SettingsStore(path, NullLogger<SettingsStore>.Instance).Load();

        Assert.Equal("/books", state.Settings.SourcePath);
        Assert.Null(state.Settings.ComparisonMode);
        Assert.Equal(CopySpeed.Gentle, state.Settings.CopySpeed);
        Assert.Null(state.Settings.ScheduleIntervalMinutes);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), state.Schedule.LastAttemptUtc);
    }

    [Fact]
    public void An_unreadable_file_starts_from_the_defaults()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, "{ not json");

        var state = new SettingsStore(path, NullLogger<SettingsStore>.Instance).Load();

        Assert.Equal(new AppSettings(), state.Settings);
        Assert.Equal(new ScheduleState(), state.Schedule);
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_before_the_defaults_are_saved_and_the_page_is_told()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig { SettingsPath = Path.Combine(workspace.Root, "settings.json") };
        const string handEdited = """{ "version": 1, "settings": { "csvPath": "/data/books.csv", }, }""";
        File.WriteAllText(config.SettingsPath, handEdited);
        using var backend = new TestBackend(config);

        // What a scheduled run does first; it used to write the defaults over the user's file.
        backend.Settings.UpdateSchedule(state => state);

        var kept = Assert.Single(Directory.GetFiles(workspace.Root, "settings.json.unreadable-*"));
        Assert.Equal(handEdited, File.ReadAllText(kept));
        Assert.Contains(
            SettingsResponse.From(backend.Settings).ServerWarnings,
            warning => warning.StartsWith("The saved settings could not be read") && warning.Contains(kept));
    }

    [Fact]
    public void An_unreadable_file_that_cannot_be_moved_aside_is_never_saved_over()
    {
        // Permission bits do not stop root, and Windows has no Unix modes to set.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var folder = Path.Combine(workspace.Root, "settings");
        Directory.CreateDirectory(folder);
        var config = new ServerConfig { SettingsPath = Path.Combine(folder, "settings.json") };
        File.WriteAllText(config.SettingsPath, "{ not json");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var backend = new TestBackend(config);

            Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out var error));

            Assert.StartsWith("Could not save the settings: ", error!.Message);
            Assert.Equal("{ not json", File.ReadAllText(config.SettingsPath));
            Assert.Contains(
                SettingsResponse.From(backend.Settings).ServerWarnings,
                warning => warning.Contains("will not save over that file"));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("5")]
    public void A_file_that_is_not_a_settings_object_starts_from_the_defaults(string content)
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig { SettingsPath = Path.Combine(workspace.Root, "settings.json") };
        File.WriteAllText(config.SettingsPath, content);

        // This used to throw from the constructor, and the backend never started.
        using var backend = new TestBackend(config);

        Assert.Equal(CopySpeed.Normal, backend.Settings.Effective.CopySpeed);
    }

    [Fact]
    public void A_switch_saved_as_null_does_not_throw_away_the_other_settings()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, """
            { "version": 1, "settings": { "csvPath": "/data/books.csv", "openAtLogin": null, "keepRunningInBackground": true } }
            """);

        var state = new SettingsStore(path, NullLogger<SettingsStore>.Instance).Load();

        Assert.Equal("/data/books.csv", state.Settings.CsvPath);
        Assert.False(state.Settings.OpenAtLogin);
        Assert.True(state.Settings.KeepRunningInBackground);
    }

    [Theory]
    [InlineData("\"keepRunningInBackground\": \"true\"")]
    [InlineData("\"openAtLogin\": 1")]
    [InlineData("\"scheduleIntervalMinutes\": 1440.0")]
    [InlineData("\"copySpeed\": 2")]
    public void A_setting_of_the_wrong_kind_falls_back_on_its_own_and_the_rest_is_saved_again(string mistyped)
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, $$"""
            { "version": 1, "settings": { "sourcePath": "/books", "csvPath": "/x.csv", {{mistyped}} } }
            """);
        var store = new SettingsStore(path, NullLogger<SettingsStore>.Instance);

        var state = store.Load();

        // This used to throw the whole block away, and the next save wrote the defaults over the file.
        Assert.Equal("/books", state.Settings.SourcePath);
        Assert.Equal("/x.csv", state.Settings.CsvPath);
        Assert.True(store.TrySave(state, out _));
        Assert.Contains("/books", File.ReadAllText(path));
    }

    [Fact]
    public void A_settings_entry_that_is_not_an_object_is_kept_aside_and_the_page_is_told()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, """{ "version": 1, "settings": ["/books"] }""");
        var store = new SettingsStore(path, NullLogger<SettingsStore>.Instance);

        Assert.Equal(new AppSettings(), store.Load().Settings);

        Assert.Single(Directory.GetFiles(workspace.Root, "settings.json.unreadable-*"));
        Assert.StartsWith("The saved settings could not be read", store.LoadWarning);
    }

    [Fact]
    public void A_last_run_this_version_cannot_read_keeps_the_schedule_times()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");
        File.WriteAllText(path, """
            { "version": 1, "settings": {},
              "schedule": { "lastAttemptUtc": "2026-01-01T00:00:00Z", "lastSuccessUtc": "2026-01-01T00:00:00Z",
                            "lastRun": { "trigger": "api" } } }
            """);

        var schedule = new SettingsStore(path, NullLogger<SettingsStore>.Instance).Load().Schedule;

        // Losing the times would start an automatic sort at once.
        var expected = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, schedule.LastAttemptUtc);
        Assert.Equal(expected, schedule.LastSuccessUtc);
        Assert.Null(schedule.LastRun);
    }

    [Fact]
    public void A_change_that_cannot_be_saved_is_refused_with_the_reason_and_not_kept()
    {
        using var workspace = new TempWorkspace();
        using var backend = WithUnwritableSettings(workspace);

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out var error));

        Assert.StartsWith("Could not save the settings: ", error!.Message);
        Assert.Equal(CopySpeed.Normal, backend.Settings.Effective.CopySpeed);

        // The page's warning says so, and which folder has to be fixed; it used to promise that
        // changes were kept until a restart.
        var warning = Assert.Single(SettingsResponse.From(backend.Settings).ServerWarnings);
        Assert.Contains("changes to them are refused", warning);
        Assert.Contains($"Check that the folder {Path.Combine(workspace.Root, "not-a-folder")} can be written", warning);
    }

    [Fact]
    public void Automatic_sorting_history_that_cannot_be_saved_is_kept_and_the_page_is_told()
    {
        using var workspace = new TempWorkspace();
        using var backend = WithUnwritableSettings(workspace);

        backend.Settings.UpdateSchedule(state => state with { LastAttemptUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc) });

        // Forgetting it would run the same slot again at once.
        Assert.NotNull(backend.Settings.Schedule.LastAttemptUtc);
        Assert.Contains(
            SettingsResponse.From(backend.Settings).ServerWarnings,
            warning => warning.StartsWith("The settings could not be saved"));
    }

    [Fact]
    public void A_later_save_that_works_clears_the_warning()
    {
        using var workspace = new TempWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        using var backend = new TestBackend(new ServerConfig { SettingsPath = settingsPath });
        Directory.CreateDirectory(settingsPath + ".tmp");

        backend.Settings.UpdateSchedule(state => state with { LastAttemptUtc = DateTime.UtcNow });
        Assert.NotNull(backend.Settings.SaveWarning);

        Directory.Delete(settingsPath + ".tmp");
        backend.Settings.UpdateSchedule(state => state with { LastSuccessUtc = DateTime.UtcNow });
        Assert.Null(backend.Settings.SaveWarning);
    }

    [Fact]
    public void Comparison_mode_from_the_environment_still_applies_after_other_settings_were_saved()
    {
        using var workspace = new TempWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");

        using (var first = new TestBackend(new ServerConfig { SettingsPath = settingsPath, DefaultComparisonMode = FileComparisonMode.Quick }))
        {
            // An automatic run's history is saved without anyone picking an update check.
            first.Settings.UpdateSchedule(state => state with { LastAttemptUtc = DateTime.UtcNow });
        }

        using var restarted = new TestBackend(new ServerConfig { SettingsPath = settingsPath, DefaultComparisonMode = FileComparisonMode.Full });

        Assert.Equal(FileComparisonMode.Full, restarted.Settings.Effective.ComparisonMode);
    }

    [Fact]
    public void Comparison_mode_from_the_environment_is_only_the_first_run_default()
    {
        using var backend = new TestBackend(new ServerConfig { DefaultComparisonMode = FileComparisonMode.Full });

        Assert.Equal(FileComparisonMode.Full, backend.Settings.Effective.ComparisonMode);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { ComparisonMode = "quick" }, out _));
        Assert.Equal(FileComparisonMode.Quick, backend.Settings.Effective.ComparisonMode);
    }

    [Fact]
    public void Paths_and_interval_from_the_environment_are_in_force_but_never_saved()
    {
        using var workspace = new TempWorkspace();
        var config = new ServerConfig
        {
            CsvPath = "/data/books.csv",
            SourcePath = "/source",
            DestinationPath = "/destination",
            ScheduleIntervalMinutes = 360,
            SettingsPath = Path.Combine(workspace.Root, "settings.json")
        };
        using var backend = new TestBackend(config);

        Assert.Equal("/source", backend.Settings.Effective.SourcePath);
        Assert.Equal(360, backend.Settings.Effective.ScheduleIntervalMinutes);

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out _));
        backend.Settings.UpdateSchedule(state => state with { LastAttemptUtc = DateTime.UtcNow });

        var saved = JsonNode.Parse(File.ReadAllText(config.SettingsPath))!["settings"]!;
        Assert.Null(saved["sourcePath"]);
        Assert.Null(saved["scheduleIntervalMinutes"]);
        Assert.Equal("gentle", saved["copySpeed"]!.GetValue<string>());
    }

    [Fact]
    public void A_locked_path_cannot_be_changed_but_may_be_sent_unchanged()
    {
        var config = new ServerConfig { CsvPath = "/data/books.csv", SourcePath = "/source", DestinationPath = "/destination" };
        using var backend = new TestBackend(config);

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { SourcePath = "/elsewhere" }, out var error));
        Assert.Equal("sourcePath", error!.Field);
        Assert.Contains("SOURCE_PATH", error.Message);

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { SourcePath = "/source", CopySpeed = "gentle" }, out _));
    }

    [Fact]
    public void A_locked_path_is_read_the_same_way_from_the_environment_and_from_the_page()
    {
        // One rule (SettingText) for both: a padded variable must not refuse the page sending it back.
        var config = ServerConfig.FromEnvironment(Env(("SOURCE_PATH", "  /source "), ("CSV_PATH", "   ")));
        using var backend = new TestBackend(config);

        Assert.Equal("/source", config.SourcePath);
        Assert.Null(config.CsvPath);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { SourcePath = " /source" }, out var error), error?.Message);
    }

    [Fact]
    public void Turning_automatic_sorting_on_records_when_and_raises_a_change()
    {
        using var workspace = new TempWorkspace();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        using var backend = new TestBackend(new ServerConfig(), time);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch
        {
            CsvPath = workspace.WriteCsv(),
            SourcePath = workspace.Source,
            DestinationPath = workspace.Destination
        }, out _));

        var changes = 0;
        backend.Settings.Changed += (_, _) => changes++;

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = 360 }, out var error), error?.Message);
        Assert.Equal(time.GetUtcNow().UtcDateTime, backend.Settings.Schedule.EnabledAtUtc);
        Assert.Equal(1, changes);

        // Changing the interval of a schedule that is already on is not turning it on.
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = 720 }, out _));
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddHours(-1), backend.Settings.Schedule.EnabledAtUtc);
    }

    [Fact]
    public void Automatic_sorting_cannot_be_turned_on_with_a_destination_that_is_not_there()
    {
        using var workspace = new TempWorkspace();
        var unplugged = Path.Combine(workspace.Root, "unplugged");
        using var backend = new TestBackend(new ServerConfig());
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch
        {
            CsvPath = workspace.WriteCsv(),
            SourcePath = workspace.Source,
            DestinationPath = unplugged
        }, out _));

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = 360 }, out var error));

        Assert.Equal("destinationPath", error!.Field);
        Assert.Equal("destinationMissing", error.Code);
        Assert.Null(backend.Settings.Effective.ScheduleIntervalMinutes);
        Assert.False(Directory.Exists(unplugged));
    }

    [Fact]
    public void A_destination_inside_the_source_is_refused_even_with_automatic_sorting_off()
    {
        using var workspace = new TempWorkspace();
        using var backend = new TestBackend(new ServerConfig());

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch
        {
            SourcePath = workspace.Source,
            DestinationPath = Path.Combine(workspace.Source, "sorted")
        }, out var error));

        Assert.Equal("destinationPath", error!.Field);
        Assert.Null(backend.Settings.Effective.SourcePath);
    }

    [Fact]
    public void A_source_picked_around_the_destination_is_refused_under_the_source_and_named()
    {
        using var workspace = new TempWorkspace();
        using var backend = new TestBackend(new ServerConfig());
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { DestinationPath = workspace.Destination }, out _));

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { SourcePath = workspace.Root }, out var error));

        // The destination was not touched, so blaming it sent people to fix the wrong folder.
        Assert.Equal("sourcePath", error!.Field);
        Assert.Contains(workspace.Root, error.Message);
        Assert.Equal(workspace.Destination, backend.Settings.Effective.DestinationPath);
    }

    [Fact]
    public void Overlapping_server_paths_do_not_block_other_changes_or_turning_automatic_sorting_off()
    {
        using var workspace = new TempWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");
        var csv = workspace.WriteCsv();
        using (var before = new TestBackend(new ServerConfig { SettingsPath = settingsPath }))
        {
            Assert.True(before.Settings.TryUpdate(new AppSettingsPatch
            {
                CsvPath = csv,
                SourcePath = workspace.Source,
                DestinationPath = workspace.Destination,
                ScheduleIntervalMinutes = 360
            }, out var saveError), saveError?.Message);
        }

        // The container's variables were changed since, so that they overlap.
        using var backend = new TestBackend(new ServerConfig
        {
            CsvPath = csv,
            SourcePath = workspace.Source,
            DestinationPath = Path.Combine(workspace.Source, "Organized"),
            SettingsPath = settingsPath
        });

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out var error), error?.Message);
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = null }, out error), error?.Message);
        Assert.Null(backend.Settings.Effective.ScheduleIntervalMinutes);

        // Turning it on again would only fail every 15 minutes.
        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = 360 }, out error));
        Assert.Equal("destinationPath", error!.Field);
    }

    [Fact]
    public void Unrelated_changes_are_saved_while_the_drive_is_unplugged()
    {
        using var workspace = new TempWorkspace();
        using var backend = new TestBackend(new ServerConfig());
        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch
        {
            CsvPath = workspace.WriteCsv(),
            SourcePath = workspace.Source,
            DestinationPath = workspace.Destination,
            ScheduleIntervalMinutes = 360
        }, out var error), error?.Message);

        Directory.Delete(workspace.Destination);

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { CopySpeed = "gentle" }, out error), error?.Message);
    }

    [Fact]
    public void Blank_paths_clear_and_omitted_ones_stay()
    {
        using var workspace = new TempWorkspace();
        using var backend = new TestBackend(new ServerConfig());
        Assert.True(backend.Settings.TryUpdate(
            new AppSettingsPatch { SourcePath = workspace.Source, DestinationPath = workspace.Destination }, out _));

        Assert.True(backend.Settings.TryUpdate(new AppSettingsPatch { SourcePath = "" }, out _));

        Assert.Null(backend.Settings.Effective.SourcePath);
        Assert.Equal(workspace.Destination, backend.Settings.Effective.DestinationPath);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public void An_interval_below_the_minimum_is_refused(int minutes)
    {
        using var backend = new TestBackend(new ServerConfig());

        Assert.False(backend.Settings.TryUpdate(new AppSettingsPatch { ScheduleIntervalMinutes = minutes }, out var error));
        Assert.Equal("scheduleIntervalMinutes", error!.Field);
    }

    [Fact]
    public void Server_config_listens_on_loopback_unless_told_otherwise()
    {
        Assert.Equal("http://127.0.0.1:5123", ServerConfig.FromEnvironment(_ => null).BindUrl);
        Assert.Equal(
            "http://0.0.0.0:5123",
            ServerConfig.FromEnvironment(Env(("ASPNETCORE_URLS", "http://0.0.0.0:5123"))).BindUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData(" ")]
    public void Sort_interval_off_or_unset_leaves_the_schedule_to_the_page(string? value)
    {
        var config = ServerConfig.FromEnvironment(Env(("SORT_INTERVAL", value)));

        Assert.False(config.ScheduleLocked);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void An_unreadable_sort_interval_is_ignored_with_a_warning_and_does_not_lock()
    {
        var config = ServerConfig.FromEnvironment(Env(("SORT_INTERVAL", "6x")));

        Assert.False(config.ScheduleLocked);
        Assert.Contains(config.Warnings, warning => warning.StartsWith("SORT_INTERVAL=\"6x\" was ignored"));
    }

    [Fact]
    public void Any_path_variable_locks_the_paths_and_parallelism_is_read_once()
    {
        var config = ServerConfig.FromEnvironment(Env(("DESTINATION_PATH", "/destination"), ("OABO_MAX_PARALLELISM", "3")));

        Assert.True(config.PathsLocked);
        Assert.Null(config.CsvPath);
        Assert.Equal(3, config.NormalParallelism);
    }

    /// <summary>A backend whose settings file cannot be written: its folder is a file.</summary>
    private static TestBackend WithUnwritableSettings(TempWorkspace workspace)
    {
        var notAFolder = Path.Combine(workspace.Root, "not-a-folder");
        File.WriteAllText(notAFolder, "");
        return new TestBackend(new ServerConfig { SettingsPath = Path.Combine(notAFolder, "settings.json") });
    }

    [Fact]
    public void Only_a_backend_that_listens_on_this_computer_alone_counts_as_the_desktop_one()
    {
        Assert.True(new ServerConfig { BindUrl = "http://127.0.0.1:5123" }.IsLoopbackOnly);
        Assert.True(new ServerConfig { BindUrl = "http://localhost:5123;http://[::1]:5123" }.IsLoopbackOnly);
        Assert.False(new ServerConfig { BindUrl = "http://0.0.0.0:5123" }.IsLoopbackOnly);
        Assert.False(new ServerConfig { BindUrl = "http://+:5123" }.IsLoopbackOnly);
        Assert.False(new ServerConfig { BindUrl = "http://127.0.0.1:5123;http://0.0.0.0:5124" }.IsLoopbackOnly);
    }

    [Fact]
    public void The_app_that_started_the_backend_is_read_from_the_environment()
    {
        Assert.Equal(4242, ServerConfig.FromEnvironment(Env(("OABO_PARENT_PID", "4242"))).ParentProcessId);
        Assert.Null(ServerConfig.FromEnvironment(Env(("OABO_PARENT_PID", "soon"))).ParentProcessId);
        Assert.Null(ServerConfig.FromEnvironment(_ => null).ParentProcessId);
    }

    private static Func<string, string?> Env(params (string Name, string? Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }
}
