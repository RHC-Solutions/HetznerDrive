using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

public class MappingAndEndpointTests
{
    [Theory]
    [InlineData("u123456", "u123456.your-storagebox.de")]
    [InlineData("U123456", "u123456.your-storagebox.de")]
    [InlineData("u123456-sub1", "u123456-sub1.your-storagebox.de")]
    // Someone pasting the whole hostname into the username box is a common slip; tolerate it
    // rather than producing u123456.your-storagebox.de.your-storagebox.de.
    [InlineData("u123456.your-storagebox.de", "u123456.your-storagebox.de")]
    public void StorageBoxHost_DerivesFromTheUsername(string username, string expected) =>
        Assert.Equal(expected, HetznerEndpoints.StorageBoxHost(username));

    [Theory]
    [InlineData("u123456", "backup")]
    [InlineData("u123456-sub1", "u123456-sub1")]
    public void SmbShare_DiffersForSubAccounts(string username, string expected) =>
        Assert.Equal(expected, HetznerEndpoints.SmbShare(username));

    [Fact]
    public void SftpRemoteTarget_IsRootedAtTheAccountHome()
    {
        // SFTP lands in the account's own directory, so no share or bucket segment belongs here.
        var mapping = new Mapping { Username = "u123456", Protocol = StorageProtocol.Sftp };
        Assert.Equal(mapping.RemoteName + ":", mapping.RemoteTarget);
    }

    [Fact]
    public void SmbRemoteTarget_IncludesTheShareSegment()
    {
        var mapping = new Mapping { Username = "u123456", Protocol = StorageProtocol.Smb };
        Assert.Equal(mapping.RemoteName + ":backup", mapping.RemoteTarget);
    }

    [Fact]
    public void S3RemoteTarget_IncludesTheBucket()
    {
        var mapping = new Mapping
        {
            Product = HetznerProduct.ObjectStorage,
            Protocol = StorageProtocol.S3,
            BucketName = "photos",
        };
        Assert.Equal(mapping.RemoteName + ":photos", mapping.RemoteTarget);
    }

    [Theory]
    [InlineData("/media/raw/", "media/raw")]
    [InlineData(@"media\raw", "media/raw")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void SubPath_IsNormalised(string? subPath, string expected)
    {
        var mapping = new Mapping { SubPath = subPath };
        Assert.Equal(expected, mapping.NormalizedSubPath);
    }

    [Fact]
    public void SubPath_AppendsToTheRemoteTargetForEveryProtocol()
    {
        var sftp = new Mapping { Username = "u1", Protocol = StorageProtocol.Sftp, SubPath = "docs" };
        var smb = new Mapping { Username = "u1", Protocol = StorageProtocol.Smb, SubPath = "docs" };

        Assert.Equal(sftp.RemoteName + ":docs", sftp.RemoteTarget);
        Assert.Equal(smb.RemoteName + ":backup/docs", smb.RemoteTarget);
    }

    [Fact]
    public void KeyPrefix_TracksTheSubPathOnly()
    {
        // Keys are relative to the mapping root, so the share and bucket must not appear in them —
        // otherwise a round trip through the on-demand layer would double the segment.
        Assert.Equal(string.Empty, new Mapping { Username = "u1" }.KeyPrefix);
        Assert.Equal("docs/", new Mapping { Username = "u1", SubPath = "/docs/" }.KeyPrefix);
    }

    [Fact]
    public void ObjectStorage_AlwaysResolvesToS3RegardlessOfTheStoredProtocol()
    {
        var mapping = new Mapping
        {
            Product = HetznerProduct.ObjectStorage,
            Protocol = StorageProtocol.Sftp, // nonsense left over from a product switch
        };
        Assert.Equal(StorageProtocol.S3, mapping.EffectiveProtocol);
    }

    [Fact]
    public void AutoWithoutAMeasurement_FallsBackToSftp()
    {
        // SFTP is the only Storage Box service that is always on and needs no console toggle.
        var mapping = new Mapping { Username = "u1", Protocol = StorageProtocol.Auto };
        Assert.Equal(StorageProtocol.Sftp, mapping.EffectiveProtocol);
    }

    [Fact]
    public void AutoWithAMeasurement_UsesTheWinner()
    {
        var mapping = new Mapping
        {
            Username = "u1",
            Protocol = StorageProtocol.Auto,
            ResolvedProtocol = StorageProtocol.Smb,
        };
        Assert.Equal(StorageProtocol.Smb, mapping.EffectiveProtocol);
    }

    [Fact]
    public void HostOverride_WinsOverTheDerivedHostname()
    {
        var mapping = new Mapping { Username = "u123456", HostOverride = "box.internal" };
        Assert.Equal("box.internal", mapping.Host);
    }

    [Theory]
    [InlineData(StorageProtocol.Sftp, 23)]
    [InlineData(StorageProtocol.Smb, 445)]
    [InlineData(StorageProtocol.WebDav, 443)]
    public void PortFor_MatchesHetznersDocumentedPorts(StorageProtocol protocol, int expected) =>
        Assert.Equal(expected, HetznerEndpoints.PortFor(protocol));
}
