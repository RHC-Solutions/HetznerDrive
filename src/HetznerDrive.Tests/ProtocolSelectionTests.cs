using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

public class ProtocolSelectionTests
{
    [Fact]
    public void Throughput_IsTheHarmonicMeanOfBothDirections()
    {
        // Bytes ÷ total elapsed, not the average of two rates: moving the same payload at 10 MiB/s
        // up and 30 MiB/s down takes 1/10 + 1/30 seconds per MiB, i.e. 15 MiB/s overall.
        var m = new ProtocolMeasurement(StorageProtocol.Sftp, ProbeOutcome.Measured,
            UploadMBps: 10, DownloadMBps: 30);

        Assert.Equal(15, m.ThroughputMBps, precision: 6);
    }

    [Fact]
    public void Throughput_PenalisesALopsidedProtocol()
    {
        // A protocol that downloads quickly but uploads at a crawl must not beat a balanced one —
        // this is the usual WebDAV shape, and a plain average would hide it.
        var lopsided = new ProtocolMeasurement(StorageProtocol.WebDav, ProbeOutcome.Measured,
            UploadMBps: 1, DownloadMBps: 100);
        var balanced = new ProtocolMeasurement(StorageProtocol.Sftp, ProbeOutcome.Measured,
            UploadMBps: 18, DownloadMBps: 22);

        Assert.True(balanced.ThroughputMBps > lopsided.ThroughputMBps);
    }

    [Fact]
    public void Throughput_FallsBackToWhicheverDirectionWasMeasured()
    {
        var uploadOnly = new ProtocolMeasurement(StorageProtocol.Smb, ProbeOutcome.Measured, UploadMBps: 12);
        Assert.Equal(12, uploadOnly.ThroughputMBps, precision: 6);
    }

    [Fact]
    public void UnreachableSummary_NamesThePortSoTheCauseIsObvious()
    {
        // "SMB is slow" and "your ISP blocks 445" need very different responses from the user.
        var m = new ProtocolMeasurement(StorageProtocol.Smb, ProbeOutcome.Unreachable);
        Assert.Contains("445", m.Summary);
    }

    [Fact]
    public void ObjectStorage_NeverNeedsMeasuring()
    {
        var mapping = new Mapping { Product = HetznerProduct.ObjectStorage, Protocol = StorageProtocol.Auto };
        Assert.False(ProtocolSelector.NeedsMeasurement(mapping));
    }

    [Fact]
    public void ExplicitProtocol_NeverNeedsMeasuring()
    {
        var mapping = new Mapping { Username = "u1", Protocol = StorageProtocol.WebDav };
        Assert.False(ProtocolSelector.NeedsMeasurement(mapping));
    }

    [Fact]
    public void UnmeasuredAutoMapping_NeedsMeasuring()
    {
        var mapping = new Mapping { Username = "u1", Protocol = StorageProtocol.Auto };
        Assert.True(ProtocolSelector.NeedsMeasurement(mapping));
    }

    [Fact]
    public void FreshlyMeasuredAutoMapping_ReusesTheCachedWinner()
    {
        var mapping = new Mapping
        {
            Username = "u1",
            Protocol = StorageProtocol.Auto,
            ResolvedProtocol = StorageProtocol.Sftp,
            ProtocolMeasuredUtc = DateTime.UtcNow.AddDays(-1),
        };
        Assert.False(ProtocolSelector.NeedsMeasurement(mapping));
    }

    [Fact]
    public void StaleMeasurement_IsRefreshed()
    {
        var mapping = new Mapping
        {
            Username = "u1",
            Protocol = StorageProtocol.Auto,
            ResolvedProtocol = StorageProtocol.Sftp,
            ProtocolMeasuredUtc = DateTime.UtcNow - ProtocolSelector.CacheLifetime - TimeSpan.FromDays(1),
        };
        Assert.True(ProtocolSelector.NeedsMeasurement(mapping));
    }

    [Fact]
    public async Task TcpProbe_ReportsNullForAnUnreachableHost()
    {
        // Reserved by RFC 5737 for documentation, so it can never route anywhere real.
        var latency = await ProtocolSelector.ProbeTcpAsync("192.0.2.1", 445, TimeSpan.FromMilliseconds(300));
        Assert.Null(latency);
    }

    [Fact]
    public async Task TcpProbe_ReportsNullForAnUnparseableTarget()
    {
        Assert.Null(await ProtocolSelector.ProbeTcpAsync("", 23, TimeSpan.FromMilliseconds(200)));
        Assert.Null(await ProtocolSelector.ProbeTcpAsync("example.invalid", 0, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void CredentialsWithOnlyAKey_RuleOutSmbAndWebDav()
    {
        var creds = new HetznerCredentials { SshKeyFile = @"C:\keys\id_ed25519" };

        Assert.True(creds.SupportsProtocol(StorageProtocol.Sftp));
        Assert.False(creds.SupportsProtocol(StorageProtocol.Smb));
        Assert.False(creds.SupportsProtocol(StorageProtocol.WebDav));
    }

    [Fact]
    public void CredentialsWithAPassword_CoverAllStorageBoxProtocols()
    {
        var creds = new HetznerCredentials { Password = "pw" };

        foreach (var protocol in HetznerEndpoints.StorageBoxProtocols)
            Assert.True(creds.SupportsProtocol(protocol), $"{protocol} should be usable with a password");
    }
}
