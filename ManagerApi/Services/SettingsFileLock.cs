namespace ManagerApi.Services;

/// <summary>
/// Keeps a second backend off the same settings file. Two backends on one file each run their own
/// automatic sorts into the same folders, and each saves its own idea of the settings over the
/// other's, quietly undoing what the user just changed.
///
/// Held as an open, unshared "settings.json.lock" beside the file for as long as the backend runs;
/// the system lets go of it however the process ends, so a crash never leaves it stuck.
/// </summary>
public sealed class SettingsFileLock : IDisposable
{
    /// <summary>
    /// How long to wait for the lock. A backend left behind by an app that crashed notices within
    /// seconds (see <see cref="ParentProcessWatch"/>), and an app opened again straight away should
    /// wait for it rather than fail.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>What the backend exits with when the lock is taken, so the desktop app can explain it without the log.</summary>
    public const int InUseExitCode = 75;

    private readonly FileStream? _stream;

    private SettingsFileLock(FileStream? stream) => _stream = stream;

    /// <summary>
    /// Takes the lock for <paramref name="settingsPath"/>, waiting a little for another backend to let
    /// go. A folder the lock cannot be made in (a read-only volume, say) is only logged: the settings
    /// store reports that folder's problems to the user, and refusing to start would help nobody.
    /// </summary>
    /// <param name="wait">How long to wait for another backend to let go; <see cref="Wait"/> by default.</param>
    /// <returns>The lock, or null (logged) when another backend still holds it.</returns>
    public static SettingsFileLock? Acquire(string? settingsPath, ILogger logger, TimeSpan? wait = null)
    {
        if (settingsPath is null)
        {
            return new SettingsFileLock(null);
        }

        var lockPath = Path.GetFullPath(settingsPath) + ".lock";
        var deadline = DateTime.UtcNow + (wait ?? Wait);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                return new SettingsFileLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException ex) when (IsHeldByAnotherProcess(ex))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    logger.LogCritical(
                        "Another copy of the Book Organizer is already using {Path}; close it, then start this one again",
                        settingsPath);
                    return null;
                }

                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not lock the settings file at {Path}; carrying on without the lock", settingsPath);
                return new SettingsFileLock(null);
            }
        }
    }

    public void Dispose() => _stream?.Dispose();

    /// <summary>Windows reports a sharing or lock violation; macOS and Linux report that the lock would block.</summary>
    private static bool IsHeldByAnotherProcess(IOException ex)
    {
        const int windowsSharingViolation = unchecked((int)0x80070020);
        const int windowsLockViolation = unchecked((int)0x80070021);
        const int linuxWouldBlock = 11;
        const int macWouldBlock = 35;

        return ex.GetType() == typeof(IOException) &&
               ex.HResult is windowsSharingViolation or windowsLockViolation or linuxWouldBlock or macWouldBlock;
    }
}
