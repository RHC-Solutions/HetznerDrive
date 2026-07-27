using System.Runtime.InteropServices;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;
using Xunit;

namespace HetznerDrive.Tests;

public class StoreTests
{
    [Fact]
    public void MappingStore_RoundTripsIncludingTheMeasuredProtocol()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hd_map_{Guid.NewGuid():N}.json");
        try
        {
            var store = new MappingStore(tmp);
            var mapping = new Mapping
            {
                Name = "X",
                Username = "u123456",
                DriveLetter = "Z",
                AutoMount = true,
                Protocol = StorageProtocol.Auto,
                ResolvedProtocol = StorageProtocol.Smb,
                ProtocolMeasuredUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            };
            store.Save(new[] { mapping });

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("X", loaded[0].Name);
            Assert.Equal(mapping.Id, loaded[0].Id);
            Assert.True(loaded[0].AutoMount);
            // Without this the Auto benchmark would re-run on every launch.
            Assert.Equal(StorageProtocol.Smb, loaded[0].ResolvedProtocol);
            Assert.Equal(mapping.ProtocolMeasuredUtc, loaded[0].ProtocolMeasuredUtc);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void MappingStore_WritesNoSecrets()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hd_map_{Guid.NewGuid():N}.json");
        try
        {
            new MappingStore(tmp).Save(new[] { new Mapping { Username = "u123456" } });
            var raw = File.ReadAllText(tmp);
            Assert.DoesNotContain("Password", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretAccessKey", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void CredentialStore_EncryptsAndRoundTrips()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // DPAPI is Windows-only.

        var tmp = Path.Combine(Path.GetTempPath(), $"hd_cred_{Guid.NewGuid():N}.dat");
        try
        {
            var id = Guid.NewGuid();
            var store = new CredentialStore(tmp);
            store.Load();
            store.Set(id, new HetznerCredentials
            {
                Password = "SUPER-SECRET",
                AccessKeyId = "AK123",
                SecretAccessKey = "S3-SECRET",
            });

            // On-disk bytes must NOT contain the plaintext secrets.
            var raw = File.ReadAllText(tmp);
            Assert.DoesNotContain("SUPER-SECRET", raw);
            Assert.DoesNotContain("S3-SECRET", raw);

            var reloaded = new CredentialStore(tmp);
            reloaded.Load();
            var got = reloaded.Get(id);
            Assert.NotNull(got);
            Assert.Equal("SUPER-SECRET", got!.Password);
            Assert.Equal("AK123", got.AccessKeyId);
            Assert.Equal("S3-SECRET", got.SecretAccessKey);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Credentials_CompletenessDependsOnTheProduct()
    {
        var storageBox = new HetznerCredentials { Password = "pw" };
        var objectStorage = new HetznerCredentials { AccessKeyId = "AK", SecretAccessKey = "SK" };

        Assert.True(storageBox.IsCompleteFor(HetznerProduct.StorageBox));
        Assert.False(storageBox.IsCompleteFor(HetznerProduct.ObjectStorage));

        Assert.True(objectStorage.IsCompleteFor(HetznerProduct.ObjectStorage));
        Assert.False(objectStorage.IsCompleteFor(HetznerProduct.StorageBox));
    }
}
