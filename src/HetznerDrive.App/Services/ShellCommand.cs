using System.Diagnostics;
using System.IO;
using System.Windows;
using HetznerDrive.CloudFiles;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.Services;

/// <summary>
/// Handles the Explorer right-click verbs (invoked as <c>HetznerDrive.exe --shell &lt;verb&gt; "path"</c>):
/// maps the clicked path to its mapping and remote key, then copies a share link / remote path, or
/// opens the Hetzner console. Runs standalone (no main UI) and exits.
/// </summary>
internal static class ShellCommand
{
    public const string CopyLinkVerb = "copylink";
    public const string ConsoleVerb = "console";
    public const string CopyPathVerb = "copypath";

    private static readonly TimeSpan LinkExpiry = TimeSpan.FromDays(7);

    /// <summary>Hetzner has no per-bucket deep link that works without a project id, so this is the root.</summary>
    private const string ConsoleUrl = "https://console.hetzner.com/";

    public static void Run(string verb, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("No path was supplied.");

            var (mapping, key) = Resolve(path)
                ?? throw new InvalidOperationException("This item is not inside a HetznerDrive location.");

            switch (verb.ToLowerInvariant())
            {
                case CopyPathVerb:
                    var remotePath = DescribeRemotePath(mapping, key);
                    SetClipboard(remotePath);
                    Info($"Copied remote path:\n\n{remotePath}");
                    break;

                case ConsoleVerb:
                    OpenUrl(ConsoleUrl);
                    break;

                case CopyLinkVerb:
                    CopyShareLink(mapping, key, path);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown shell verb '{verb}'.");
            }
        }
        catch (Exception ex)
        {
            Warn($"HetznerDrive could not complete that action:\n\n{ex.Message}");
        }
    }

    private static void CopyShareLink(Mapping mapping, string key, string path)
    {
        if (IsDirectory(path))
        {
            Warn("Share links can only be created for files, not folders.");
            return;
        }

        // Only Object Storage can mint a URL. A Storage Box has no presigned-URL equivalent, so
        // saying that plainly beats copying something that will not work.
        if (mapping.Product != HetznerProduct.ObjectStorage)
        {
            Warn("Share links are an Object Storage feature. A Storage Box has no presigned-URL " +
                 "equivalent — use Hetzner's own sharing options in the console instead.");
            return;
        }

        var creds = LoadCredentials(mapping.Id)
            ?? throw new InvalidOperationException("No saved credentials for this mapping.");

        using var client = S3StorageClient.ForMapping(mapping, creds);
        var url = client.CreateShareLink(key, LinkExpiry);
        if (string.IsNullOrWhiteSpace(url))
        {
            Warn("A share link could not be created for this item.");
            return;
        }

        SetClipboard(url);
        Info($"A share link (valid {LinkExpiry.TotalDays:0} days) was copied to the clipboard.");
    }

    /// <summary>Renders the remote location in the form that is meaningful for the protocol.</summary>
    private static string DescribeRemotePath(Mapping mapping, string key) =>
        mapping.Product == HetznerProduct.ObjectStorage
            ? $"s3://{mapping.BucketName}/{key}"
            : mapping.EffectiveProtocol switch
            {
                StorageProtocol.Smb => $@"\\{mapping.Host}\{HetznerEndpoints.SmbShare(mapping.Username)}\{key.Replace('/', '\\')}",
                StorageProtocol.WebDav => $"https://{mapping.Host}/{key}",
                _ => $"sftp://{HetznerEndpoints.StorageBoxUser(mapping.Username)}@{mapping.Host}:{mapping.SshPort}/{key}",
            };

    /// <summary>Finds the mapping whose local root contains <paramref name="path"/> and returns the key.</summary>
    private static (Mapping Mapping, string Key)? Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var mapping in new MappingStore().Load())
        {
            var root = RootFor(mapping);
            if (string.IsNullOrEmpty(root)) continue;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = full[root.Length..].Replace('\\', '/').TrimStart('/');
            return (mapping, mapping.KeyPrefix + relative);
        }
        return null;
    }

    private static string RootFor(Mapping mapping)
    {
        if (mapping.Mode == MappingMode.OnDemandFolder)
        {
            var folder = OnDemandSyncManager.ResolveFolderPath(mapping);
            return folder.TrimEnd('\\') + "\\";
        }
        // Drive letter, e.g. "H:\".
        return mapping.DriveTarget.TrimEnd('\\') + "\\";
    }

    private static HetznerCredentials? LoadCredentials(Guid mappingId)
    {
        var store = new CredentialStore();
        store.Load();
        return store.Get(mappingId);
    }

    private static bool IsDirectory(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static void SetClipboard(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch { Thread.Sleep(120); }
        }
        throw new InvalidOperationException("The clipboard was busy; try again.");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static void Info(string message) =>
        MessageBox.Show(message, "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void Warn(string message) =>
        MessageBox.Show(message, "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
}
