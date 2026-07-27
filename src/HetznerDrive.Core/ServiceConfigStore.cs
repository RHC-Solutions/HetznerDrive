using System.Runtime.Versioning;
using System.Text.Json;
using HetznerDrive.Core.Models;

namespace HetznerDrive.Core;

/// <summary>
/// The handover between the tray app and the Windows service: the set of mappings the service
/// should keep mounted, plus their credentials.
///
/// This is deliberately a file rather than an IPC channel. The service's job is to converge on a
/// desired state, and a file is that state — it survives restarts, needs no protocol or pipe
/// security, and lets the service recover by re-reading rather than by replaying messages. The
/// service watches the directory and reconciles whenever it changes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _mappingsPath;
    private readonly string? _credentialsPath;

    public ServiceConfigStore(string? mappingsPath = null, string? credentialsPath = null)
    {
        _mappingsPath = mappingsPath ?? MachinePaths.MappingsFile;
        _credentialsPath = credentialsPath;
    }

    /// <summary>Directory the service watches for changes.</summary>
    public string WatchDirectory => Path.GetDirectoryName(_mappingsPath) ?? MachinePaths.BaseDir;

    public string MappingsPath => _mappingsPath;

    /// <summary>Mappings the service should mount. Empty when nothing is configured yet.</summary>
    public List<Mapping> LoadMappings()
    {
        if (!File.Exists(_mappingsPath)) return new List<Mapping>();
        try
        {
            var json = File.ReadAllText(_mappingsPath);
            return JsonSerializer.Deserialize<List<Mapping>>(json, Options) ?? new List<Mapping>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A torn or partially-written file must not take the service down; the writer replaces
            // it atomically, so the next change event will bring a good one.
            return new List<Mapping>();
        }
    }

    /// <summary>
    /// Publishes the desired state. Only mappings that are actually service-hosted are written, and
    /// only their credentials — the tray app's other mappings never leave the user profile.
    /// </summary>
    public void Publish(IEnumerable<Mapping> mappings, Func<Guid, HetznerCredentials?> credentialLookup)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(credentialLookup);

        MachinePaths.EnsureDirectoryFor(_mappingsPath);

        var wanted = mappings
            .Where(m => m is { RunAsService: true, Mode: MappingMode.DriveLetter })
            .ToList();

        var json = JsonSerializer.Serialize(wanted, Options);
        var tmp = _mappingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _mappingsPath, overwrite: true);

        var credentials = new Dictionary<Guid, HetznerCredentials>();
        foreach (var mapping in wanted)
        {
            if (credentialLookup(mapping.Id) is { } creds)
                credentials[mapping.Id] = creds;
        }

        var store = CredentialStore.ForService(_credentialsPath);
        store.Load();
        store.ReplaceAll(credentials);
    }

    /// <summary>Loads the machine credential store the service authenticates with.</summary>
    public CredentialStore LoadCredentials()
    {
        var store = CredentialStore.ForService(_credentialsPath);
        try { store.Load(); }
        catch (Exception)
        {
            // An unreadable blob (wrong scope, corrupted, restored from another machine) is
            // recoverable by re-publishing from the app; report it as "no credentials" so the
            // service logs a clean per-mapping failure instead of crashing at startup.
        }
        return store;
    }

    /// <summary>Removes the published state entirely (used when the service is uninstalled).</summary>
    public void Clear()
    {
        foreach (var path in new[] { _mappingsPath, _credentialsPath ?? MachinePaths.CredentialsFile })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
