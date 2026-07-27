using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

/// <summary>Why a protocol was ruled out, or that it was measured.</summary>
public enum ProbeOutcome
{
    /// <summary>Measured end to end; <see cref="ProtocolMeasurement.ThroughputMBps"/> is meaningful.</summary>
    Measured,

    /// <summary>The TCP port did not accept a connection (blocked, or the service is off).</summary>
    Unreachable,

    /// <summary>Reachable, but the transfer test failed (usually authentication or permissions).</summary>
    Failed,

    /// <summary>Not attempted — the credentials or the product cannot use this protocol.</summary>
    NotApplicable,
}

/// <summary>One protocol's probe result.</summary>
/// <param name="Protocol">The protocol measured.</param>
/// <param name="Outcome">Whether it was measured, and if not, why.</param>
/// <param name="ConnectLatency">TCP handshake time, or null when unreachable.</param>
/// <param name="UploadMBps">Measured upload throughput in MiB/s, if measured.</param>
/// <param name="DownloadMBps">Measured download throughput in MiB/s, if measured.</param>
/// <param name="Detail">Human-facing note, e.g. the error that ruled the protocol out.</param>
public sealed record ProtocolMeasurement(
    StorageProtocol Protocol,
    ProbeOutcome Outcome,
    TimeSpan? ConnectLatency = null,
    double UploadMBps = 0,
    double DownloadMBps = 0,
    string? Detail = null)
{
    /// <summary>
    /// The score protocols are ranked by: total bytes moved over total time. Combining both
    /// directions rather than picking the best one avoids crowning a protocol that downloads
    /// quickly but uploads at a crawl, which is the usual WebDAV failure mode.
    /// </summary>
    public double ThroughputMBps =>
        UploadMBps > 0 && DownloadMBps > 0
            ? 2 / (1 / UploadMBps + 1 / DownloadMBps) // harmonic mean = bytes ÷ total elapsed
            : Math.Max(UploadMBps, DownloadMBps);

    public string Summary => Outcome switch
    {
        ProbeOutcome.Measured =>
            $"{Protocol}: {ThroughputMBps:0.0} MiB/s (up {UploadMBps:0.0}, down {DownloadMBps:0.0})",
        ProbeOutcome.Unreachable => $"{Protocol}: port {HetznerEndpoints.PortFor(Protocol)} unreachable",
        ProbeOutcome.Failed => $"{Protocol}: failed — {Detail}",
        _ => $"{Protocol}: not applicable — {Detail}",
    };
}

/// <summary>The outcome of a full protocol selection run.</summary>
/// <param name="Winner">The protocol to use.</param>
/// <param name="Measurements">Every candidate's result, for display in the UI and the log.</param>
/// <param name="WasMeasured">False when nothing could be benchmarked and the fallback order decided it.</param>
public sealed record ProtocolSelection(
    StorageProtocol Winner,
    IReadOnlyList<ProtocolMeasurement> Measurements,
    bool WasMeasured);

/// <summary>
/// Picks the fastest protocol for a Storage Box by actually measuring it.
///
/// A static ranking cannot know the things that decide this in practice: whether the user's ISP
/// blocks port 445, whether SMB is even enabled in the Hetzner console, how far the client is from
/// the datacentre, or whether a VPN is mangling one path. So each reachable protocol is probed for
/// connectivity and then made to move a real payload both ways.
///
/// The candidates are measured strictly one at a time. Running them together would have them
/// compete for the same uplink and produce three equally wrong numbers.
/// </summary>
public sealed class ProtocolSelector
{
    /// <summary>
    /// Payload size for the transfer test. Large enough to get past TCP slow start (where every
    /// protocol looks the same) but small enough that the whole sweep stays in the seconds.
    /// </summary>
    public int TestPayloadMiB { get; init; } = 16;

    /// <summary>How long to wait for the TCP handshake before calling a port unreachable.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling for one direction of one protocol's transfer test.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>A cached Auto result older than this is re-measured on the next mount.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(14);

    private readonly RcloneCommand _rclone;
    private readonly Action<string>? _log;

    public ProtocolSelector(string rcloneExePath, Action<string>? log = null)
    {
        _rclone = new RcloneCommand(rcloneExePath);
        _log = log;
    }

