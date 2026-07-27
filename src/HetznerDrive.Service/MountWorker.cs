using System.Runtime.Versioning;
using HetznerDrive.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HetznerDrive.Service;

/// <summary>
/// The service's main loop: reconcile once at startup, again whenever the published configuration
/// changes, and periodically as a backstop.
///
/// The periodic sweep is not redundant with the file watcher. It also recovers mounts whose rclone
/// process died in a way <see cref="MountManager"/>'s own restart budget could not fix, and it
/// covers the case where a change notification was missed entirely — a file watcher on a directory
/// is not a guarantee.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountWorker : BackgroundService
{
    /// <summary>Coalescing window for change notifications; one save can raise several events.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>Backstop sweep interval.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<MountWorker> _logger;
    private readonly ServiceConfigStore _config = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private FileSystemWatcher? _watcher;

    public MountWorker(ILogger<MountWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MachinePaths.EnsureCreated();

        var rclone = AppPaths.ResolveRcloneExe();
        _logger.LogInformation("HetznerDrive service starting. Config: {Path}. rclone: {Rclone}",
            _config.MappingsPath, rclone ?? "(not found)");

        await using var reconciler = new MountReconciler(_config, _logger, rclone);

        StartWatching();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // The loop must outlive any single failure, or one bad reconcile would leave
                    // the service running but permanently idle.
                    _logger.LogError(ex, "Reconcile failed; will retry.");
                }

                // Wake early on a config change, otherwise sweep on the timer.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(SweepInterval);
                try { await _wake.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timer elapsed: fall through to the backstop sweep.
                }
                catch (OperationCanceledException) { break; }

                if (stoppingToken.IsCancellationRequested) break;
                // Let a burst of writes settle so a half-written file isn't read.
                try { await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _watcher?.Dispose();
            _logger.LogInformation("HetznerDrive service stopping; unmounting everything.");
        }
    }

    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(_config.WatchDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnConfigChanged;
            _watcher.Created += OnConfigChanged;
            _watcher.Deleted += OnConfigChanged;
            _watcher.Renamed += OnConfigChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Without the watcher the service still works, just on the sweep interval.
            _logger.LogWarning(ex, "Could not watch {Dir}; falling back to periodic reconciles only.",
                _config.WatchDirectory);
        }
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        // The log directory lives under the same root; ignore anything that is not the config.
        if (!e.Name?.StartsWith("service-", StringComparison.OrdinalIgnoreCase) ?? true) return;

        // Release only if nobody is already holding a pending wake — the semaphore caps at one, and
        // a second Release would throw rather than coalesce.
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _wake.Dispose();
        base.Dispose();
    }
}
