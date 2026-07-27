namespace HetznerDrive.Core.Models;

/// <summary>
/// Secrets for one mapping. Which fields matter depends on the mapping's
/// <see cref="HetznerProduct"/>: a Storage Box authenticates with a password or an SSH key, while
/// Object Storage uses an S3 access/secret pair. Stored encrypted at rest (see
/// <c>CredentialStore</c>) and never written to the plaintext mappings file.
/// </summary>
public sealed class HetznerCredentials
{
    // --- Storage Box ---

    /// <summary>Storage Box account password (also used for SMB and WebDAV).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Optional path to a private key used instead of the password for SFTP. SMB and WebDAV cannot
    /// use a key, so a key-only mapping is restricted to SFTP.
    /// </summary>
    public string? SshKeyFile { get; set; }

    /// <summary>Passphrase protecting <see cref="SshKeyFile"/>, if it has one.</summary>
    public string? SshKeyPassphrase { get; set; }

    // --- Object Storage ---

    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>True when SFTP can authenticate: a key is enough, otherwise a password is needed.</summary>
    public bool HasSshAuth =>
        !string.IsNullOrWhiteSpace(Password) || !string.IsNullOrWhiteSpace(SshKeyFile);

    /// <summary>True when the credentials satisfy <paramref name="product"/>.</summary>
    public bool IsCompleteFor(HetznerProduct product) => product switch
    {
        HetznerProduct.ObjectStorage =>
            !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey),
        _ => HasSshAuth,
    };

    /// <summary>
    /// True when the credentials work for <paramref name="protocol"/> specifically. A key-only
    /// Storage Box login is valid for SFTP but not for SMB or WebDAV, which are password-only.
    /// </summary>
    public bool SupportsProtocol(StorageProtocol protocol) => protocol switch
    {
        StorageProtocol.S3 => !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey),
        StorageProtocol.Sftp => HasSshAuth,
        StorageProtocol.Smb or StorageProtocol.WebDav => !string.IsNullOrWhiteSpace(Password),
        _ => true,
    };
}
