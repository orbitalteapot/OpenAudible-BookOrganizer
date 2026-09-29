using System.Diagnostics;

namespace ManagerApi.Services;

/// <summary>
/// Stops the backend once the desktop app that started it has gone.
///
/// The app stops its backend when it quits normally, but a crash, a force-quit or an out-of-memory
/// kill skips that. The backend would then keep running automatic sorts on its own timer where
/// nobody can see or cancel them, while the next launch starts a second one on the same folders and
/// the same settings file.
/// </summary>
public sealed class ParentProcessWatch(
    ServerConfig config,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatch> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.ParentProcessId is not { } parentId)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(parentId);
            await parent.WaitForExitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The backend is stopping anyway.
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone before the backend got this far.
        }

        logger.LogWarning("The app that started the backend (process {ParentId}) has closed; stopping", parentId);
        lifetime.StopApplication();
    }
}
