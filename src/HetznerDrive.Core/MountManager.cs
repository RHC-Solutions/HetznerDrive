using System.Collections.Concurrent;
using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

public sealed class MountStatusChangedEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required MountState State { get; init; }
    public string? Message { get; init; }
}

public sealed class MountLogEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required string Line { get; init; }
}

/// <summary>Raised once an Auto mapping has been benchmarked, so the winner can be persisted.</summary>
public sealed class ProtocolResolvedEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required ProtocolSelection Selection { get; init; }
}

/// <summary>
/// Orchestrates one rclone mount per mapping: resolves the protocol for Auto mappings, launches
/// the process, waits for the drive letter to appear, tracks live state, and (optionally) restarts
/// a mount whose process dies unexpectedly.
/// </summary>
public sealed class MountManager : IAsyncDisposable
{
    private readonly string _rcloneExePath;
    private readonly ConcurrentDictionary<Guid, MountSession> _sessions = new();

    /// <summary>Max attempts to auto-restart a mount whose process exits while it was mounted.</summary>
    public int MaxAutoRestarts { get; init; } = 2;

    /// <summary>How long to wait for the drive letter to appear before declaring failure.</summary>
    public TimeSpan MountTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Run mounts at DEBUG log level for troubleshooting.</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>Payload size handed to the Auto benchmark.</summary>
    public int BenchmarkPayloadMiB { get; init; } = 16;

    /// <summary>Ignore a cached Auto winner and re-measure on every mount.</summary>
    public bool AlwaysReBenchmark { get; init; }

