using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HetznerDrive.App.Services;

/// <summary>
/// Registers a cascading Explorer right-click menu ("HetznerDrive ▸ Copy share link / Open console /
/// Copy remote path") scoped to HetznerDrive locations. Uses static per-user registry verbs with an
/// <c>AppliesTo</c> filter (Advanced Query Syntax) so the menu only appears on files/folders inside
/// a mounted drive or on-demand folder — no COM shell extension required. Fully reversible (HKCU).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellMenu
{
    private const string MenuKeyName = "HetznerDrive";
    private static readonly string[] Classes = { "*", "Directory" };

    private static readonly (string Sub, string Verb, string Label)[] Items =
    {
        ("01copylink", ShellCommand.CopyLinkVerb, "Copy share link"),
        ("02console",  ShellCommand.ConsoleVerb,  "Open Hetzner console"),
        ("03copypath", ShellCommand.CopyPathVerb, "Copy remote path"),
    };

    /// <summary>
    /// (Re)registers the menu, scoped to <paramref name="rootPaths"/> (drive roots like "H:" and
    /// on-demand folder paths). With no roots the menu is removed.
    /// </summary>
    public static void Register(string? exePath, IReadOnlyCollection<string> rootPaths)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        if (rootPaths.Count == 0) { Unregister(); return; }

        // AppliesTo: show only when the item's path starts with one of our roots (~< = "starts with").
        var appliesTo = string.Join(" OR ",
            rootPaths.Select(r => $"System.ItemPathDisplay:~<\"{r.TrimEnd('\\')}\""));
        var icon = $"{exePath},0";

        try
        {
            foreach (var cls in Classes)
            {
                using var menu = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell\{MenuKeyName}");
                menu!.SetValue("MUIVerb", "HetznerDrive");
                menu.SetValue("Icon", icon);
                menu.SetValue("AppliesTo", appliesTo);

                using var sub = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{cls}\shell\{MenuKeyName}\ExtendedSubCommands\shell");
                // Remove any stale items first so re-registration is clean.
                foreach (var existing in sub!.GetSubKeyNames()) sub.DeleteSubKeyTree(existing, false);

                foreach (var (subName, verb, label) in Items)
                {
                    using var item = sub.CreateSubKey(subName);
                    item!.SetValue("MUIVerb", label);
                    item.SetValue("Icon", icon);
                    using var command = item.CreateSubKey("command");
                    command!.SetValue(null, $"\"{exePath}\" --shell {verb} \"%1\"");
                }
            }
        }
        catch
        {
            // Context-menu registration is best-effort; never block startup over it.
        }
    }

    public static void Unregister()
    {
        foreach (var cls in Classes)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{cls}\shell\{MenuKeyName}", false); }
            catch { /* ignore */ }
        }
    }
}