    /// <summary>True when <paramref name="mapping"/> needs a (re)measurement before mounting.</summary>
    public static bool NeedsMeasurement(Mapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Product == HetznerProduct.ObjectStorage) return false;
        if (mapping.Protocol != StorageProtocol.Auto) return false;
        if (mapping.ResolvedProtocol is null || mapping.ProtocolMeasuredUtc is null) return true;
        return DateTime.UtcNow - mapping.ProtocolMeasuredUtc.Value > CacheLifetime;
    }

    /// <summary>
    /// Probes and benchmarks every protocol this mapping could use and returns the fastest.
    /// Never throws for a protocol-level failure — a protocol that cannot be reached or
    /// authenticated is recorded and skipped, so one broken path does not sink the whole run.
    /// </summary>
    public async Task<ProtocolSelection> SelectFastestAsync(
        Mapping mapping, HetznerCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);

        if (mapping.Product == HetznerProduct.ObjectStorage)
        {
            return new ProtocolSelection(
                StorageProtocol.S3,
                new[]
                {
                    new ProtocolMeasurement(StorageProtocol.S3, ProbeOutcome.NotApplicable,
                        Detail: "Object Storage speaks only S3, so there is nothing to choose between."),
                },
                WasMeasured: false);
        }

        var payload = CreateTestPayload();
        try
        {
            var measurements = new List<ProtocolMeasurement>();
            foreach (var protocol in HetznerEndpoints.StorageBoxProtocols)
            {
                ct.ThrowIfCancellationRequested();
                var measurement = await MeasureAsync(mapping, credentials, protocol, payload, ct)
                    .ConfigureAwait(false);
                measurements.Add(measurement);
                _log?.Invoke("Protocol probe — " + measurement.Summary);
            }

            var best = measurements
                .Where(m => m.Outcome == ProbeOutcome.Measured && m.ThroughputMBps > 0)
                .OrderByDescending(m => m.ThroughputMBps)
                .FirstOrDefault();

            if (best is not null)
            {
                _log?.Invoke($"Selected {best.Protocol} as the fastest protocol " +
                             $"({best.ThroughputMBps:0.0} MiB/s).");
                return new ProtocolSelection(best.Protocol, measurements, WasMeasured: true);
            }

            var fallback = FallbackProtocol(measurements, credentials);
            _log?.Invoke($"No protocol could be measured; falling back to {fallback}.");
            return new ProtocolSelection(fallback, measurements, WasMeasured: false);
        }
        finally
        {
            TryDelete(payload);
        }
    }

    private async Task<ProtocolMeasurement> MeasureAsync(
        Mapping mapping, HetznerCredentials credentials, StorageProtocol protocol,
        string payloadPath, CancellationToken ct)
    {
        if (!credentials.SupportsProtocol(protocol))
        {
            return new ProtocolMeasurement(protocol, ProbeOutcome.NotApplicable,
                Detail: protocol is StorageProtocol.Smb or StorageProtocol.WebDav
                    ? "needs the account password; only an SSH key is configured"
                    : "credentials do not cover this protocol");
        }

        var port = HetznerEndpoints.PortFor(protocol);
        var latency = await ProbeTcpAsync(mapping.Host, port, ConnectTimeout, ct).ConfigureAwait(false);
        if (latency is null)
            return new ProtocolMeasurement(protocol, ProbeOutcome.Unreachable);

        IReadOnlyDictionary<string, string> env;
        try
        {
            env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, credentials, protocol);
        }
        catch (Exception ex)
        {
            return new ProtocolMeasurement(protocol, ProbeOutcome.NotApplicable, latency, Detail: ex.Message);
        }

        // A dotted, uniquely-named file so a leftover from an interrupted run is obvious and never
        // collides with the user's own data.
        var remoteName = $".hetznerdrive-speedtest-{Guid.NewGuid():N}.tmp";
        var remotePath = CombineRemote(RemoteRootFor(mapping, protocol), remoteName);
        var downloadDir = Path.Combine(Path.GetTempPath(), "HetznerDrive", Guid.NewGuid().ToString("N"));

        try
        {
            var upload = await TimeTransferAsync(
                new[] { "copyto", payloadPath, remotePath, "--retries", "1", "--low-level-retries", "2" },
                env, ct).ConfigureAwait(false);
            if (!upload.Result.Succeeded)
                return new ProtocolMeasurement(protocol, ProbeOutcome.Failed, latency,
                    Detail: Describe(upload.Result));

            Directory.CreateDirectory(downloadDir);
            var download = await TimeTransferAsync(
                new[] { "copyto", remotePath, Path.Combine(downloadDir, remoteName),
                        "--retries", "1", "--low-level-retries", "2" },
                env, ct).ConfigureAwait(false);
            if (!download.Result.Succeeded)
                return new ProtocolMeasurement(protocol, ProbeOutcome.Failed, latency,
                    Detail: Describe(download.Result));

            return new ProtocolMeasurement(
                protocol, ProbeOutcome.Measured, latency,
                UploadMBps: Rate(TestPayloadMiB, upload.Elapsed),
                DownloadMBps: Rate(TestPayloadMiB, download.Elapsed));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProtocolMeasurement(protocol, ProbeOutcome.Failed, latency, Detail: ex.Message);
        }
        finally
        {
            // Always clean up, including after a failure partway through: a half-uploaded test file
            // left in someone's Storage Box is the kind of litter that gets noticed.
            await TryDeleteRemoteAsync(remotePath, env).ConfigureAwait(false);
            TryDeleteDirectory(downloadDir);
        }
    }

    private async Task<(RcloneResult Result, TimeSpan Elapsed)> TimeTransferAsync(
        IEnumerable<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _rclone.RunAsync(args, env, TransferTimeout, ct).ConfigureAwait(false);
        stopwatch.Stop();
        return (result, stopwatch.Elapsed);
    }

    private static double Rate(int megabytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : megabytes / elapsed.TotalSeconds;

    /// <summary>
    /// Opens a TCP connection and returns how long the handshake took, or null if it could not be
    /// established. This is what separates "SMB is slow here" from "your ISP blocks port 445",
    /// and it costs a few milliseconds against a transfer test that costs seconds.
    /// </summary>
    public static async Task<TimeSpan?> ProbeTcpAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return null;

        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out
        }
        catch (Exception)
        {
            return null; // refused, DNS failure, unreachable
        }
    }

    /// <summary>
    /// When nothing could be measured, prefer a protocol that at least answered on its port, in
    /// the documented preference order; failing that, SFTP, which is the one service a Storage Box
    /// always has switched on.
    /// </summary>
    private static StorageProtocol FallbackProtocol(
        IReadOnlyList<ProtocolMeasurement> measurements, HetznerCredentials credentials)
    {
        foreach (var protocol in HetznerEndpoints.StorageBoxProtocols)
        {
            var m = measurements.FirstOrDefault(x => x.Protocol == protocol);
            if (m is not null && m.Outcome != ProbeOutcome.Unreachable
                && m.Outcome != ProbeOutcome.NotApplicable)
                return protocol;
        }
        return credentials.SupportsProtocol(StorageProtocol.Sftp)
            ? StorageProtocol.Sftp
            : StorageProtocol.WebDav;
    }

    /// <summary>
    /// The remote root a test file is written to. SMB needs the share segment; SFTP and WebDAV are
    /// already rooted in the account's own directory.
    /// </summary>
    private static string RemoteRootFor(Mapping mapping, StorageProtocol protocol)
    {
        var root = mapping.RemoteName + ":";
        var segments = new List<string>();
        if (protocol == StorageProtocol.Smb)
            segments.Add(HetznerEndpoints.SmbShare(mapping.Username));
        var sub = mapping.NormalizedSubPath;
        if (sub.Length > 0) segments.Add(sub);
        return root + string.Join('/', segments);
    }

    private static string CombineRemote(string root, string name) =>
        root.EndsWith(':') || root.EndsWith('/') ? root + name : root + "/" + name;

    /// <summary>Writes an incompressible temp file, so a protocol cannot win by compressing it.</summary>
    private string CreateTestPayload()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hetznerdrive-speedtest-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const int chunk = 1 << 20; // 1 MiB
        var buffer = new byte[chunk];
        using var stream = File.Create(path);
        for (var i = 0; i < TestPayloadMiB; i++)
        {
            RandomNumberGenerator.Fill(buffer);
            stream.Write(buffer, 0, buffer.Length);
        }
        return path;
    }

    private async Task TryDeleteRemoteAsync(string remotePath, IReadOnlyDictionary<string, string> env)
    {
        try
        {
            await _rclone.RunAsync(
                new[] { "deletefile", remotePath, "--retries", "1" },
                env, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch { /* the file may never have been created; cleanup is best-effort */ }
    }

    private static string Describe(RcloneResult result)
    {
        var line = result.LastLine;
        return string.IsNullOrWhiteSpace(line) ? $"rclone exited with code {result.ExitCode}" : line;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}
