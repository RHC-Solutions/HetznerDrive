using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

/// <summary>
/// Builds the rclone remote definition for a mapping, one variant per protocol.
///
/// Rather than writing secrets into an rclone.conf file on disk, the remote is defined entirely
/// through <c>RCLONE_CONFIG_&lt;REMOTE&gt;_&lt;KEY&gt;</c> environment variables injected into the
/// rclone child process at launch. That keeps the password off disk and off the command line
/// (where it would show up in the process list).
/// See https://rclone.org/docs/#config-file for the env-var override mechanism.
/// </summary>
public static class RcloneConfigWriter
{
    /// <summary>
    /// Produces the environment variables defining <paramref name="mapping"/>'s remote, using the
    /// protocol from <see cref="Mapping.EffectiveProtocol"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildRemoteEnvironment(
        Mapping mapping, HetznerCredentials credentials) =>
        BuildRemoteEnvironment(mapping, credentials, mapping.EffectiveProtocol);

    /// <summary>
    /// Produces the environment variables defining <paramref name="mapping"/>'s remote over an
    /// explicit <paramref name="protocol"/>. The protocol is a parameter rather than being read
    /// from the mapping so the benchmark can build a config for each candidate without mutating it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildRemoteEnvironment(
        Mapping mapping, HetznerCredentials credentials, StorageProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);

        if (protocol == StorageProtocol.Auto)
            throw new ArgumentException("Auto must be resolved to a concrete protocol first.", nameof(protocol));
        if (protocol == StorageProtocol.S3 != (mapping.Product == HetznerProduct.ObjectStorage))
            throw new InvalidOperationException(
                "S3 is only available on Hetzner Object Storage; a Storage Box has no S3 endpoint.");

        // rclone uppercases the whole env-var key, so the remote name is uppercased to match.
        var prefix = "RCLONE_CONFIG_" + mapping.RemoteName.ToUpperInvariant() + "_";

        var config = protocol switch
        {
            StorageProtocol.Sftp => BuildSftp(mapping, credentials),
            StorageProtocol.Smb => BuildSmb(mapping, credentials),
            StorageProtocol.WebDav => BuildWebDav(mapping, credentials),
            StorageProtocol.S3 => BuildS3(mapping, credentials),
            _ => throw new NotSupportedException($"Unsupported protocol '{protocol}'."),
        };

        return config.ToDictionary(kv => prefix + kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildSftp(Mapping mapping, HetznerCredentials creds)
    {
        RequireStorageBox(mapping);

        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "sftp",
            ["HOST"] = mapping.Host,
            ["USER"] = HetznerEndpoints.StorageBoxUser(mapping.Username),
            ["PORT"] = mapping.SshPort.ToString(),
            // The Storage Box runs a restricted but real shell on port 23, so rclone can call
            // md5sum/sha1sum for checksums. Declaring the shell type up front skips rclone's
            // probe, which otherwise costs an extra SSH session out of a small budget on
            // every single mount.
            ["SHELL_TYPE"] = "unix",
            ["MD5SUM_COMMAND"] = "md5sum",
            ["SHA1SUM_COMMAND"] = "sha1sum",
        };

        // A key beats a password when both are present: it survives password rotation and is what
        // Hetzner recommends for unattended access.
        if (!string.IsNullOrWhiteSpace(creds.SshKeyFile))
        {
            config["KEY_FILE"] = creds.SshKeyFile!.Trim();
            if (!string.IsNullOrWhiteSpace(creds.SshKeyPassphrase))
                config["KEY_FILE_PASS"] = RcloneObscure.Obscure(creds.SshKeyPassphrase!);
        }
        else
        {
            config["PASS"] = RcloneObscure.Obscure(creds.Password);
        }

        return config;
    }

    private static Dictionary<string, string> BuildSmb(Mapping mapping, HetznerCredentials creds)
    {
        RequireStorageBox(mapping);
        RequirePassword(creds, "SMB");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "smb",
            ["HOST"] = mapping.Host,
            ["USER"] = HetznerEndpoints.StorageBoxUser(mapping.Username),
            ["PASS"] = RcloneObscure.Obscure(creds.Password),
            ["PORT"] = HetznerEndpoints.SmbPort.ToString(),
            // Hetzner's Samba is not domain-joined; the default "WORKGROUP" is what it expects.
            ["DOMAIN"] = "WORKGROUP",
        };
    }

    private static Dictionary<string, string> BuildWebDav(Mapping mapping, HetznerCredentials creds)
    {
        RequireStorageBox(mapping);
        RequirePassword(creds, "WebDAV");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "webdav",
            ["URL"] = "https://" + mapping.Host,
            // Hetzner runs a plain WebDAV server, not Nextcloud/ownCloud: picking one of those
            // vendors would make rclone use chunked-upload and hash endpoints that do not exist here.
            ["VENDOR"] = "other",
            ["USER"] = HetznerEndpoints.StorageBoxUser(mapping.Username),
            ["PASS"] = RcloneObscure.Obscure(creds.Password),
        };
    }

    private static Dictionary<string, string> BuildS3(Mapping mapping, HetznerCredentials creds)
    {
        if (mapping.Product != HetznerProduct.ObjectStorage)
            throw new InvalidOperationException("S3 requires an Object Storage mapping.");

        var location = HetznerEndpoints.FindLocation(mapping.LocationCode)
            ?? throw new InvalidOperationException($"Unknown Object Storage location '{mapping.LocationCode}'.");
        if (string.IsNullOrWhiteSpace(creds.AccessKeyId) || string.IsNullOrWhiteSpace(creds.SecretAccessKey))
            throw new InvalidOperationException("Object Storage needs an access key and secret key.");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TYPE"] = "s3",
            // rclone ships a first-class Hetzner provider; it sets the quirks (signature version,
            // addressing style, unsupported ACL/storage-class handling) that "Other" leaves to guesswork.
            ["PROVIDER"] = "Hetzner",
            ["ENV_AUTH"] = "false",
            ["REGION"] = location.Code,
            ["ENDPOINT"] = location.Endpoint,
            ["ACCESS_KEY_ID"] = creds.AccessKeyId.Trim(),
            ["SECRET_ACCESS_KEY"] = creds.SecretAccessKey.Trim(),
            // S3 has no real directories. Without markers, an empty folder created on the drive
            // exists only in rclone's memory: invisible to every other tool and gone on remount.
            ["DIRECTORY_MARKERS"] = "true",
        };
    }

    private static void RequireStorageBox(Mapping mapping)
    {
        if (mapping.Product != HetznerProduct.StorageBox)
            throw new InvalidOperationException("This protocol is only available on a Storage Box.");
        if (string.IsNullOrWhiteSpace(mapping.Host))
            throw new InvalidOperationException("The Storage Box username (or host) is required.");
    }

    private static void RequirePassword(HetznerCredentials creds, string protocolName)
    {
        if (string.IsNullOrWhiteSpace(creds.Password))
            throw new InvalidOperationException(
                $"{protocolName} authenticates with the Storage Box password; an SSH key cannot be used for it.");
    }
}
