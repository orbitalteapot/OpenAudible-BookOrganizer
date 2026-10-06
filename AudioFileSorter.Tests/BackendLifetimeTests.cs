using System.Diagnostics;
using ManagerApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioFileSorter.Tests;

/// <summary>A desktop backend never outlives its app, and never runs twice on one settings file.</summary>
public class BackendLifetimeTests
{
    [Fact]
    public async Task The_backend_stops_once_the_app_that_started_it_has_gone()
    {
        using var parent = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        await parent.WaitForExitAsync();
        using var lifetime = new TestLifetime();
        using var watch = new ParentProcessWatch(
            new ServerConfig { ParentProcessId = parent.Id }, lifetime, NullLogger<ParentProcessWatch>.Instance);

        await watch.StartAsync(CancellationToken.None);

        await TestBackend.WaitUntil(() => lifetime.ApplicationStopping.IsCancellationRequested, "the backend to stop");
    }

    [Fact]
    public async Task The_backend_keeps_running_while_its_app_does()
    {
        using var lifetime = new TestLifetime();
        using var watch = new ParentProcessWatch(
            new ServerConfig { ParentProcessId = Environment.ProcessId }, lifetime, NullLogger<ParentProcessWatch>.Instance);

        await watch.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await watch.StopAsync(CancellationToken.None);

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public void A_second_backend_on_the_same_settings_file_is_refused()
    {
        using var workspace = new TempWorkspace();
        var settingsPath = Path.Combine(workspace.Root, "settings.json");

        using var first = SettingsFileLock.Acquire(settingsPath, NullLogger.Instance);
        Assert.NotNull(first);
        Assert.Null(SettingsFileLock.Acquire(settingsPath, NullLogger.Instance, TimeSpan.FromMilliseconds(300)));

        first!.Dispose();
        using var afterwards = SettingsFileLock.Acquire(settingsPath, NullLogger.Instance, TimeSpan.Zero);
        Assert.NotNull(afterwards);
    }

    [Fact]
    public void A_settings_folder_the_lock_cannot_be_made_in_does_not_stop_the_backend()
    {
        using var workspace = new TempWorkspace();
        var notAFolder = Path.Combine(workspace.Root, "not-a-folder");
        File.WriteAllText(notAFolder, "");

        using var settingsLock = SettingsFileLock.Acquire(Path.Combine(notAFolder, "settings.json"), NullLogger.Instance, TimeSpan.Zero);

        Assert.NotNull(settingsLock);
    }
}
