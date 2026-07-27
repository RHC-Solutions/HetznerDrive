using System.Diagnostics;
using System.Text;

namespace HetznerDrive.Core;

/// <summary>Exit code plus captured output of a finished rclone invocation.</summary>
/// <param name="ExitCode">Process exit code; 0 means success.</param>
/// <param name="Output">Combined stdout/stderr, trimmed.</param>
public sealed record RcloneResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The last non-empty output line, which is where rclone puts its error summary.</summary>
    public string LastLine =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
}

/// <summary>
/// Runs a short-lived rclone command to completion and captures its output — as opposed to
/// <see cref="RcloneRunner"/>, which supervises a long-running <c>mount</c>. Used for the
/// protocol benchmark and connection tests.
/// </summary>
public sealed class RcloneCommand
{
    private readonly string _rcloneExePath;

    public RcloneCommand(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath) || !File.Exists(rcloneExePath))
            throw new FileNotFoundException("rclone.exe not found.", rcloneExePath);
        _rcloneExePath = rcloneExePath;
    }

    /// <summary>
    /// Runs rclone with <paramref name="args"/> and the remote config in <paramref name="env"/>,
    /// returning once it exits or <paramref name="timeout"/> elapses (after which it is killed and
    /// reported as a failure rather than left running).
    /// </summary>
    public async Task<RcloneResult> RunAsync(
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string> env,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _rcloneExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var output = new StringBuilder();
        var gate = new object();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (gate) output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            lock (gate) return new RcloneResult(-1, output.ToString().Trim() + "\nTimed out.");
        }

        string text;
        lock (gate) text = output.ToString().Trim();
        return new RcloneResult(process.ExitCode, text);
    }
}
