using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HetznerDrive.Core;

/// <summary>
/// Machine-wide state, under <c>%ProgramData%\HetznerDrive</c>.
///
/// Separate from <see cref="AppPaths"/> on purpose. The Windows service runs as LocalSystem and has
/// no access to any user's <c>%LOCALAPPDATA%</c>, and its mounts must exist before anyone signs in,
/// so its configuration cannot live in a user profile. Only mappings explicitly marked
/// <c>RunAsService</c> are copied here; the tray app's own mappings stay per-user.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachinePaths
{
    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HetznerDrive");

    /// <summary>Mappings the service should mount (no secrets).</summary>
    public static string MappingsFile => Path.Combine(BaseDir, "service-mappings.json");

    /// <summary>Credentials for those mappings, DPAPI LocalMachine + a restrictive ACL.</summary>
    public static string CredentialsFile => Path.Combine(BaseDir, "service-credentials.dat");

    public static string LogsDir => Path.Combine(BaseDir, "logs");

    /// <summary>
    /// Creates the directory and locks it to SYSTEM + Administrators. %ProgramData% grants Users
    /// write access by default, which would let any account tamper with what the service mounts.
    /// </summary>
    public static void EnsureCreated() => EnsureDirectoryFor(MappingsFile);

    /// <summary>
    /// Creates the directory holding <paramref name="path"/>, hardening it only when it really is
    /// the machine store. Callers can point the stores elsewhere (tests do), and locking down an
    /// arbitrary directory to SYSTEM + Administrators would be both surprising and, for a
    /// non-elevated caller, a way to lock itself out of its own temp folder.
    /// </summary>
    public static void EnsureDirectoryFor(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;

        var created = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);

        if (!IsMachineStorePath(path)) return;
        Directory.CreateDirectory(LogsDir);
        if (created) TryRestrictDirectory(BaseDir);
    }

    /// <summary>
    /// True when <paramref name="path"/> really lives in the machine store. Both the directory
    /// hardening and the credential file's ACL key off this, so pointing a store at some other
    /// location (a test, a custom deployment) does not silently produce files their own author
    /// cannot read back.
    /// </summary>
    public static bool IsMachineStorePath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(BaseDir).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort ACL tightening. It needs ownership or WRITE_DAC, which the elevated tray app and
    /// the service both have but a standard user does not — and a standard user cannot create the
    /// directory in the first place, so failing quietly here is correct rather than fatal.
    /// </summary>
    public static void TryRestrictDirectory(string path)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch
        {
            // Not fatal: the credential file sets its own ACL regardless.
        }
    }
}
