using System.Diagnostics;
using System.Text;
using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

/// <summary>
/// Wraps a single <c>rclone mount</c> child process: builds its argument list, injects the
/// remote's config via environment variables, captures its log output (rclone logs to stderr),
/// and terminates it on unmount (WinFsp detects the exit and releases the drive letter).
/// </summary>
public sealed class RcloneRunner : IDisposable
{
    private readonly string _rcloneExePath;
    private Process? _process;

    public RcloneRunner(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath))
            throw new ArgumentException("rclone path is required.", nameof(rcloneExePath));
        _rcloneExePath = rcloneExePath;
    }

    /// <summary>When true, the mount runs at DEBUG log level (verbose troubleshooting).</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Raised for each line rclone writes to its log (stderr/stdout).</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the rclone process exits, with its exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Translates a mapping's settings into an rclone <c>mount</c> argument list.
    /// Kept static and pure so it can be unit-tested without launching a process.
    /// </summary>
    public static IReadOnlyList<string> BuildMountArguments(Mapping mapping, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var c = mapping.Cache;
        var protocol = mapping.EffectiveProtocol;

        if (string.IsNullOrWhiteSpace(mapping.MountPoint))
            throw new InvalidOperationException("The mapping has no mount point configured.");

        var args = new List<string>
        {
            "mount",
            mapping.RemoteTarget,
            mapping.MountPoint,
            "--vfs-cache-mode", c.CacheMode.ToString().ToLowerInvariant(),
            "--dir-cache-time", ToRcloneDuration(c.DirCacheTime),
            "--buffer-size", $"{Math.Max(0, c.BufferSizeMb)}Mi",
            "--volname", VolumeName(mapping),
            "--no-console",
            // Present the drive as a network drive. Windows never uses a Recycle Bin on network
            // drives, so deletes become real remote deletes instead of a server-side copy into a
            // hidden "$RECYCLE.BIN" folder that keeps consuming quota. Also makes folder deletes work.
            "--network-mode",
            "--log-level", verbose ? "DEBUG" : "INFO",
        };

        if (c.CacheMode != VfsCacheMode.Off)
        {
            if (c.VfsCacheMaxSizeMb > 0)
            {
                args.Add("--vfs-cache-max-size");
                args.Add($"{c.VfsCacheMaxSizeMb}Mi");
            }
            args.Add("--vfs-cache-max-age");
            args.Add(ToRcloneDuration(c.VfsCacheMaxAge));
        }

        AddThroughputArguments(args, c, protocol);

        if (!string.IsNullOrWhiteSpace(c.CacheDir))
        {
            args.Add("--cache-dir");
            args.Add(c.CacheDir!.Trim());
        }

        return args;
    }

    /// <summary>
    /// Adds the throughput flags for <paramref name="protocol"/>.
    ///
    /// The protocols differ in what actually makes them fast, so a single flag set would be wrong
    /// for three of the four. S3 rewards many concurrent HTTP range requests. The Storage Box
    /// protocols do not: SFTP multiplexes inside one SSH connection and is bounded by a hard
    /// server-side connection cap, while SMB and WebDAV have their own pipelining and no
    /// multipart concept at all.
    /// </summary>
    public static void AddThroughputArguments(List<string> args, CacheSettings c, StorageProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(c);

        var (transfers, checkers) = ConcurrencyFor(c, protocol);
        if (transfers > 0) { args.Add("--transfers"); args.Add(transfers.ToString()); }
        if (checkers > 0) { args.Add("--checkers"); args.Add(checkers.ToString()); }

        // Sequential read-ahead only does anything when whole files land in the cache.
        if (c.ReadAheadMb > 0 && c.CacheMode == VfsCacheMode.Full)
        {
            args.Add("--vfs-read-ahead");
            args.Add($"{c.ReadAheadMb}Mi");
        }

        switch (protocol)
        {
            case StorageProtocol.S3:
                // Reads: many concurrent range GETs per open file. rclone's own S3 guidance is a
                // high stream count with a small constant chunk size; throughput scales roughly
                // linearly with the stream count.
                if (c.ReadChunkStreams > 0)
                {
                    args.Add("--vfs-read-chunk-streams");
                    args.Add(c.ReadChunkStreams.ToString());
                }
                if (c.ReadChunkSizeMb > 0)
                {
                    args.Add("--vfs-read-chunk-size");
                    args.Add($"{c.ReadChunkSizeMb}Mi");
                }
                if (c.UploadConcurrency > 0)
                {
                    args.Add("--s3-upload-concurrency");
                    args.Add(c.UploadConcurrency.ToString());
                }
                if (c.UploadChunkSizeMb > 0)
                {
                    args.Add("--s3-chunk-size");
                    args.Add($"{c.UploadChunkSizeMb}Mi");
                }
                // Reading an object's real modtime costs a HEAD per file, and hashing to detect
                // changes costs another. Both are avoidable on a mount.
                if (c.UseServerModTime) args.Add("--use-server-modtime");
                break;

            case StorageProtocol.Sftp:
                // Pipelined requests inside the single SSH connection rclone already holds. This
                // is the SFTP equivalent of upload concurrency and costs no extra connections,
                // which matters because the server caps them.
                if (c.SftpConcurrency > 0)
                {
                    args.Add("--sftp-concurrency");
                    args.Add(c.SftpConcurrency.ToString());
                }
                // Parallel chunk streams would each open another SSH session, and the connection
                // budget is better spent on concurrent whole-file transfers — so they stay off.
                break;

            case StorageProtocol.WebDav:
                // Hetzner's WebDAV serves HTTP range requests, so parallel chunks help here the
                // way they do on S3 — but with no multipart upload, writes stay one stream per file.
                if (c.ReadChunkStreams > 0)
                {
                    args.Add("--vfs-read-chunk-streams");
                    args.Add(Math.Min(c.ReadChunkStreams, 8).ToString());
                }
                if (c.ReadChunkSizeMb > 0)
                {
                    args.Add("--vfs-read-chunk-size");
                    args.Add($"{c.ReadChunkSizeMb}Mi");
                }
                break;

            case StorageProtocol.Smb:
                // SMB pipelines within its own session and reacts badly to rclone slicing reads
                // into separate streams, so throughput here comes purely from --transfers.
                break;
        }

        // Detecting changes by size + modtime instead of hashing avoids a round trip whenever the
        // VFS revalidates a cached file, on every backend.
        if (c.FastFingerprint)
            args.Add("--vfs-fast-fingerprint");
    }

    /// <summary>
    /// Resolves <c>--transfers</c> / <c>--checkers</c> for a protocol.
    ///
    /// Every rclone transfer and checker on an SSH-based backend takes one of the Storage Box's
    /// ~10 concurrent connections. Going over the cap does not degrade gracefully: the server
    /// refuses the extra sessions and rclone reports them as checksum and I/O errors partway
    /// through a large copy. The budget is therefore split with a session left spare for the
    /// control connection.
    /// </summary>
    public static (int Transfers, int Checkers) ConcurrencyFor(CacheSettings c, StorageProtocol protocol)
    {
        var transfers = Math.Max(1, c.Transfers);
        var checkers = Math.Max(1, c.Checkers);

        if (protocol != StorageProtocol.Sftp)
            return (transfers, checkers);

        const int budget = HetznerEndpoints.MaxSshConnections - 1; // keep one in reserve
        var half = Math.Max(1, budget / 2);
        return (Math.Min(transfers, half), Math.Min(checkers, budget - half));
    }

    /// <summary>Volume label shown in Explorer; falls back to whatever identifies the remote.</summary>
    private static string VolumeName(Mapping mapping) =>
        !string.IsNullOrWhiteSpace(mapping.Name) ? mapping.Name
        : mapping.Product == HetznerProduct.ObjectStorage && !string.IsNullOrWhiteSpace(mapping.BucketName)
            ? mapping.BucketName
            : !string.IsNullOrWhiteSpace(mapping.Username) ? mapping.Username
            : "HetznerDrive";

    /// <summary>rclone accepts durations like "3600s"; use whole seconds for an unambiguous value.</summary>
    private static string ToRcloneDuration(TimeSpan span) =>
        $"{Math.Max(0, (long)span.TotalSeconds)}s";

    /// <summary>
    /// Launches the mount. <paramref name="remoteEnv"/> comes from
    /// <see cref="RcloneConfigWriter.BuildRemoteEnvironment(Mapping, HetznerCredentials)"/> and
    /// carries the (secret) remote config.
    /// </summary>
    public void Start(Mapping mapping, IReadOnlyDictionary<string, string> remoteEnv)
    {
        if (IsRunning)
            throw new InvalidOperationException("rclone process is already running for this mapping.");

        var psi = new ProcessStartInfo
        {
            FileName = _rcloneExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // rclone writes its logs as UTF-8. Without forcing UTF-8 here, .NET decodes the streams
            // with the console's default code page (e.g. Windows-1252), which turns non-ASCII names
            // (Cyrillic, Hebrew, …) into mojibake in the log window.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildMountArguments(mapping, VerboseLogging))
            psi.ArgumentList.Add(arg);
        foreach (var kv in remoteEnv)
            psi.Environment[kv.Key] = kv.Value;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) LogLineReceived?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLineReceived?.Invoke(e.Data); };
        _process.Exited += (_, _) => Exited?.Invoke(TryGetExitCode());

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private int TryGetExitCode()
    {
        try { return _process?.ExitCode ?? -1; }
        catch { return -1; }
    }

    /// <summary>
    /// Stops the mount by terminating the rclone process; WinFsp then releases the drive letter.
    /// Returns when the process has exited (or the timeout elapses).
    /// </summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var proc = _process;
        if (proc is null || proc.HasExited)
            return;

        try
        {
            proc.Kill(entireProcessTree: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* best-effort: timed out waiting */ }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public void Dispose()
    {
        try { _process?.Dispose(); }
        catch { /* ignore */ }
        _process = null;
    }
}
