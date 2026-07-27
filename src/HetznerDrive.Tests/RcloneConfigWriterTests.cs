using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

public class RcloneConfigWriterTests
{
    private static Mapping StorageBox(string user = "u123456") => new()
    {
        Product = HetznerProduct.StorageBox,
        Username = user,
    };

    private static Mapping ObjectStorage() => new()
    {
        Product = HetznerProduct.ObjectStorage,
        Protocol = StorageProtocol.S3,
        BucketName = "bucket",
        LocationCode = "fsn1",
    };

    private static HetznerCredentials Password(string password = "pw") => new() { Password = password };

    private static string Prefix(Mapping m) => "RCLONE_CONFIG_" + m.RemoteName.ToUpperInvariant() + "_";

    [Fact]
    public void Sftp_UsesPort23AndDeclaresTheShell()
    {
        var mapping = StorageBox();
        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password(), StorageProtocol.Sftp);
        var p = Prefix(mapping);

        Assert.Equal("sftp", env[p + "TYPE"]);
        Assert.Equal("u123456.your-storagebox.de", env[p + "HOST"]);
        Assert.Equal("u123456", env[p + "USER"]);
        Assert.Equal("23", env[p + "PORT"]);
        // Declaring the shell up front avoids rclone spending an SSH session probing for it.
        Assert.Equal("unix", env[p + "SHELL_TYPE"]);
        Assert.Equal("md5sum", env[p + "MD5SUM_COMMAND"]);
    }

    [Fact]
    public void Sftp_ObscuresThePasswordRatherThanStoringIt()
    {
        var mapping = StorageBox();
        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password("hunter2"), StorageProtocol.Sftp);

        var stored = env[Prefix(mapping) + "PASS"];
        Assert.NotEqual("hunter2", stored);
        Assert.Equal("hunter2", RcloneObscure.Reveal(stored));
    }

    [Fact]
    public void Sftp_PrefersAKeyOverAPassword()
    {
        var mapping = StorageBox();
        var creds = new HetznerCredentials { Password = "pw", SshKeyFile = @"C:\keys\id_ed25519" };

        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds, StorageProtocol.Sftp);
        var p = Prefix(mapping);

        Assert.Equal(@"C:\keys\id_ed25519", env[p + "KEY_FILE"]);
        Assert.False(env.ContainsKey(p + "PASS"));
    }

    [Fact]
    public void Smb_TargetsThePasswordAndWorkgroup()
    {
        var mapping = StorageBox();
        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password(), StorageProtocol.Smb);
        var p = Prefix(mapping);

        Assert.Equal("smb", env[p + "TYPE"]);
        Assert.Equal("445", env[p + "PORT"]);
        Assert.Equal("WORKGROUP", env[p + "DOMAIN"]);
    }

    [Fact]
    public void WebDav_UsesTheHttpsRootAndTheGenericVendor()
    {
        var mapping = StorageBox();
        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password(), StorageProtocol.WebDav);
        var p = Prefix(mapping);

        Assert.Equal("webdav", env[p + "TYPE"]);
        Assert.Equal("https://u123456.your-storagebox.de", env[p + "URL"]);
        // Nextcloud/ownCloud vendors would make rclone call chunking endpoints Hetzner doesn't have.
        Assert.Equal("other", env[p + "VENDOR"]);
    }

    [Theory]
    [InlineData(StorageProtocol.Smb)]
    [InlineData(StorageProtocol.WebDav)]
    public void PasswordOnlyProtocols_RejectAKeyOnlyLogin(StorageProtocol protocol)
    {
        var mapping = StorageBox();
        var creds = new HetznerCredentials { SshKeyFile = @"C:\keys\id_ed25519" };

        Assert.Throws<InvalidOperationException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds, protocol));
    }

    [Fact]
    public void S3_UsesRclonesHetznerProviderAndTheLocationEndpoint()
    {
        var mapping = ObjectStorage();
        var creds = new HetznerCredentials { AccessKeyId = "AK", SecretAccessKey = "SK" };

        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds, StorageProtocol.S3);
        var p = Prefix(mapping);

        Assert.Equal("s3", env[p + "TYPE"]);
        Assert.Equal("Hetzner", env[p + "PROVIDER"]);
        Assert.Equal("fsn1", env[p + "REGION"]);
        Assert.Equal("fsn1.your-objectstorage.com", env[p + "ENDPOINT"]);
        Assert.Equal("AK", env[p + "ACCESS_KEY_ID"]);
        Assert.Equal("SK", env[p + "SECRET_ACCESS_KEY"]);
        Assert.Equal("true", env[p + "DIRECTORY_MARKERS"]);
    }

    [Fact]
    public void S3_AgainstAStorageBox_IsRejected()
    {
        // The single most likely configuration mistake: a Storage Box has no S3 endpoint at all.
        var mapping = StorageBox();
        Assert.Throws<InvalidOperationException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password(), StorageProtocol.S3));
    }

    [Fact]
    public void FileProtocol_AgainstObjectStorage_IsRejected()
    {
        var mapping = ObjectStorage();
        var creds = new HetznerCredentials { AccessKeyId = "AK", SecretAccessKey = "SK", Password = "pw" };

        Assert.Throws<InvalidOperationException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds, StorageProtocol.Sftp));
    }

    [Fact]
    public void Auto_MustBeResolvedBeforeBuildingConfig()
    {
        var mapping = StorageBox();
        Assert.Throws<ArgumentException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, Password(), StorageProtocol.Auto));
    }

    [Fact]
    public void UnknownObjectStorageLocation_Throws()
    {
        var mapping = ObjectStorage();
        mapping.LocationCode = "nowhere-1";
        var creds = new HetznerCredentials { AccessKeyId = "AK", SecretAccessKey = "SK" };

        Assert.Throws<InvalidOperationException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds, StorageProtocol.S3));
    }
}
