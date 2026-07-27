namespace HetznerDrive.Core.Models;

/// <summary>App-wide settings (persisted to settings.json). Contains no secrets.</summary>
public sealed class AppSettings
{
    /// <summary>Optional explicit path to rclone.exe; null = auto-resolve.</summary>
    public string? RcloneExePath { get; set; }

    /// <summary>Default cache settings applied to newly created mappings.</summary>
    public CacheSettings DefaultCache { get; set; } = CacheSettings.Default();

    /// <summary>
    /// Protocol pre-selected for new Storage Box mappings. <see cref="StorageProtocol.Auto"/>
    /// benchmarks the box and uses whichever protocol is actually fastest from this machine.
    /// </summary>
    public StorageProtocol DefaultProtocol { get; set; } = StorageProtocol.Auto;

    /// <summary>
    /// Payload size, in MiB, for the Auto benchmark. Bigger is more accurate on a fast link and
    /// slower to run; the default clears TCP slow start without making mounting feel sluggish.
    /// </summary>
    public int BenchmarkPayloadMiB { get; set; } = 16;

    /// <summary>
    /// Re-run the Auto benchmark on every mount instead of reusing the cached winner. Off by
    /// default — the measurement costs seconds and a link rarely changes character — but useful
    /// on a laptop that moves between very different networks.
    /// </summary>
    public bool AlwaysReBenchmark { get; set; }

    /// <summary>Whether the app is registered to launch at user logon.</summary>
    public bool StartAtLogin { get; set; }

    /// <summary>Minimize to tray instead of exiting when the window is closed.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Check GitHub for a newer release on startup and offer to update.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Run rclone mounts at DEBUG log level (verbose) for troubleshooting.</summary>
    public bool VerboseLogging { get; set; }
}
