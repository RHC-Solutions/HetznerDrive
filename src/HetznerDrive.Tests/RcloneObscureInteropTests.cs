using System.Diagnostics;
using System.Text;
using HetznerDrive.Core;
using Xunit;

namespace HetznerDrive.Tests;

/// <summary>
/// Round-trips <see cref="RcloneObscure"/> against the real rclone binary.
///
/// <see cref="RcloneObscureTests"/> proves the implementation is self-consistent, which would stay
/// true even if the whole construction disagreed with rclone. Only rclone itself can confirm the
/// bytes are the ones it expects — and getting this wrong does not fail loudly, it fails at mount
/// time with an opaque base64 error.
///
/// Skips when rclone.exe has not been fetched, so a clean clone still runs green;
/// <c>scripts\fetch-deps.ps1</c> puts it in place.
/// </summary>
public class RcloneObscureInteropTests
{
    public static TheoryData<string> Secrets => new()
    {
        "hunter2",
        "a",
        "exactly-16-chars",                                        // exactly one AES block
        "a password that is definitely longer than one aes block", // spans several
        "pässwörd-with-ünicode-字符",                              // multi-byte UTF-8
        "trailing space ",
        "-starts-with-a-hyphen",                                   // must not be read as a flag
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void RcloneCanRevealWhatWeObscure(string secret)
    {
        if (FindRclone() is not { } rclone) return;

        var obscured = RcloneObscure.Obscure(secret);
        Assert.Equal(secret, Run(rclone, "reveal", obscured));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void WeCanRevealWhatRcloneObscures(string secret)
    {
        if (FindRclone() is not { } rclone) return;

        // rclone salts with a fresh IV each time, so this exercises a ciphertext we did not produce.
        var obscured = Run(rclone, "obscure", secret);
        Assert.Equal(secret, RcloneObscure.Reveal(obscured));
    }

    [Fact]
    public void OurCiphertextVariesButRcloneStillReadsIt()
    {
        if (FindRclone() is not { } rclone) return;

        var a = RcloneObscure.Obscure("same password");
        var b = RcloneObscure.Obscure("same password");

        Assert.NotEqual(a, b);
        Assert.Equal("same password", Run(rclone, "reveal", a));
        Assert.Equal("same password", Run(rclone, "reveal", b));
    }

    /// <summary>
    /// Invokes rclone with the value passed after a <c>--</c> terminator.
    ///
    /// That terminator is load-bearing, and finding out why took a flaky test: obscured values are
    /// base64url, whose alphabet includes <c>-</c>, so roughly one in sixty-four begins with a
    /// hyphen and rclone's flag parser rejects it as an unknown shorthand flag. The app itself is
    /// immune because it never puts a secret on a command line — it passes them as environment
    /// variables, which keeps them out of the process list too.
    /// </summary>
    private static string Run(string rclone, string subcommand, string value)
    {
        var args = new[] { subcommand, "--", value };
        var psi = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("rclone did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        Assert.True(process.ExitCode == 0, $"rclone {subcommand} failed: {stderr}");
        // rclone terminates its output with a newline; the value itself never contains one.
        return stdout.TrimEnd('\r', '\n');
    }

    /// <summary>Locates the bundled rclone, or null when it has not been fetched.</summary>
    private static string? FindRclone()
    {
        if (AppPaths.ResolveRcloneExe() is { } resolved) return resolved;

        // The test binary sits deeper than the app does, so walk up for third_party as well.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
