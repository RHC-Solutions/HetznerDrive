namespace HetznerDrive.Core.Models;

/// <summary>Live mount state for a mapping.</summary>
public enum MountState
{
    Unmounted,
    Mounting,
    Mounted,
    Unmounting,
    Error,
}

/// <summary>How storage is surfaced to the user.</summary>
public enum MappingMode
{
    /// <summary>Virtual drive letter backed by rclone mount + WinFsp.</summary>
    DriveLetter,

    /// <summary>
    /// A normal folder on disk with Windows "Files On-Demand" placeholders (Cloud Files API):
    /// files show in Explorer but download only when opened, with pin / free-up-space support.
    /// </summary>
    OnDemandFolder,
}

/// <summary>
/// Where a <see cref="MappingMode.DriveLetter"/> mapping attaches itself.
///
/// Both forms work under the Windows service. A drive letter is normally per-logon-session, but
/// the service runs as LocalSystem and a DOS device created by SYSTEM is written to the global
/// namespace, so interactive users see it too. The forms differ in behaviour, not in reach:
/// only a letter can be presented as a network drive (see <c>--network-mode</c> in
/// <see cref="RcloneRunner.BuildMountArguments"/>).
/// </summary>
public enum MountTarget
{
    /// <summary>A drive letter such as <c>H:</c>, mounted as a network drive.</summary>
    DriveLetter,

    /// <summary>
    /// An empty NTFS directory such as <c>C:\HetznerDrive\Backups</c>, mounted as a fixed disk.
    /// </summary>
    Directory,
}

