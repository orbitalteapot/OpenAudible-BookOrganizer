using System.Collections.Concurrent;
using ManagerApi.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioFileSorter.Tests;

/// <summary>
/// The backend's services wired together the way Program.cs wires them, without a web host, so
/// the service and scheduler tests can control the clock and the app's shutdown.
/// </summary>
public sealed class TestBackend : IDisposable
{
    public TestBackend(ServerConfig config, TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        Settings = new SettingsService(
            config, new SettingsStore(config.SettingsPath, NullLogger<SettingsStore>.Instance), Time);
        Sort = new SortService(Settings, Time, Lifetime, NullLogger<SortService>.Instance);
    }

    public TimeProvider Time { get; }
    public TestLifetime Lifetime { get; } = new();
    public SettingsService Settings { get; }
    public SortService Sort { get; }

    /// <summary>A backend whose three paths are fixed, as a container's would be.</summary>
    public static TestBackend LockedTo(
        TempWorkspace workspace, string csvPath, int? intervalMinutes = null, string? destination = null, TimeProvider? time = null)
    {
        return new TestBackend(
            new ServerConfig
            {
                CsvPath = csvPath,
                SourcePath = workspace.Source,
                DestinationPath = destination ?? workspace.Destination,
                ScheduleIntervalMinutes = intervalMinutes
            },
            time);
    }

    /// <summary>What the scheduler created by <see cref="CreateScheduler"/> has logged, so a test can see it join a run.</summary>
    public RecordingLogger<SortScheduler> SchedulerLog { get; } = new();

    public SortScheduler CreateScheduler()
    {
        return new SortScheduler(Sort, Settings, Time, SchedulerLog);
    }

    public void Dispose()
    {
        // Leaves no run copying into a workspace that is about to be deleted.
        Sort.CancelSort();
        Lifetime.Dispose();
    }

    /// <summary>
    /// Holds every run once it has sorted its first book, until it is cancelled. Without it a test
    /// that cancels a running sort races the sort: on a two-core CI runner the test thread can go
    /// unscheduled until every book is done, leaving nothing to cancel.
    /// </summary>
    public static void HoldRunsUntilCanceled(SortService sort)
    {
        sort.AfterProgress = (info, token) =>
        {
            if (info.CurrentBook > 0)
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            }
        };
    }

    /// <inheritdoc cref="HoldRunsUntilCanceled(SortService)"/>
    public TestBackend HoldRunsUntilCanceled()
    {
        HoldRunsUntilCanceled(Sort);
        return this;
    }

    /// <summary>Polls until <paramref name="condition"/> holds, failing the test after a few seconds.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>An app lifetime a test can stop, to see what the backend does when the app closes.</summary>
public sealed class TestLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication() => _stopping.Cancel();

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}

/// <summary>A logger that keeps every message, for tests that wait on something only the log shows.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _messages.Enqueue(formatter(state, exception));
    }
}
