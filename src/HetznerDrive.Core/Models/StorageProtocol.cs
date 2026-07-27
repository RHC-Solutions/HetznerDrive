namespace HetznerDrive.Core.Models;

/// <summary>
/// Which Hetzner product a mapping talks to. These are genuinely different services, not two
/// dialects of one: a Storage Box is a per-user file server (SFTP/SMB/WebDAV, no S3 API at all),
/// while Object Storage is a separate S3-compatible bucket service with its own credentials.
/// Conflating them is the most common Hetzner setup mistake, so the model keeps them apart.
/// </summary>
public enum HetznerProduct
{
    /// <summary>Storage Box — <c>uNNNNNN.your-storagebox.de</c>, reached over SFTP, SMB or WebDAV.</summary>
    StorageBox,

    /// <summary>Object Storage — S3-compatible buckets at <c>&lt;location&gt;.your-objectstorage.com</c>.</summary>
    ObjectStorage,
}

/// <summary>
/// The wire protocol used to move bytes. <see cref="Auto"/> is the default: the app measures the
/// reachable protocols against the actual box and picks whichever is fastest from here, rather
/// than guessing from a table that can't know the user's ISP, firewall or distance to the DC.
/// </summary>
public enum StorageProtocol
{
    /// <summary>Probe and benchmark, then use the fastest protocol. The default for new mappings.</summary>
    Auto,

    /// <summary>SSH/SFTP on port 23. Usually the fastest and most capable choice for a Storage Box.</summary>
    Sftp,

    /// <summary>SMB/CIFS on port 445 against the <c>backup</c> share. Fast on low-latency links.</summary>
    Smb,

    /// <summary>WebDAV over HTTPS on port 443. The most firewall-friendly option.</summary>
    WebDav,

    /// <summary>S3. Object Storage only — a Storage Box has no S3 endpoint.</summary>
    S3,
}

/// <summary>Hosts, ports, shares and endpoints for the two Hetzner products.</summary>
public static class HetznerEndpoints
{
    /// <summary>Storage Box hostnames live under this zone.</summary>
    public const string StorageBoxDomain = "your-storagebox.de";

    /// <summary>
    /// Hetzner runs an extended SSH service on 23. Port 22 exists but refuses interactive
    /// commands, which breaks rclone's checksum support — so 23 is the default everywhere.
    /// </summary>
    public const int SshPort = 23;

    public const int SmbPort = 445;
    public const int WebDavPort = 443;
    public const int S3Port = 443;

    /// <summary>The share a main account's files live in. Sub-accounts use their own username instead.</summary>
    public const string MainAccountShare = "backup";

    /// <summary>
    /// A Storage Box caps concurrent SSH sessions (Hetzner documents ~10). Every rclone transfer
    /// and checker takes one, so the SFTP tuning stays under this ceiling — exceeding it produces
    /// spurious checksum/connection errors on large directory copies rather than a clean failure.
    /// </summary>
    public const int MaxSshConnections = 10;

    /// <summary>
    /// Hostname for a Storage Box user. Sub-accounts (<c>u123456-sub1</c>) get their own hostname,
    /// so the username is used verbatim rather than reduced to the parent account.
    /// </summary>
    public static string StorageBoxHost(string username)
    {
        var user = (username ?? string.Empty).Trim();
        if (user.Length == 0) return string.Empty;
        // Tolerate someone pasting the full hostname into the username box.
        if (user.EndsWith("." + StorageBoxDomain, StringComparison.OrdinalIgnoreCase))
            return user.ToLowerInvariant();
        return $"{user.ToLowerInvariant()}.{StorageBoxDomain}";
    }

    /// <summary>The account name from either a bare username or a full Storage Box hostname.</summary>
    public static string StorageBoxUser(string usernameOrHost)
    {
        var value = (usernameOrHost ?? string.Empty).Trim().ToLowerInvariant();
        var suffix = "." + StorageBoxDomain;
        return value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
    }

    /// <summary>True for a sub-account username such as <c>u123456-sub1</c>.</summary>
    public static bool IsSubAccount(string username) =>
        StorageBoxUser(username).Contains("-sub", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SMB share for a user: the main account exposes <c>backup</c>, while a sub-account exposes a
    /// share named after itself.
    /// </summary>
    public static string SmbShare(string username)
    {
        var user = StorageBoxUser(username);
        return IsSubAccount(user) ? user : MainAccountShare;
    }

    /// <summary>WebDAV base URL for a user.</summary>
    public static string WebDavUrl(string username) => "https://" + StorageBoxHost(username);

    /// <summary>Object Storage locations, as rclone's <c>Hetzner</c> S3 provider expects them.</summary>
    public static readonly IReadOnlyList<ObjectStorageLocation> ObjectStorageLocations = new[]
    {
        new ObjectStorageLocation("fsn1", "Falkenstein (Germany)", "fsn1.your-objectstorage.com"),
        new ObjectStorageLocation("nbg1", "Nuremberg (Germany)", "nbg1.your-objectstorage.com"),
        new ObjectStorageLocation("hel1", "Helsinki (Finland)", "hel1.your-objectstorage.com"),
    };

    public static ObjectStorageLocation? FindLocation(string? code) =>
        ObjectStorageLocations.FirstOrDefault(l =>
            string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The TCP port a protocol connects on, used by the reachability probe.</summary>
    public static int PortFor(StorageProtocol protocol) => protocol switch
    {
        StorageProtocol.Sftp => SshPort,
        StorageProtocol.Smb => SmbPort,
        StorageProtocol.WebDav => WebDavPort,
        StorageProtocol.S3 => S3Port,
        _ => 0,
    };

    /// <summary>Protocols a Storage Box can actually serve, in fallback preference order.</summary>
    public static readonly IReadOnlyList<StorageProtocol> StorageBoxProtocols = new[]
    {
        StorageProtocol.Sftp,
        StorageProtocol.Smb,
        StorageProtocol.WebDav,
    };
}

/// <summary>An Object Storage location and its S3 endpoint host.</summary>
/// <param name="Code">Region/location code, e.g. "fsn1".</param>
/// <param name="DisplayName">Human-facing name shown in the UI.</param>
/// <param name="Endpoint">Endpoint hostname (no scheme), as rclone expects.</param>
public sealed record ObjectStorageLocation(string Code, string DisplayName, string Endpoint);
