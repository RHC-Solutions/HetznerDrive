using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace HetznerDrive.Core;

/// <summary>Installed/running state of the Windows service.</summary>
public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Pending,
    Unknown,
}

/// <summary>
/// Installs, removes and controls the HetznerDrive Windows service.
///
/// Creation and deletion go through <c>sc.exe</c> rather than a managed API because .NET has no
/// supported in-process equivalent — <c>ServiceInstaller</c> did not come across to modern .NET, and
/// P/Invoking <c>CreateService</c> directly buys nothing over the tool Windows already ships.
/// Querying and start/stop use <see cref="ServiceController"/>, which does have a clean API.
///
/// Every mutating call needs an elevated process; the tray app relaunches itself with the
/// <c>runas</c> verb rather than requiring the whole app to run as administrator.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "HetznerDrive";
    public const string DisplayName = "HetznerDrive mount service";

    private const string Description =
        "Keeps HetznerDrive directory mountpoints available to every session, including before "
        + "any user signs in.";

    /// <summary>Executable name of the service host, alongside the app.</summary>
    public const string ServiceExeName = "HetznerDrive.Service.exe";

    /// <summary>Locates the service host next to the running app, or null if it isn't deployed.</summary>
    public static string? ResolveServiceExe(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, ServiceExeName),
            Path.Combine(appDir, "service", ServiceExeName),
            // Development layout: the service project's own build output.
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..",
                "HetznerDrive.Service", "bin", "Debug", "net8.0-windows", ServiceExeName)),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..",
                "HetznerDrive.Service", "bin", "Release", "net8.0-windows", ServiceExeName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending
                    or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.PausePending
                    => ServiceState.Pending,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this (wrapping ERROR_SERVICE_DOES_NOT_EXIST) rather than
            // returning a status when the service was never installed.
            return ServiceState.NotInstalled;
        }
    }

    public static bool IsInstalled() => GetState() != ServiceState.NotInstalled;

    /// <summary>
    /// Registers the service to run as LocalSystem and start at boot. Re-running against an
    /// existing installation updates the binary path instead of failing, so an upgrade that moved
    /// the exe repairs itself.
    /// </summary>
    public static void Install(string serviceExePath)
    {
        if (string.IsNullOrWhiteSpace(serviceExePath) || !File.Exists(serviceExePath))
            throw new FileNotFoundException("The service executable was not found.", serviceExePath);

        // sc.exe is picky: binPath= needs the space after '=' and the whole value quoted.
        var binPath = $"\"{serviceExePath}\"";
        var verb = IsInstalled() ? "config" : "create";

        RunSc(verb, ServiceName,
            $"binPath= {binPath}",
            "start= auto",
            "obj= LocalSystem",
            $"DisplayName= \"{DisplayName}\"");

        // Description is a separate call; a failure here is cosmetic.
        try { RunSc("description", ServiceName, $"\"{Description}\""); } catch { /* ignore */ }

        // Restart on failure rather than leaving mounts down after a transient crash.
        try { RunSc("failure", ServiceName, "reset= 86400", "actions= restart/5000/restart/15000/restart/60000"); }
        catch { /* ignore */ }
    }

    public static void Uninstall()
    {
        if (!IsInstalled()) return;
        try { Stop(TimeSpan.FromSeconds(30)); } catch { /* delete anyway */ }
        RunSc("delete", ServiceName);
    }

    public static void Start(TimeSpan timeout)
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Running) return;
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static void Stop(TimeSpan timeout)
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    /// <summary>Restarts so a configuration change is picked up immediately rather than on the next poll.</summary>
    public static void Restart(TimeSpan timeout)
    {
        try { Stop(timeout); } catch { /* may already be stopped */ }
        Start(timeout);
    }

    private static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start sc.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var detail = string.Join(" ", new[] { stdout, stderr }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()));
            throw new InvalidOperationException(
                $"sc.exe {args[0]} failed (exit {process.ExitCode}). {detail}".Trim());
        }
    }
}
