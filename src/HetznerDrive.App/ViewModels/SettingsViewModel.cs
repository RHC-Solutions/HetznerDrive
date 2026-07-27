using CommunityToolkit.Mvvm.ComponentModel;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.ViewModels;

/// <summary>Backing model for the Settings dialog.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        StartAtLogin = settings.StartAtLogin;
        MinimizeToTray = settings.MinimizeToTray;
        AutoCheckForUpdates = settings.AutoCheckForUpdates;
        VerboseLogging = settings.VerboseLogging;
        RcloneExePath = settings.RcloneExePath ?? string.Empty;

        DefaultProtocol = settings.DefaultProtocol;
        BenchmarkPayloadMiB = settings.BenchmarkPayloadMiB;
        AlwaysReBenchmark = settings.AlwaysReBenchmark;

        DefaultCacheMode = settings.DefaultCache.CacheMode;
        DefaultCacheMaxSizeMb = settings.DefaultCache.VfsCacheMaxSizeMb;
        DefaultCacheMaxAgeHours = settings.DefaultCache.VfsCacheMaxAge.TotalHours;
        DefaultCacheDir = settings.DefaultCache.CacheDir ?? string.Empty;
    }

    public IReadOnlyList<VfsCacheMode> CacheModes { get; } = Enum.GetValues<VfsCacheMode>();

    /// <summary>Protocols a new Storage Box mapping can default to (S3 is Object Storage only).</summary>
    public IReadOnlyList<StorageProtocol> Protocols { get; } = new[]
    {
        StorageProtocol.Auto,
        StorageProtocol.Sftp,
        StorageProtocol.Smb,
        StorageProtocol.WebDav,
    };

    [ObservableProperty] private bool _startAtLogin;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private string _rcloneExePath = string.Empty;
    [ObservableProperty] private StorageProtocol _defaultProtocol = StorageProtocol.Auto;
    [ObservableProperty] private int _benchmarkPayloadMiB = 16;
    [ObservableProperty] private bool _alwaysReBenchmark;
    [ObservableProperty] private VfsCacheMode _defaultCacheMode;
    [ObservableProperty] private int _defaultCacheMaxSizeMb;
    [ObservableProperty] private double _defaultCacheMaxAgeHours;
    [ObservableProperty] private string _defaultCacheDir = string.Empty;

    /// <summary>Writes the edited values back into <paramref name="settings"/>.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.StartAtLogin = StartAtLogin;
        settings.MinimizeToTray = MinimizeToTray;
        settings.AutoCheckForUpdates = AutoCheckForUpdates;
        settings.VerboseLogging = VerboseLogging;
        settings.RcloneExePath = string.IsNullOrWhiteSpace(RcloneExePath) ? null : RcloneExePath.Trim();
        settings.DefaultProtocol = DefaultProtocol;
        settings.BenchmarkPayloadMiB = Math.Clamp(BenchmarkPayloadMiB, 1, 512);
        settings.AlwaysReBenchmark = AlwaysReBenchmark;
        settings.DefaultCache.CacheMode = DefaultCacheMode;
        settings.DefaultCache.VfsCacheMaxSizeMb = DefaultCacheMaxSizeMb;
        settings.DefaultCache.VfsCacheMaxAge = TimeSpan.FromHours(Math.Max(0, DefaultCacheMaxAgeHours));
        settings.DefaultCache.CacheDir = string.IsNullOrWhiteSpace(DefaultCacheDir) ? null : DefaultCacheDir.Trim();
    }
}
