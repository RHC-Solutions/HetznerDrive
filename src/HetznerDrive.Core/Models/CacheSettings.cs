using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace HetznerDrive.Core.Models;

/// <summary>rclone VFS cache modes. See https://rclone.org/commands/rclone_mount/#vfs-file-caching</summary>
public enum VfsCacheMode
{
    Off,
    Minimal,
    Writes,
    Full,
}

/// <summary>
/// rclone VFS/cache tuning knobs surfaced in the UI and translated to mount flags by
/// <c>RcloneRunner</c>. Some are protocol-specific (the <c>--s3-*</c> and <c>--sftp-*</c> families);
/// the runner emits only the ones that apply to the protocol in use.
/// </summary>
public sealed class CacheSettings
{
    public VfsCacheMode CacheMode { get; set; } = VfsCacheMode.Full;

    /// <summary>Max on-disk cache size in MiB. 0 = unlimited (omit the flag). Default 50 GiB.</summary>
    public int VfsCacheMaxSizeMb { get; set; } = 50 * 1024;

    /// <summary>Objects are evicted from the cache after this idle age.</summary>
    [XmlIgnore]
    public TimeSpan VfsCacheMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>XML-serialization surrogate for <see cref="VfsCacheMaxAge"/> (TimeSpan isn't XML-serializable).</summary>
    [JsonIgnore]
    [XmlElement("VfsCacheMaxAgeSeconds")]
    public long VfsCacheMaxAgeSeconds
    {
        get => (long)VfsCacheMaxAge.TotalSeconds;
        set => VfsCacheMaxAge = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// How long directory listings are cached before re-reading from Hetzner. None of these
    /// protocols can push change notifications, so edits made elsewhere (the Hetzner console,
    /// another machine) only appear after this interval.
    /// </summary>
    [XmlIgnore]
    public TimeSpan DirCacheTime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>XML-serialization surrogate for <see cref="DirCacheTime"/>.</summary>
    [JsonIgnore]
    [XmlElement("DirCacheTimeSeconds")]
    public long DirCacheTimeSeconds
    {
        get => (long)DirCacheTime.TotalSeconds;
        set => DirCacheTime = TimeSpan.FromSeconds(value);
    }

    /// <summary>In-memory read-ahead buffer per open file, in MiB.</summary>
    public int BufferSizeMb { get; set; } = 32;

    /// <summary>
    /// Extra sequential read-ahead beyond <see cref="BufferSizeMb"/>, in MiB
    /// (<c>--vfs-read-ahead</c>, cache-mode Full only). 0 = omit the flag.
    /// </summary>
    public int ReadAheadMb { get; set; } = 128;

    /// <summary>
    /// Parallel download streams per open file (<c>--vfs-read-chunk-streams</c>). Only meaningful
    /// where a backend can serve concurrent ranges cheaply — that is S3. On the Storage Box
    /// protocols each stream is another connection against a capped budget, so the runner
    /// suppresses this rather than spending the connection allowance on it.
    /// </summary>
    public int ReadChunkStreams { get; set; } = 16;

    /// <summary>Size of each parallel read chunk in MiB (<c>--vfs-read-chunk-size</c>).</summary>
    public int ReadChunkSizeMb { get; set; } = 4;

    /// <summary>
    /// How many files transfer at once (<c>--transfers</c>). The main lever for many-small-file
    /// copies. Clamped down for SSH-based protocols, which have a hard connection ceiling.
    /// </summary>
    public int Transfers { get; set; } = 8;

    /// <summary>How many files are compared at once (<c>--checkers</c>). Also consumes connections.</summary>
    public int Checkers { get; set; } = 8;

    /// <summary>Parallel multipart chunks within a single large upload (<c>--s3-upload-concurrency</c>).</summary>
    public int UploadConcurrency { get; set; } = 4;

    /// <summary>Multipart upload chunk size in MiB (<c>--s3-chunk-size</c>).</summary>
    public int UploadChunkSizeMb { get; set; } = 16;

    /// <summary>
    /// Outstanding requests per SFTP file transfer (<c>--sftp-concurrency</c>). This is
    /// pipelining inside one SSH connection, not extra connections, so it raises throughput
    /// without touching the connection cap.
    /// </summary>
    public int SftpConcurrency { get; set; } = 64;

    /// <summary>
    /// Take modification times from the listing rather than a per-object metadata request
    /// (<c>--use-server-modtime</c>). Saves a round trip per file on S3.
    /// </summary>
    public bool UseServerModTime { get; set; } = true;

    /// <summary>
    /// Detect changes from size + modtime instead of hashing (<c>--vfs-fast-fingerprint</c>).
    /// </summary>
    public bool FastFingerprint { get; set; } = true;

    /// <summary>
    /// Directory where rclone stores the on-disk VFS cache (<c>--cache-dir</c>). Null/blank =
    /// rclone's default (<c>%LOCALAPPDATA%\rclone</c>).
    /// </summary>
    public string? CacheDir { get; set; }

    public static CacheSettings Default() => new();

    public CacheSettings Clone() => new()
    {
        CacheMode = CacheMode,
        VfsCacheMaxSizeMb = VfsCacheMaxSizeMb,
        VfsCacheMaxAge = VfsCacheMaxAge,
        DirCacheTime = DirCacheTime,
        BufferSizeMb = BufferSizeMb,
        ReadAheadMb = ReadAheadMb,
        ReadChunkStreams = ReadChunkStreams,
        ReadChunkSizeMb = ReadChunkSizeMb,
        Transfers = Transfers,
        Checkers = Checkers,
        UploadConcurrency = UploadConcurrency,
        UploadChunkSizeMb = UploadChunkSizeMb,
        SftpConcurrency = SftpConcurrency,
        UseServerModTime = UseServerModTime,
        FastFingerprint = FastFingerprint,
        CacheDir = CacheDir,
    };
}
