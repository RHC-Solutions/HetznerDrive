using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

public class RcloneArgumentTests
{
    private static Mapping StorageBox(StorageProtocol protocol) => new()
    {
        Product = HetznerProduct.StorageBox,
        Username = "u123456",
        Protocol = protocol,
        DriveLetter = "H",
        Name = "Backups",
    };

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void MountArguments_CarryTheTargetAndDriveLetter()
    {
        var args = RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.Sftp));

        Assert.Equal("mount", args[0]);
        Assert.Equal("H:", args[2]);
        Assert.Contains("--network-mode", args);
        Assert.Equal("Backups", ValueAfter(args, "--volname"));
    }

    [Fact]
    public void S3Mount_GetsMultipartAndParallelReadFlags()
    {
        var mapping = new Mapping
        {
            Product = HetznerProduct.ObjectStorage,
            Protocol = StorageProtocol.S3,
            BucketName = "bucket",
        };

        var args = RcloneRunner.BuildMountArguments(mapping);

        Assert.Equal("16", ValueAfter(args, "--vfs-read-chunk-streams"));
        Assert.Equal("4", ValueAfter(args, "--s3-upload-concurrency"));
        Assert.Equal("16Mi", ValueAfter(args, "--s3-chunk-size"));
        Assert.Contains("--use-server-modtime", args);
    }

    [Fact]
    public void SftpMount_PipelinesWithinTheConnectionInsteadOfOpeningMore()
    {
        var args = RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.Sftp));

        Assert.Equal("64", ValueAfter(args, "--sftp-concurrency"));
        // Parallel chunk streams would each cost another SSH session against a capped budget.
        Assert.DoesNotContain("--vfs-read-chunk-streams", args);
        // S3-only knobs must not leak onto a non-S3 backend.
        Assert.DoesNotContain("--s3-chunk-size", args);
        Assert.DoesNotContain("--s3-upload-concurrency", args);
    }

    [Fact]
    public void SmbMount_LeavesReadSlicingAlone()
    {
        var args = RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.Smb));

        Assert.DoesNotContain("--vfs-read-chunk-streams", args);
        Assert.DoesNotContain("--sftp-concurrency", args);
        Assert.DoesNotContain("--s3-chunk-size", args);
    }

    [Fact]
    public void WebDavMount_UsesRangeReadsButNoMultipartUpload()
    {
        var args = RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.WebDav));

        Assert.NotNull(ValueAfter(args, "--vfs-read-chunk-streams"));
        Assert.DoesNotContain("--s3-upload-concurrency", args);
        Assert.DoesNotContain("--sftp-concurrency", args);
    }

    [Fact]
    public void SftpConcurrency_StaysUnderTheStorageBoxSessionCap()
    {
        // Hetzner allows roughly ten concurrent SSH sessions per box; every transfer and checker
        // consumes one, and exceeding the cap surfaces as mid-copy I/O errors rather than queueing.
        var cache = new CacheSettings { Transfers = 64, Checkers = 64 };

        var (transfers, checkers) = RcloneRunner.ConcurrencyFor(cache, StorageProtocol.Sftp);

        Assert.True(transfers + checkers < HetznerEndpoints.MaxSshConnections,
            $"{transfers} + {checkers} must stay below {HetznerEndpoints.MaxSshConnections}");
        Assert.True(transfers >= 1 && checkers >= 1);
    }

    [Theory]
    [InlineData(StorageProtocol.Smb)]
    [InlineData(StorageProtocol.WebDav)]
    [InlineData(StorageProtocol.S3)]
    public void NonSshProtocols_KeepTheRequestedConcurrency(StorageProtocol protocol)
    {
        var cache = new CacheSettings { Transfers = 32, Checkers = 24 };

        var (transfers, checkers) = RcloneRunner.ConcurrencyFor(cache, protocol);

        Assert.Equal(32, transfers);
        Assert.Equal(24, checkers);
    }

    [Fact]
    public void CacheModeOff_OmitsTheCacheSizeAndAgeFlags()
    {
        var mapping = StorageBox(StorageProtocol.Sftp);
        mapping.Cache.CacheMode = VfsCacheMode.Off;

        var args = RcloneRunner.BuildMountArguments(mapping);

        Assert.Equal("off", ValueAfter(args, "--vfs-cache-mode"));
        Assert.DoesNotContain("--vfs-cache-max-size", args);
        Assert.DoesNotContain("--vfs-cache-max-age", args);
        // Read-ahead only does anything once whole files land in the cache.
        Assert.DoesNotContain("--vfs-read-ahead", args);
    }

    [Fact]
    public void UnlimitedCacheSize_OmitsTheSizeFlagButKeepsTheAge()
    {
        var mapping = StorageBox(StorageProtocol.Sftp);
        mapping.Cache.VfsCacheMaxSizeMb = 0;

        var args = RcloneRunner.BuildMountArguments(mapping);

        Assert.DoesNotContain("--vfs-cache-max-size", args);
        Assert.NotNull(ValueAfter(args, "--vfs-cache-max-age"));
    }

    [Fact]
    public void VerboseFlag_SwitchesTheLogLevel()
    {
        Assert.Equal("INFO", ValueAfter(RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.Sftp)), "--log-level"));
        Assert.Equal("DEBUG", ValueAfter(RcloneRunner.BuildMountArguments(StorageBox(StorageProtocol.Sftp), verbose: true), "--log-level"));
    }
}
