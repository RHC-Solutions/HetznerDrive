using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

/// <summary>
/// Stores Hetzner secrets encrypted at rest using Windows DPAPI. The encrypted blob is a JSON map
/// of mapping-id → credentials, so a secret is never written in plaintext.
///
/// Two scopes are in play. The tray app uses <see cref="DataProtectionScope.CurrentUser"/>, which
/// binds the blob to one Windows account. The Windows service cannot use that — it runs as
/// LocalSystem and would be unable to decrypt the user's blob — so its copy is protected at
/// <see cref="DataProtectionScope.LocalMachine"/> scope instead. That is a genuinely weaker
/// boundary: *any* process on the machine can call Unprotect on a LocalMachine blob. The file ACL
/// is therefore restricted to SYSTEM and Administrators, which is what actually keeps other users
/// out; see <see cref="RestrictToAdministrators"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    // Extra entropy mixed into DPAPI so the blob is scoped to this app, not just the user/machine.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HetznerDrive.v1.credentials");

    private readonly string _filePath;
    private readonly DataProtectionScope _scope;
    private Dictionary<string, HetznerCredentials> _cache = new();

    public CredentialStore(string? filePath = null)
        : this(filePath ?? AppPaths.CredentialsFile, DataProtectionScope.CurrentUser)
    {
    }

    private CredentialStore(string filePath, DataProtectionScope scope)
    {
        _filePath = filePath;
        _scope = scope;
    }

    /// <summary>
    /// Whether to lock the file down. Only the real machine store is hardened: a LocalMachine blob
    /// written somewhere else still needs to be readable by whoever wrote it.
    /// </summary>
    private bool ShouldRestrictAcl =>
        _scope == DataProtectionScope.LocalMachine && MachinePaths.IsMachineStorePath(_filePath);

    /// <summary>
    /// The machine-wide store the Windows service reads, protected at LocalMachine scope and
    /// locked down to SYSTEM + Administrators by ACL.
    /// </summary>
    public static CredentialStore ForService(string? filePath = null) =>
        new(filePath ?? MachinePaths.CredentialsFile, DataProtectionScope.LocalMachine);

    /// <summary>Loads and decrypts the store from disk. Safe to call when the file is absent.</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, HetznerCredentials>();
            return;
        }

        var protectedBytes = File.ReadAllBytes(_filePath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, _scope);
        var json = Encoding.UTF8.GetString(plainBytes);
        _cache = JsonSerializer.Deserialize<Dictionary<string, HetznerCredentials>>(json)
                 ?? new Dictionary<string, HetznerCredentials>();
    }

    public HetznerCredentials? Get(Guid mappingId) =>
        _cache.TryGetValue(mappingId.ToString("N"), out var c) ? c : null;

    public void Set(Guid mappingId, HetznerCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _cache[mappingId.ToString("N")] = credentials;
        Save();
    }

    public void Remove(Guid mappingId)
    {
        if (_cache.Remove(mappingId.ToString("N")))
            Save();
    }

    /// <summary>Replaces the whole store with <paramref name="credentials"/> and persists it.</summary>
    public void ReplaceAll(IReadOnlyDictionary<Guid, HetznerCredentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _cache = credentials.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value);
        Save();
    }

    private void Save()
    {
        if (_scope == DataProtectionScope.LocalMachine) MachinePaths.EnsureDirectoryFor(_filePath);
        else AppPaths.EnsureCreated();

        var json = JsonSerializer.Serialize(_cache);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, _scope);

        // Write atomically so a crash mid-write can't corrupt the store.
        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _filePath, overwrite: true);

        // Harden after the move, not before. Stripping the writer's own access first would deny it
        // the DELETE right that renaming the temp file requires -- which fails for any caller whose
        // token is not already elevated. The containing directory is restricted regardless, so the
        // file is never broadly readable in between.
        if (ShouldRestrictAcl) RestrictToAdministrators(_filePath);
    }

    /// <summary>
    /// Strips inherited permissions and grants only SYSTEM and Administrators. A LocalMachine DPAPI
    /// blob is decryptable by anything running on the box, so the file permission — not the
    /// encryption — is what stops a standard user reading the service's stored passwords.
    /// </summary>
    private static void RestrictToAdministrators(string path)
    {
        var security = new FileSecurity();
        // Break inheritance without copying the inherited entries, or Users would keep read access.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }
}
