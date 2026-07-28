using System.Runtime.Versioning;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Microsoft.Extensions.Logging;

namespace HetznerDrive.Service;

/// <summary>
/// Drives the mounts the service is responsible for towards the state published in
/// <see cref="ServiceConfigStore"/>.
///
/// It is written as a converge-on-desired-state loop rather than as a queue of mount/unmount
/// commands. The service can be restarted, the machine can reboot, and rclone can die mid-flight;
/// in every case the correct recovery is the same — read what should be mounted, compare it with
/// what is mounted, fix the difference. There is no message history to lose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountReconciler : IAsyncDisposable
{
    private readonly ServiceConfigStore _config;
    private readonly ILogger _logger;
    private readonly MountManager? _mounts;
    private readonly string? _rcloneError;

    /// <summary>What each mapping id is currently mounted with, so a config edit can be detected.</summary>
    private readonly Dictionary<Guid, string> _applied = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public MountReconciler(ServiceConfigStore config, ILogger logger, string? rcloneExePath)
    {
        _config = config;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(rcloneExePath))
        {
            _rcloneError = "rclone.exe was not found next to the service executable.";
            return;
        }

        _mounts = new MountManager(rcloneExePath);
        _mounts.StatusChanged += (_, e) =>
        {
            if (e.Message is not null)
                _logger.LogInformation("[{Mapping}] {State}: {Message}", e.MappingId, e.State, e.Message);
        };
        _mounts.LogReceived += (_, e) => _logger.LogDebug("{Line}", e.Line);
    }

    /// <summary>
    /// Brings mounts in line with the published configuration. Serialised, because a burst of file
    /// writes can fire several change notifications for what is really one edit.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ReconcileCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ReconcileCoreAsync(CancellationToken ct)
    {
        if (_mounts is null)
        {
            _logger.LogError("{Error} No mounts can be started.", _rcloneError);
            return;
        }

        var desired = _config.LoadMappings()
            .Where(IsServiceable)
            .ToDictionary(m => m.Id);

        var credentials = _config.LoadCredentials();

        // Gone from the configuration, or edited: drop the old mount first so the mountpoint is
        // free before the replacement claims it.
        foreach (var id in _applied.Keys.ToArray())
        {
            var stillWanted = desired.TryGetValue(id, out var mapping)
                              && _applied[id] == Fingerprint(mapping);
            if (stillWanted) continue;

            _logger.LogInformation("Unmounting {Mapping}.", id);
            try
            {
                await _mounts.UnmountAsync(id, ct).ConfigureAwait(false);
                _applied.Remove(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unmounting {Mapping} failed.", id);
            }
        }

        foreach (var (id, mapping) in desired)
        {
            ct.ThrowIfCancellationRequested();
            if (_applied.ContainsKey(id) && _mounts.IsMounted(id)) continue;

            var creds = credentials.Get(id);
            if (creds is null || !creds.IsCompleteFor(mapping.Product))
            {
                _logger.LogError(
                    "No usable credentials for '{Name}' ({Mapping}). Re-save it in HetznerDrive to republish.",
                    mapping.Name, id);
                continue;
            }

            try
            {
                _logger.LogInformation("Mounting '{Name}' at {MountPoint}.", mapping.Name, mapping.MountPoint);
                await _mounts.MountAsync(mapping, creds, ct).ConfigureAwait(false);
                _applied[id] = Fingerprint(mapping);
                _logger.LogInformation("Mounted '{Name}' at {MountPoint} over {Protocol}.",
                    mapping.Name, mapping.MountPoint, mapping.EffectiveProtocol);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One bad mapping must not stop the others; the next reconcile retries it.
                _logger.LogError(ex, "Mounting '{Name}' failed.", mapping.Name);
            }
        }
    }

    /// <summary>
    /// Rejects configurations the service cannot honour, rather than failing obscurely later.
    ///
    /// A drive letter is fine here, despite drive letters normally being per-logon-session: the
    /// service runs as LocalSystem, and a DOS device created by SYSTEM lands in the global
    /// namespace, so the letter is visible to every interactive user. Only the mountpoint being
    /// absent is fatal.
    /// </summary>
    private bool IsServiceable(Mapping mapping)
    {
        if (mapping.Mode != MappingMode.DriveLetter)
        {
            _logger.LogWarning(
                "Skipping '{Name}': Files On-Demand folders need an interactive session and cannot be serviced.",
                mapping.Name);
            return false;
        }
        // MountPoint is never blank for a letter target — an unset letter still yields ":" — so the
        // two forms have to be checked at the source rather than through it.
        var isDirectory = mapping.MountTarget == MountTarget.Directory;
        var configured = isDirectory
            ? !string.IsNullOrWhiteSpace(mapping.MountDirectory)
            : !string.IsNullOrWhiteSpace(mapping.DriveLetter?.TrimEnd(':'));
        if (!configured)
        {
            _logger.LogWarning("Skipping '{Name}': no {Target} is configured.",
                mapping.Name, isDirectory ? "mount directory" : "drive letter");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Everything that would require a remount if it changed. Compared as a string so an edited
    /// mapping is remounted while an unrelated edit (a rename, say) is not.
    /// </summary>
    private static string Fingerprint(Mapping m) => string.Join('|',
        m.RemoteTarget, m.MountPoint, m.EffectiveProtocol, m.Host, m.Username, m.SshPort,
        m.Cache.CacheMode, m.Cache.VfsCacheMaxSizeMb, m.Cache.VfsCacheMaxAgeSeconds,
        m.Cache.DirCacheTimeSeconds, m.Cache.BufferSizeMb, m.Cache.CacheDir);

    /// <summary>Unmounts everything on shutdown so mountpoints do not linger after the service stops.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_mounts is not null)
        {
            try { await _mounts.UnmountAllAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Unmounting everything on shutdown failed."); }
            await _mounts.DisposeAsync().ConfigureAwait(false);
        }
        _gate.Dispose();
    }
}