    public MountManager(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath) || !File.Exists(rcloneExePath))
            throw new FileNotFoundException("rclone.exe not found.", rcloneExePath);
        _rcloneExePath = rcloneExePath;
    }

    public event EventHandler<MountStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<MountLogEventArgs>? LogReceived;
    public event EventHandler<ProtocolResolvedEventArgs>? ProtocolResolved;

    public MountState GetState(Guid mappingId) =>
        _sessions.TryGetValue(mappingId, out var s) ? s.State : MountState.Unmounted;

    public bool IsMounted(Guid mappingId) => GetState(mappingId) == MountState.Mounted;

    /// <summary>
    /// Mounts <paramref name="mapping"/> using <paramref name="credentials"/> and returns once the
    /// drive is live (state <see cref="MountState.Mounted"/>) or throws on failure.
    /// </summary>
    public async Task MountAsync(Mapping mapping, HetznerCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsCompleteFor(mapping.Product))
            throw new InvalidOperationException("The saved credentials for this mapping are incomplete.");

        if (_sessions.TryGetValue(mapping.Id, out var existing) &&
            existing.State is MountState.Mounted or MountState.Mounting)
            return;

        if (IsDriveLetterInUse(mapping.DriveTarget))
            throw new InvalidOperationException(
                $"Drive {mapping.DriveTarget} is already in use by another volume.");

        await ResolveProtocolAsync(mapping, credentials, ct).ConfigureAwait(false);

        if (!credentials.SupportsProtocol(mapping.EffectiveProtocol))
            throw new InvalidOperationException(
                $"The saved credentials cannot authenticate over {mapping.EffectiveProtocol}.");

        var session = new MountSession(mapping, credentials,
            new RcloneRunner(_rcloneExePath) { VerboseLogging = VerboseLogging });
        _sessions[mapping.Id] = session;

        await StartSessionAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmarks an Auto mapping and records the winner on it. A cached result inside the
    /// selector's lifetime is reused so that remounting (and auto-mount at logon) does not pay the
    /// measurement cost every time.
    /// </summary>
    public async Task<ProtocolSelection?> ResolveProtocolAsync(
        Mapping mapping, HetznerCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.Protocol != StorageProtocol.Auto || mapping.Product == HetznerProduct.ObjectStorage)
            return null;
        if (!AlwaysReBenchmark && !ProtocolSelector.NeedsMeasurement(mapping))
            return null;

        SetStateById(mapping.Id, MountState.Mounting, "Measuring the fastest protocol…");

        var selector = new ProtocolSelector(_rcloneExePath, line => Log(mapping.Id, line))
        {
            TestPayloadMiB = Math.Max(1, BenchmarkPayloadMiB),
        };

        ProtocolSelection selection;
        try
        {
            selection = await selector.SelectFastestAsync(mapping, credentials, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A failed benchmark must not block mounting: fall through on the protocol the mapping
            // would have used anyway and let the mount itself report any real problem.
            Log(mapping.Id, $"Protocol benchmark failed ({ex.Message}); using {mapping.EffectiveProtocol}.");
            return null;
        }

        mapping.ResolvedProtocol = selection.Winner;
        mapping.ProtocolMeasuredUtc = DateTime.UtcNow;
        ProtocolResolved?.Invoke(this,
            new ProtocolResolvedEventArgs { MappingId = mapping.Id, Selection = selection });
        return selection;
    }

    private async Task StartSessionAsync(MountSession session, CancellationToken ct)
    {
        var mapping = session.Mapping;
        SetState(session, MountState.Mounting, $"Mounting over {mapping.EffectiveProtocol}…");

        var runner = session.Runner;
        runner.LogLineReceived += line =>
            LogReceived?.Invoke(this, new MountLogEventArgs { MappingId = mapping.Id, Line = line });
        runner.Exited += code => OnRunnerExited(session, code);

        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, session.Credentials);
        runner.Start(mapping, env);

        // Poll for the drive to appear. If the process dies first, OnRunnerExited flips us to Error.
        var deadline = DateTime.UtcNow + MountTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (session.State == MountState.Error)
                throw new InvalidOperationException(
                    session.LastError ?? "rclone exited before the drive became available.");
            if (DriveIsReady(mapping.DriveTarget))
            {
                SetState(session, MountState.Mounted);
                return;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        // Timed out: tear down and report.
        await runner.StopAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        SetState(session, MountState.Error, $"Timed out waiting for {mapping.DriveTarget} to appear.");
        throw new TimeoutException($"Mounting {mapping.DriveTarget} timed out.");
    }

    private void OnRunnerExited(MountSession session, int exitCode)
    {
        // Expected exits (during Unmount) are handled by UnmountAsync; ignore them here.
        if (session.State is MountState.Unmounting or MountState.Unmounted)
            return;

        if (session.State == MountState.Mounted &&
            session.RestartCount < MaxAutoRestarts)
        {
            session.RestartCount++;
            SetState(session, MountState.Mounting,
                $"rclone exited (code {exitCode}); restarting (attempt {session.RestartCount}).");
            _ = RestartSessionAsync(session);
            return;
        }

        SetState(session, MountState.Error, $"rclone process exited unexpectedly (code {exitCode}).");
    }

    private async Task RestartSessionAsync(MountSession session)
    {
        try
        {
            session.Runner.Dispose();
            session.Runner = new RcloneRunner(_rcloneExePath) { VerboseLogging = VerboseLogging };
            await StartSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(session, MountState.Error, $"Restart failed: {ex.Message}");
        }
    }

    /// <summary>Unmounts a mapping's drive and forgets the session.</summary>
    public async Task UnmountAsync(Guid mappingId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(mappingId, out var session))
            return;

        SetState(session, MountState.Unmounting);
        await session.Runner.StopAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        session.Runner.Dispose();
        _sessions.TryRemove(mappingId, out _);
        SetState(session, MountState.Unmounted);
    }

    public async Task UnmountAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _sessions.Keys.ToArray())
            await UnmountAsync(id, ct).ConfigureAwait(false);
    }

    private void SetState(MountSession session, MountState state, string? message = null)
    {
        session.State = state;
        if (state == MountState.Error)
            session.LastError = message;
        StatusChanged?.Invoke(this,
            new MountStatusChangedEventArgs { MappingId = session.Mapping.Id, State = state, Message = message });
    }

    /// <summary>Reports progress for a mapping that has no session yet (e.g. during the benchmark).</summary>
    private void SetStateById(Guid mappingId, MountState state, string? message) =>
        StatusChanged?.Invoke(this,
            new MountStatusChangedEventArgs { MappingId = mappingId, State = state, Message = message });

    private void Log(Guid mappingId, string line) =>
        LogReceived?.Invoke(this, new MountLogEventArgs { MappingId = mappingId, Line = line });

    private static bool DriveIsReady(string driveTarget)
    {
        try { return Directory.Exists(driveTarget + Path.DirectorySeparatorChar); }
        catch { return false; }
    }

    private static bool IsDriveLetterInUse(string driveTarget) => DriveIsReady(driveTarget);

    public async ValueTask DisposeAsync() => await UnmountAllAsync().ConfigureAwait(false);

    private sealed class MountSession
    {
        public MountSession(Mapping mapping, HetznerCredentials credentials, RcloneRunner runner)
        {
            Mapping = mapping;
            Credentials = credentials;
            Runner = runner;
        }

        public Mapping Mapping { get; }
        public HetznerCredentials Credentials { get; }
        public RcloneRunner Runner { get; set; }
        public MountState State { get; set; } = MountState.Unmounted;
        public string? LastError { get; set; }
        public int RestartCount { get; set; }
    }
}