/// <summary>
/// A persisted storage → local mapping. Contains no secret material; the matching
/// <see cref="HetznerCredentials"/> are looked up separately by <see cref="Id"/>.
/// </summary>
public sealed class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly display name, e.g. "Backups".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which Hetzner service this mapping talks to.</summary>
    public HetznerProduct Product { get; set; } = HetznerProduct.StorageBox;

    /// <summary>
    /// The protocol the user chose. <see cref="StorageProtocol.Auto"/> means "measure and use the
    /// fastest", in which case <see cref="ResolvedProtocol"/> holds the last measured winner.
    /// </summary>
    public StorageProtocol Protocol { get; set; } = StorageProtocol.Auto;

    /// <summary>
    /// The protocol <see cref="StorageProtocol.Auto"/> last settled on, cached so a remount does
    /// not have to re-run the benchmark. Null until the first probe.
    /// </summary>
    public StorageProtocol? ResolvedProtocol { get; set; }

    /// <summary>When <see cref="ResolvedProtocol"/> was measured, so a stale pick can be refreshed.</summary>
    public DateTime? ProtocolMeasuredUtc { get; set; }

    // --- Storage Box ---

    /// <summary>Storage Box account, e.g. "u123456" or a sub-account "u123456-sub1".</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Overrides the derived <c>&lt;user&gt;.your-storagebox.de</c> hostname. Normally left blank.
    /// </summary>
    public string? HostOverride { get; set; }

    /// <summary>SSH/SFTP port. Hetzner's extended SSH service is on 23; 22 has no interactive shell.</summary>
    public int SshPort { get; set; } = HetznerEndpoints.SshPort;

    // --- Object Storage ---

    /// <summary>S3 bucket name (Object Storage only).</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Object Storage location code, e.g. "fsn1".</summary>
    public string LocationCode { get; set; } = "fsn1";

    // --- Common ---

    /// <summary>Optional path within the remote to surface as the root.</summary>
    public string? SubPath { get; set; }

    /// <summary>Drive letter without colon, e.g. "H".</summary>
    public string DriveLetter { get; set; } = "H";

    /// <summary>Whether a drive-letter mapping attaches to a letter or to a directory.</summary>
    public MountTarget MountTarget { get; set; } = MountTarget.DriveLetter;

    /// <summary>
    /// Directory mountpoint for <see cref="Models.MountTarget.Directory"/>. WinFsp creates it on
    /// mount and removes it on unmount, so it must not already exist.
    /// </summary>
    public string? MountDirectory { get; set; }

    /// <summary>
    /// Mount this from the Windows service rather than the tray app, so it survives logoff and is
    /// present before anyone signs in. Requires <see cref="MappingMode.DriveLetter"/> — a Files
    /// On-Demand sync root lives in a user profile and has no session-0 equivalent.
    /// </summary>
    public bool RunAsService { get; set; }

    public bool AutoMount { get; set; }

    /// <summary>Whether this mapping is a drive-letter mount or an on-demand folder.</summary>
    public MappingMode Mode { get; set; } = MappingMode.OnDemandFolder;

    /// <summary>
    /// For <see cref="MappingMode.OnDemandFolder"/>: the local folder registered as a Cloud Files
    /// sync root. Null = a default under the user profile is used.
    /// </summary>
    public string? LocalFolderPath { get; set; }

    public CacheSettings Cache { get; set; } = CacheSettings.Default();

    /// <summary>
    /// The protocol to actually use right now: the explicit choice, or the cached Auto winner,
    /// falling back to the sane default for the product when nothing has been measured yet.
    /// </summary>
    public StorageProtocol EffectiveProtocol
    {
        get
        {
            if (Product == HetznerProduct.ObjectStorage) return StorageProtocol.S3;
            if (Protocol != StorageProtocol.Auto) return Protocol;
            // SFTP is the fallback rather than an arbitrary pick: it is the only Storage Box
            // protocol that is always enabled, needs no Robot toggle, and carries real timestamps.
            return ResolvedProtocol ?? StorageProtocol.Sftp;
        }
    }

    /// <summary>Hostname this mapping connects to.</summary>
    public string Host => Product == HetznerProduct.ObjectStorage
        ? HetznerEndpoints.FindLocation(LocationCode)?.Endpoint ?? string.Empty
        : !string.IsNullOrWhiteSpace(HostOverride)
            ? HostOverride!.Trim()
            : HetznerEndpoints.StorageBoxHost(Username);

    /// <summary>rclone remote name. Unique per mapping so two mappings never share config.</summary>
    public string RemoteName => "hetzner_" + Id.ToString("N");

    public string DriveTarget => DriveLetter.TrimEnd(':') + ":";

    /// <summary>
    /// What rclone is told to mount onto, and what the readiness check watches for. Both forms
    /// behave the same way for detection: neither the letter nor the directory exists until WinFsp
    /// has attached, and both disappear on unmount.
    /// </summary>
    public string MountPoint => MountTarget == MountTarget.Directory
        ? (MountDirectory ?? string.Empty).TrimEnd('\\', '/')
        : DriveTarget;

    /// <summary>
    /// Default directory mountpoint for a mapping that switches to service hosting. Placed outside
    /// any user profile because the service account cannot reach one, and the whole point is that
    /// the mount is there before a user signs in.
    /// </summary>
    public string DefaultMountDirectory
    {
        get
        {
            var leaf = !string.IsNullOrWhiteSpace(Name) ? Name
                : Product == HetznerProduct.ObjectStorage && !string.IsNullOrWhiteSpace(BucketName) ? BucketName
                : !string.IsNullOrWhiteSpace(Username) ? Username
                : "Storage";
            foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
            var root = Path.Combine(
                Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\",
                "HetznerDrive");
            return Path.Combine(root, leaf);
        }
    }

    /// <summary>Sub-path with the separators and edges normalised ("" or "a/b").</summary>
    public string NormalizedSubPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SubPath)) return string.Empty;
            return SubPath.Trim().Replace('\\', '/').Trim('/');
        }
    }

    /// <summary>
    /// The rclone remote path. Each backend roots differently: SMB needs the share as the first
    /// path segment, S3 needs the bucket, while SFTP and WebDAV already land in the account's own
    /// directory, so their root is the remote itself.
    /// </summary>
    public string RemoteTarget
    {
        get
        {
            var root = RemoteName + ":";
            var segments = new List<string>();

            switch (EffectiveProtocol)
            {
                case StorageProtocol.S3:
                    segments.Add(BucketName.Trim('/'));
                    break;
                case StorageProtocol.Smb:
                    segments.Add(HetznerEndpoints.SmbShare(Username));
                    break;
            }

            var sub = NormalizedSubPath;
            if (sub.Length > 0) segments.Add(sub);

            return root + string.Join('/', segments.Where(s => s.Length > 0));
        }
    }

    /// <summary>
    /// Prefix that on-demand keys carry, so a key round-trips to the same remote path the mount
    /// would use. S3 keys are bucket-relative; the file protocols are already rooted at the share
    /// or home directory, so only the sub-path applies.
    /// </summary>
    public string KeyPrefix
    {
        get
        {
            var sub = NormalizedSubPath;
            return sub.Length == 0 ? string.Empty : sub + "/";
        }
    }

    /// <summary>Short human-facing description of where this mapping points.</summary>
    public string RemoteDescription => Product == HetznerProduct.ObjectStorage
        ? $"{BucketName} @ {LocationCode}"
        : $"{Username}{(NormalizedSubPath.Length > 0 ? "/" + NormalizedSubPath : string.Empty)}";
}
