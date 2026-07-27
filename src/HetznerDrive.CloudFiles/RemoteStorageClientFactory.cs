using System.Runtime.Versioning;
using HetznerDrive.Core.Models;

namespace HetznerDrive.CloudFiles;

/// <summary>Builds the right <see cref="IRemoteStorageClient"/> for a mapping's protocol.</summary>
[SupportedOSPlatform("windows")]
public static class RemoteStorageClientFactory
{
    /// <summary>
    /// Creates a client for <paramref name="mapping"/>'s effective protocol. Auto mappings must
    /// have been resolved first (<c>MountManager.ResolveProtocolAsync</c>); an unresolved Auto
    /// falls back to SFTP, which is the one Storage Box service that is always switched on.
    /// </summary>
    public static IRemoteStorageClient Create(
        Mapping mapping, HetznerCredentials credentials, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);

        var protocol = mapping.EffectiveProtocol;
        if (!credentials.SupportsProtocol(protocol))
            throw new InvalidOperationException(
                $"The saved credentials cannot authenticate over {protocol}. " +
                (protocol is StorageProtocol.Smb or StorageProtocol.WebDav
                    ? "SMB and WebDAV need the account password, not an SSH key."
                    : "Check the mapping's credentials."));

        return protocol switch
        {
            StorageProtocol.S3 => S3StorageClient.ForMapping(mapping, credentials),
            StorageProtocol.Sftp => SftpStorageClient.ForMapping(mapping, credentials, log),
            StorageProtocol.WebDav => WebDavStorageClient.ForMapping(mapping, credentials, log),
            StorageProtocol.Smb => SmbStorageClient.ForMapping(mapping, credentials, log),
            _ => throw new NotSupportedException($"Unsupported protocol '{protocol}'."),
        };
    }
}
