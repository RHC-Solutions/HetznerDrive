using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

// The test project targets plain net8.0 so it can run the pure logic anywhere, but everything
// exercised here is Windows-only by nature (DPAPI, service control, ACLs). Declaring that satisfies
// the platform-compatibility analyser; the individual tests still guard at runtime where they touch
// APIs that would actually fail off-Windows.
[SupportedOSPlatform("windows")]
public class ServiceModeTests
{
    private static Mapping DirectoryMapping(string dir) => new()
    {
        Name = "Backups",
        Username = "u123456",
        Protocol = StorageProtocol.Sftp,
        Mode = MappingMode.DriveLetter,
        MountTarget = MountTarget.Directory,
        MountDirectory = dir,
        RunAsService = true,
    };

    [Fact]
    public void MountPoint_FollowsTheMountTarget()
    {
        var letter = new Mapping { DriveLetter = "H", MountTarget = MountTarget.DriveLetter };
        Assert.Equal("H:", letter.MountPoint);

        var dir = new Mapping { MountTarget = MountTarget.Directory, MountDirectory = @"C:\HetznerDrive\Backups\" };
        // The trailing separator is trimmed: rclone and the readiness check both want the bare path.
        Assert.Equal(@"C:\HetznerDrive\Backups", dir.MountPoint);
    }

    [Fact]
    public void MountArguments_TargetTheDirectoryRatherThanALetter()
    {
        var args = RcloneRunner.BuildMountArguments(DirectoryMapping(@"C:\HetznerDrive\Backups"));

        Assert.Equal("mount", args[0]);
        Assert.Equal(@"C:\HetznerDrive\Backups", args[2]);
        // A stale drive letter here would mount somewhere nobody asked for.
        Assert.DoesNotContain("H:", args);
    }

    [Fact]
    public void MountArguments_OmitNetworkModeForADirectoryMountpoint()
    {
        // rclone does not fail on the combination, it logs "Ignoring --network-mode as it is not
        // supported with directory mountpoint" at ERROR and mounts as a fixed disk. Sending the
        // flag anyway would put a red line in the activity log on every service mount.
        var args = RcloneRunner.BuildMountArguments(DirectoryMapping(@"C:\HetznerDrive\Backups"));

        Assert.DoesNotContain("--network-mode", args);
    }

    [Fact]
    public void MountArguments_KeepNetworkModeForAServicedDriveLetter()
    {
        // A letter mounted by the service is legitimate: LocalSystem writes the DOS device to the
        // global namespace, so it is visible in interactive sessions and still wants network mode.
        var mapping = new Mapping
        {
            Name = "Backups",
            Username = "u123456",
            Mode = MappingMode.DriveLetter,
            MountTarget = MountTarget.DriveLetter,
            DriveLetter = "H",
            RunAsService = true,
        };

        var args = RcloneRunner.BuildMountArguments(mapping);

        Assert.Equal("H:", args[2]);
        Assert.Contains("--network-mode", args);
    }

    [Fact]
    public void MountArguments_RejectAnEmptyMountPoint()
    {
        // Directory target with nothing configured: catching it here beats rclone failing with
        // a mount error that names no cause.
        var mapping = new Mapping { MountTarget = MountTarget.Directory, MountDirectory = null };
        Assert.Throws<InvalidOperationException>(() => RcloneRunner.BuildMountArguments(mapping));
    }

    [Fact]
    public void DefaultMountDirectory_SitsOutsideAnyUserProfile()
    {
        // The service runs as LocalSystem and its mounts must exist before anyone signs in, so a
        // path under a user profile would be unreachable at exactly the moment it is needed.
        var mapping = new Mapping { Name = "Backups", Username = "u123456" };
        var dir = mapping.DefaultMountDirectory;

        Assert.Contains("HetznerDrive", dir);
        Assert.EndsWith("Backups", dir);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            Assert.DoesNotContain(profile, dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultMountDirectory_SanitisesTheName()
    {
        var mapping = new Mapping { Name = "Back/ups: prod", Username = "u1" };
        var leaf = Path.GetFileName(mapping.DefaultMountDirectory);
        Assert.DoesNotContain('/', leaf);
        Assert.DoesNotContain(':', leaf);
    }

    [Fact]
    public void Publish_WritesOnlyServiceHostedMappings()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // DPAPI is Windows-only.

        var dir = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mappingsPath = Path.Combine(dir, "service-mappings.json");
        var credentialsPath = Path.Combine(dir, "service-credentials.dat");

        try
        {
            var serviced = DirectoryMapping(Path.Combine(dir, "mount"));
            var localOnly = new Mapping { Name = "Local", Username = "u999", RunAsService = false };
            var onDemand = new Mapping
            {
                Name = "OnDemand",
                Username = "u888",
                RunAsService = true,
                // Cloud Files needs an interactive session; marking it for the service is a
                // configuration mistake that must not reach the machine store.
                Mode = MappingMode.OnDemandFolder,
            };

            var store = new ServiceConfigStore(mappingsPath, credentialsPath);
            store.Publish(
                new[] { serviced, localOnly, onDemand },
                id => new HetznerCredentials { Password = "pw-" + id.ToString("N")[..4] });

            var loaded = store.LoadMappings();
            Assert.Single(loaded);
            Assert.Equal(serviced.Id, loaded[0].Id);
            Assert.Equal(MountTarget.Directory, loaded[0].MountTarget);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Publish_KeepsSecretsOutOfTheMappingsFile()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mappingsPath = Path.Combine(dir, "service-mappings.json");
        var credentialsPath = Path.Combine(dir, "service-credentials.dat");

        try
        {
            var mapping = DirectoryMapping(Path.Combine(dir, "mount"));
            new ServiceConfigStore(mappingsPath, credentialsPath).Publish(
                new[] { mapping },
                _ => new HetznerCredentials { Password = "MACHINE-SECRET" });

            Assert.DoesNotContain("MACHINE-SECRET", File.ReadAllText(mappingsPath));
            Assert.DoesNotContain("MACHINE-SECRET", File.ReadAllText(credentialsPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ServiceCredentials_RoundTripAtMachineScope()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mappingsPath = Path.Combine(dir, "service-mappings.json");
        var credentialsPath = Path.Combine(dir, "service-credentials.dat");

        try
        {
            var mapping = DirectoryMapping(Path.Combine(dir, "mount"));
            var store = new ServiceConfigStore(mappingsPath, credentialsPath);
            store.Publish(new[] { mapping }, _ => new HetznerCredentials { Password = "pw" });

            // LocalMachine scope is the point: the service runs as LocalSystem and could not read a
            // CurrentUser blob written by the signed-in user.
            var reloaded = store.LoadCredentials().Get(mapping.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("pw", reloaded!.Password);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadMappings_TreatsACorruptFileAsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mappingsPath = Path.Combine(dir, "service-mappings.json");

        try
        {
            // A half-written file must not take the service down; the next change event brings a
            // good one.
            File.WriteAllText(mappingsPath, "[{\"Name\": \"tru");
            Assert.Empty(new ServiceConfigStore(mappingsPath).LoadMappings());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadMappings_IsEmptyWhenNothingHasBeenPublished()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}.json");
        Assert.Empty(new ServiceConfigStore(path).LoadMappings());
    }

    [Fact]
    public void Clear_RemovesBothFiles()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Path.Combine(Path.GetTempPath(), $"hd_svc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mappingsPath = Path.Combine(dir, "service-mappings.json");
        var credentialsPath = Path.Combine(dir, "service-credentials.dat");

        try
        {
            var store = new ServiceConfigStore(mappingsPath, credentialsPath);
            store.Publish(new[] { DirectoryMapping(Path.Combine(dir, "mount")) },
                _ => new HetznerCredentials { Password = "pw" });
            Assert.True(File.Exists(mappingsPath));

            store.Clear();

            // Uninstalling must not leave a machine-wide copy of the password behind.
            Assert.False(File.Exists(mappingsPath));
            Assert.False(File.Exists(credentialsPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ServiceControl_ReportsNotInstalledRatherThanThrowing()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Whatever the state on this machine, querying must never throw — the Settings dialog
        // calls it on every open.
        var state = ServiceControl.GetState();
        Assert.True(Enum.IsDefined(state));
    }
}
