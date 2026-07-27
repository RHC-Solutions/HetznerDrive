using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HetznerDrive.CloudFiles;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.ViewModels;

/// <summary>Backing model for the Add/Edit mapping dialog.</summary>
public sealed partial class MappingEditViewModel : ObservableObject
{
    private readonly Guid _id;

    /// <summary>Folders other mappings already occupy; two sync roots must not overlap.</summary>
    private readonly IReadOnlyList<string> _otherFolders;

    /// <summary>The folder this mapping was already registered at, if it is an existing on-demand one.</summary>
    private readonly string? _originalFolder;

    /// <summary>Carried across an edit so a previously measured Auto winner isn't thrown away.</summary>
    private readonly StorageProtocol? _resolvedProtocol;
    private readonly DateTime? _protocolMeasuredUtc;

    public MappingEditViewModel(Mapping? existing, HetznerCredentials? credentials, AppSettings settings,
        IEnumerable<string>? otherFolders = null)
    {
        _id = existing?.Id ?? Guid.NewGuid();
        IsNew = existing is null;
        _otherFolders = otherFolders?.ToList() ?? new List<string>();
        _originalFolder = existing?.Mode == MappingMode.OnDemandFolder
            ? OnDemandSyncManager.ResolveFolderPath(existing)
            : null;
        _resolvedProtocol = existing?.ResolvedProtocol;
        _protocolMeasuredUtc = existing?.ProtocolMeasuredUtc;

        var m = existing ?? new Mapping
        {
            Cache = settings.DefaultCache.Clone(),
            Protocol = settings.DefaultProtocol,
        };

        Name = m.Name;
        Product = m.Product;
        Protocol = m.Protocol;
        Username = m.Username;
        HostOverride = m.HostOverride ?? string.Empty;
        SshPort = m.SshPort;
        BucketName = m.BucketName;
        LocationCode = m.LocationCode;
        SubPath = m.SubPath ?? string.Empty;
        AutoMount = m.AutoMount;
        MountTarget = m.MountTarget;
        MountDirectory = m.MountDirectory ?? string.Empty;
        RunAsService = m.RunAsService;
        // New mappings default to the on-demand folder (OneDrive/Google-Drive style); existing
        // mappings keep whatever mode they were saved with.
        Mode = existing?.Mode ?? MappingMode.OnDemandFolder;
        LocalFolderPath = m.LocalFolderPath ?? string.Empty;

        CacheMode = m.Cache.CacheMode;
        VfsCacheMaxSizeMb = m.Cache.VfsCacheMaxSizeMb;
        VfsCacheMaxAgeHours = m.Cache.VfsCacheMaxAge.TotalHours;
        DirCacheTimeMinutes = m.Cache.DirCacheTime.TotalMinutes;
        BufferSizeMb = m.Cache.BufferSizeMb;
        CacheDir = m.Cache.CacheDir ?? string.Empty;

        Password = credentials?.Password ?? string.Empty;
        SshKeyFile = credentials?.SshKeyFile ?? string.Empty;
        SshKeyPassphrase = credentials?.SshKeyPassphrase ?? string.Empty;
        AccessKeyId = credentials?.AccessKeyId ?? string.Empty;
        SecretAccessKey = credentials?.SecretAccessKey ?? string.Empty;

        AvailableDriveLetters = BuildDriveLetters(m.DriveLetter);
        DriveLetter = string.IsNullOrWhiteSpace(m.DriveLetter) || !AvailableDriveLetters.Contains(m.DriveLetter)
            ? AvailableDriveLetters.FirstOrDefault() ?? "H"
            : m.DriveLetter;
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Add mapping" : "Edit mapping";

    public IReadOnlyList<HetznerProduct> Products { get; } = Enum.GetValues<HetznerProduct>();
    public IReadOnlyList<ObjectStorageLocation> Locations => HetznerEndpoints.ObjectStorageLocations;
    public IReadOnlyList<VfsCacheMode> CacheModes { get; } = Enum.GetValues<VfsCacheMode>();
    public IReadOnlyList<MappingMode> Modes { get; } = Enum.GetValues<MappingMode>();
    public IReadOnlyList<string> AvailableDriveLetters { get; }

    /// <summary>
    /// Protocols offered for a Storage Box. S3 is deliberately absent: a Storage Box has no S3
    /// endpoint, and offering it would just produce a confusing failure at mount time.
    /// </summary>
    public IReadOnlyList<StorageProtocol> StorageBoxProtocols { get; } = new[]
    {
        StorageProtocol.Auto,
        StorageProtocol.Sftp,
        StorageProtocol.Smb,
        StorageProtocol.WebDav,
    };

    public bool IsStorageBox => Product == HetznerProduct.StorageBox;
    public bool IsObjectStorage => Product == HetznerProduct.ObjectStorage;

    /// <summary>True when the mapping uses a virtual drive letter (controls which fields show).</summary>
    public bool IsDriveLetterMode => Mode == MappingMode.DriveLetter;
    public bool IsOnDemandMode => Mode == MappingMode.OnDemandFolder;

    /// <summary>SSH-key fields only make sense for a Storage Box reached over SFTP.</summary>
    public bool ShowSshKeyFields => IsStorageBox && Protocol is StorageProtocol.Auto or StorageProtocol.Sftp;

    public string ProductDescription => Product == HetznerProduct.StorageBox
        ? "A Storage Box (uNNNNNN.your-storagebox.de) reached over SFTP, SMB or WebDAV. This is the "
          + "product with a fixed size and a single password — it has no S3 endpoint."
        : "Hetzner Object Storage: S3-compatible buckets billed per GB, with their own access key "
          + "and secret. A different product from a Storage Box.";

    public string ProtocolDescription => Protocol switch
    {
        StorageProtocol.Auto =>
            "Measures SFTP, SMB and WebDAV against your box and uses whichever is actually fastest "
            + "from this machine. Recommended — the winner depends on your ISP and firewall as much "
            + "as on Hetzner.",
        StorageProtocol.Sftp =>
            "SSH file transfer on port 23. Always enabled, keeps real timestamps, and works with an "
            + "SSH key. Limited to about 10 concurrent connections per box.",
        StorageProtocol.Smb =>
            "Windows file sharing on port 445. Quick on a low-latency link, but must be enabled in "
            + "the Hetzner console and is blocked by many ISPs.",
        StorageProtocol.WebDav =>
            "HTTPS on port 443. The most likely to get through a restrictive firewall, and usually "
            + "the slowest of the three.",
        _ => string.Empty,
    };

    /// <summary>The hostname this mapping will connect to, shown so a typo is visible up front.</summary>
    public string ResolvedHost => IsObjectStorage
        ? HetznerEndpoints.FindLocation(LocationCode)?.Endpoint ?? string.Empty
        : !string.IsNullOrWhiteSpace(HostOverride)
            ? HostOverride.Trim()
            : HetznerEndpoints.StorageBoxHost(Username);

    /// <summary>The SMB share that would be used — "backup", or the sub-account's own name.</summary>
    public string ResolvedShare => HetznerEndpoints.SmbShare(Username);

    /// <summary>
    /// The folder that will actually be used, with the default filled in when no custom location is
    /// set. Shown in the dialog so the location is never a mystery — the same thing OneDrive does by
    /// always displaying its folder path rather than leaving the field blank.
    /// </summary>
    public string EffectiveFolderPath => OnDemandSyncManager.ResolveFolderPath(BuildMapping());

    /// <summary>True when a location has been chosen explicitly rather than inherited from the default.</summary>
    public bool UsesCustomFolder => !string.IsNullOrWhiteSpace(LocalFolderPath);

    /// <summary>True when saving would move an existing mapping's folder, orphaning the old one.</summary>
    public bool FolderIsMoving =>
        _originalFolder is not null &&
        !string.Equals(_originalFolder.TrimEnd('\\'), EffectiveFolderPath.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder this mapping currently occupies, for the "moving" warning.</summary>
    public string? OriginalFolderPath => _originalFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    [NotifyPropertyChangedFor(nameof(FolderIsMoving))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStorageBox))]
    [NotifyPropertyChangedFor(nameof(IsObjectStorage))]
    [NotifyPropertyChangedFor(nameof(ProductDescription))]
    [NotifyPropertyChangedFor(nameof(ShowSshKeyFields))]
    [NotifyPropertyChangedFor(nameof(ResolvedHost))]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    private HetznerProduct _product = HetznerProduct.StorageBox;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtocolDescription))]
    [NotifyPropertyChangedFor(nameof(ShowSshKeyFields))]
    private StorageProtocol _protocol = StorageProtocol.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedHost))]
    [NotifyPropertyChangedFor(nameof(ResolvedShare))]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedHost))]
    private string _hostOverride = string.Empty;

    [ObservableProperty] private int _sshPort = HetznerEndpoints.SshPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    private string _bucketName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedHost))]
    private string _locationCode = "fsn1";

    [ObservableProperty] private string _subPath = string.Empty;
    [ObservableProperty] private string _driveLetter = "H";
    [ObservableProperty] private bool _autoMount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectoryMount))]
    [NotifyPropertyChangedFor(nameof(IsLetterMount))]
    [NotifyPropertyChangedFor(nameof(MountTargetDescription))]
    [NotifyPropertyChangedFor(nameof(EffectiveMountDirectory))]
    private MountTarget _mountTarget = MountTarget.DriveLetter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMountDirectory))]
    private string _mountDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceDescription))]
    private bool _runAsService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriveLetterMode))]
    [NotifyPropertyChangedFor(nameof(IsOnDemandMode))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    [NotifyPropertyChangedFor(nameof(ShowMountTarget))]
    private MappingMode _mode = MappingMode.OnDemandFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    [NotifyPropertyChangedFor(nameof(UsesCustomFolder))]
    [NotifyPropertyChangedFor(nameof(FolderIsMoving))]
    private string _localFolderPath = string.Empty;

    [ObservableProperty] private VfsCacheMode _cacheMode = VfsCacheMode.Full;
    [ObservableProperty] private int _vfsCacheMaxSizeMb = 50 * 1024;
    [ObservableProperty] private double _vfsCacheMaxAgeHours = 24;
    [ObservableProperty] private double _dirCacheTimeMinutes = 1;
    [ObservableProperty] private int _bufferSizeMb = 32;
    [ObservableProperty] private string _cacheDir = string.Empty;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _sshKeyFile = string.Empty;
    [ObservableProperty] private string _sshKeyPassphrase = string.Empty;
    [ObservableProperty] private string _accessKeyId = string.Empty;
    [ObservableProperty] private string _secretAccessKey = string.Empty;

    /// <summary>Result of the last "Test speeds" run, rendered under the protocol dropdown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBenchmarkResult))]
    private string _benchmarkResult = string.Empty;

    public bool HasBenchmarkResult => !string.IsNullOrWhiteSpace(BenchmarkResult);

    public IReadOnlyList<MountTarget> MountTargets { get; } = Enum.GetValues<MountTarget>();

    public bool IsLetterMount => MountTarget == MountTarget.DriveLetter;
    public bool IsDirectoryMount => MountTarget == MountTarget.Directory;

    /// <summary>Mount-target options only apply to a drive mapping, not an on-demand folder.</summary>
    public bool ShowMountTarget => IsDriveLetterMode;

    /// <summary>The directory that will be used, with the default filled in when none is set.</summary>
    public string EffectiveMountDirectory => string.IsNullOrWhiteSpace(MountDirectory)
        ? BuildMapping().DefaultMountDirectory
        : MountDirectory.Trim();

    public string MountTargetDescription => MountTarget == MountTarget.Directory
        ? @"A folder such as C:\HetznerDrive\Backups. Visible from every session, which is what "
          + "lets the Windows service host it. WinFsp creates the folder on mount, so it must not "
          + "already exist."
        : "A drive letter such as H:. Only exists inside your own logon session — a service "
          + "cannot provide one.";

    public string ServiceDescription => RunAsService
        ? "Mounted by the HetznerDrive Windows service, so it is available before you sign in and "
          + "survives logoff. Requires a directory mountpoint, and administrator approval when saving."
        : "Mounted by this app while you are signed in.";

    /// <summary>Helper text shown under the Mode dropdown explaining the selected mode.</summary>
    public string ModeDescription => Mode == MappingMode.OnDemandFolder
        ? "A normal folder in Explorer with cloud placeholders — files download only when opened, "
          + "with pin / free-up-space and no drive letter. Recommended."
        : "A virtual drive (e.g. H:) backed by rclone + WinFsp. The whole remote appears as a mapped drive.";

    /// <summary>Returns an error message if the form is invalid, otherwise null.</summary>
    public string? Validate()
    {
        if (IsStorageBox)
        {
            if (string.IsNullOrWhiteSpace(Username))
                return "The Storage Box username (for example u123456) is required.";
            if (SshPort is <= 0 or > 65535)
                return "The SSH port must be between 1 and 65535.";
            if (string.IsNullOrWhiteSpace(Password) && string.IsNullOrWhiteSpace(SshKeyFile))
                return "Enter the Storage Box password, or choose an SSH key file.";
            if (!string.IsNullOrWhiteSpace(SshKeyFile) && !File.Exists(SshKeyFile.Trim()))
                return "The SSH key file was not found.";
            // SMB and WebDAV authenticate with the password only, so a key-only login cannot use
            // them — and Auto would have nothing left to measure but SFTP.
            if (string.IsNullOrWhiteSpace(Password) && Protocol is StorageProtocol.Smb or StorageProtocol.WebDav)
                return $"{Protocol} needs the Storage Box password; an SSH key only works for SFTP.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(BucketName))
                return "The bucket name is required.";
            if (HetznerEndpoints.FindLocation(LocationCode) is null)
                return "Choose an Object Storage location.";
            if (string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
                return "Object Storage needs an access key and a secret key.";
        }

        if (IsDriveLetterMode && IsLetterMount && string.IsNullOrWhiteSpace(DriveLetter))
            return "A drive letter is required.";
        if (IsDriveLetterMode && IsDirectoryMount)
        {
            var dir = EffectiveMountDirectory;
            if (!Path.IsPathFullyQualified(dir))
                return @"Enter a full path for the mount folder, for example C:\HetznerDrive\Backups.";
            // WinFsp creates the mountpoint itself and removes it on unmount, so an existing
            // directory is not merely untidy — the mount will refuse to start.
            if (Directory.Exists(dir))
                return $"'{dir}' already exists. A directory mountpoint is created by the mount "
                     + "itself, so choose a path that does not exist yet.";
            if (File.Exists(dir))
                return $"'{dir}' is a file.";
        }
        if (RunAsService)
        {
            if (!IsDriveLetterMode)
                return "Files On-Demand folders need an interactive session and cannot be hosted by "
                     + "the Windows service. Use drive-letter mode, or untick the service option.";
            if (!IsDirectoryMount)
                return "The Windows service can only use a directory mountpoint — a drive letter "
                     + "mounted by a service is invisible to your session.";
        }
        if (IsOnDemandMode && OnDemandFolderRules.Validate(LocalFolderPath, _otherFolders) is { } folderError)
            return folderError;
        return null;
    }

    /// <summary>Resets the folder to the default location under the user profile.</summary>
    public void UseDefaultFolder() => LocalFolderPath = string.Empty;

    /// <summary>
    /// Sets the folder from a parent directory the user picked, creating a named subfolder inside it
    /// the way OneDrive does — so choosing "D:\" yields "D:\&lt;name&gt;" rather than turning the whole
    /// volume into a sync root.
    /// </summary>
    public void SetFolderFromParent(string parentDirectory)
    {
        var leaf = !string.IsNullOrWhiteSpace(Name) ? Name
            : IsObjectStorage ? BucketName
            : Username;
        LocalFolderPath = OnDemandFolderRules.CombineForMapping(parentDirectory, leaf);
    }

    public Mapping BuildMapping() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Product = Product,
        Protocol = IsObjectStorage ? StorageProtocol.S3 : Protocol,
        // A saved measurement survives an edit; changing the cache size shouldn't cost a re-probe.
        ResolvedProtocol = _resolvedProtocol,
        ProtocolMeasuredUtc = _protocolMeasuredUtc,
        Username = Username.Trim(),
        HostOverride = string.IsNullOrWhiteSpace(HostOverride) ? null : HostOverride.Trim(),
        SshPort = SshPort,
        BucketName = BucketName.Trim(),
        LocationCode = LocationCode,
        SubPath = string.IsNullOrWhiteSpace(SubPath) ? null : SubPath.Trim(),
        DriveLetter = DriveLetter.TrimEnd(':'),
        MountTarget = MountTarget,
        MountDirectory = string.IsNullOrWhiteSpace(MountDirectory) ? null : MountDirectory.Trim(),
        RunAsService = RunAsService && Mode == MappingMode.DriveLetter,
        AutoMount = AutoMount,
        Mode = Mode,
        LocalFolderPath = IsOnDemandMode && !string.IsNullOrWhiteSpace(LocalFolderPath)
            ? LocalFolderPath.Trim()
            : null,
        Cache = new CacheSettings
        {
            CacheMode = CacheMode,
            VfsCacheMaxSizeMb = VfsCacheMaxSizeMb,
            VfsCacheMaxAge = TimeSpan.FromHours(Math.Max(0, VfsCacheMaxAgeHours)),
            DirCacheTime = TimeSpan.FromMinutes(Math.Max(0, DirCacheTimeMinutes)),
            BufferSizeMb = BufferSizeMb,
            CacheDir = string.IsNullOrWhiteSpace(CacheDir) ? null : CacheDir.Trim(),
        },
    };

    public HetznerCredentials BuildCredentials() => new()
    {
        Password = Password,
        SshKeyFile = string.IsNullOrWhiteSpace(SshKeyFile) ? null : SshKeyFile.Trim(),
        SshKeyPassphrase = string.IsNullOrWhiteSpace(SshKeyPassphrase) ? null : SshKeyPassphrase,
        AccessKeyId = AccessKeyId.Trim(),
        SecretAccessKey = SecretAccessKey.Trim(),
    };

    private static List<string> BuildDriveLetters(string current)
    {
        var used = DriveInfo.GetDrives()
            .Select(d => d.Name.TrimEnd('\\', ':').ToUpperInvariant())
            .ToHashSet();
        var free = new List<string>();
        for (var c = 'D'; c <= 'Z'; c++)
        {
            var letter = c.ToString();
            if (!used.Contains(letter) || string.Equals(letter, current, StringComparison.OrdinalIgnoreCase))
                free.Add(letter);
        }
        return free;
    }
}
