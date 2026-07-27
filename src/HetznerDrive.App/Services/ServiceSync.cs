using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.Services;

/// <summary>What an elevated helper run should do.</summary>
public enum ServiceAction
{
    /// <summary>Write the current service-hosted mappings and credentials to the machine store.</summary>
    Publish,

    /// <summary>Register the Windows service, publish, and start it.</summary>
    Install,

    /// <summary>Stop and remove the service, and clear the machine store.</summary>
    Uninstall,
}

/// <summary>
/// Bridges the non-elevated tray app to the machine-wide state the Windows service reads.
///
/// Everything here needs administrator rights — <c>%ProgramData%\HetznerDrive</c> is ACL'd to
/// SYSTEM and Administrators, and registering a service always is. Rather than demanding the whole
/// app run elevated (it must not: it mounts into the user's own session and touches HKCU), the app
/// relaunches *itself* with the <c>runas</c> verb for the one operation that needs it, and exits.
///
/// The elevated copy stays the same Windows user, which matters: the per-user credential store is
/// DPAPI CurrentUser, so only the same account can read the passwords it has to re-protect at
/// machine scope. Elevating as a *different* administrator breaks that, and
/// <see cref="Apply"/> reports it rather than silently publishing empty credentials.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceSync
{
    /// <summary>Command-line switch the elevated copy is launched with.</summary>
    public const string Switch = "--service-apply";

    /// <summary>Exit code meaning the user's credentials could not be read (wrong account elevated).</summary>
    private const int ExitCredentialsUnreadable = 3;

    /// <summary>
    /// Runs <paramref name="action"/> in an elevated copy of this app and waits for it. Returns
    /// null on success, or a message describing why it failed.
    /// </summary>
    public static async Task<string?> RunElevatedAsync(ServiceAction action)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return "Could not determine the HetznerDrive executable path.";

        var psi = new ProcessStartInfo(exe)
        {
            // UseShellExecute is required for the runas verb; without it Windows has no way to
            // raise the UAC prompt and the launch just fails with access denied.
            UseShellExecute = true,
            Verb = "runas",
        };
        psi.ArgumentList.Add(Switch);
        psi.ArgumentList.Add(action.ToString().ToLowerInvariant());

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return "The elevated helper did not start.";
            await process.WaitForExitAsync().ConfigureAwait(true);

            return process.ExitCode switch
            {
                0 => null,
                ExitCredentialsUnreadable =>
                    "The elevated step could not read your saved credentials. This happens when you "
                    + "approve the prompt as a different administrator account — approve it as your "
                    + "own user, or re-enter the credentials while running elevated.",
                _ => $"The elevated step failed (exit code {process.ExitCode}). "
                     + $"See {MachinePaths.LogsDir} for details.",
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the UAC prompt. Not an error worth alarming over.
            return "Cancelled: administrator approval is required to change the Windows service.";
        }
        catch (Exception ex)
        {
            return $"Could not run the elevated step: {ex.Message}";
        }
    }

    /// <summary>
    /// The elevated side. Runs in a fresh, admin process launched by
    /// <see cref="RunElevatedAsync"/>; returns the process exit code.
    /// </summary>
    public static int Apply(ServiceAction action)
    {
        try
        {
            if (action == ServiceAction.Uninstall)
            {
                ServiceControl.Uninstall();
                new ServiceConfigStore().Clear();
                return 0;
            }

            var mappings = new MappingStore().Load();
            var credentials = new CredentialStore();
            try
            {
                credentials.Load();
            }
            catch (Exception)
            {
                // DPAPI refused: almost always because this elevated process is running as a
                // different user than the one that saved them.
                return ExitCredentialsUnreadable;
            }

            new ServiceConfigStore().Publish(mappings, id => credentials.Get(id));

            if (action == ServiceAction.Install)
            {
                var serviceExe = ServiceControl.ResolveServiceExe()
                    ?? throw new FileNotFoundException(
                        $"{ServiceControl.ServiceExeName} was not found next to HetznerDrive.exe.");
                ServiceControl.Install(serviceExe);
                ServiceControl.Start(TimeSpan.FromSeconds(30));
            }
            else if (ServiceControl.IsInstalled())
            {
                // Already installed: bounce it so the new configuration takes effect now rather
                // than on its next sweep.
                ServiceControl.Restart(TimeSpan.FromSeconds(30));
            }

            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                MachinePaths.EnsureCreated();
                using var log = new FileLogger(MachinePaths.LogsDir);
                log.Log($"Service {action} failed: {ex}");
            }
            catch { /* nothing more we can do from here */ }
            return 1;
        }
    }

    /// <summary>True when any mapping is configured to be mounted by the service.</summary>
    public static bool HasServiceMappings(IEnumerable<Mapping> mappings) =>
        mappings.Any(m => m is { RunAsService: true, Mode: MappingMode.DriveLetter });
}
