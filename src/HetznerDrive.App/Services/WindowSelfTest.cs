using System.IO;
using System.Text;
using HetznerDrive.App.ViewModels;
using HetznerDrive.App.Views;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.Services;

/// <summary>
/// Constructs every window and reports which ones loaded.
///
/// A green build says nothing about XAML at runtime: <c>StaticResource</c> lookups, merged resource
/// dictionaries and templates are resolved when a window is loaded, so a renamed brush key compiles
/// perfectly and then crashes the first time someone opens a dialog. This runs that resolution
/// headlessly, which is something CI can do and a human clicking through the UI usually will not.
///
/// Invoked with <c>HetznerDrive.exe --selftest [output-file]</c>. The app writes a report and exits
/// with 0 (all windows loaded) or 1 (at least one failed) — a WinExe has no console to print to.
/// </summary>
internal static class WindowSelfTest
{
    public static int Run(string? outputPath)
    {
        var report = new StringBuilder();
        var failures = 0;

        // Constructing these is harmless: AppController only touches disk in Initialize(), which is
        // deliberately not called here.
        var controller = new AppController();
        var settings = new AppSettings();

        Check("MainWindow", () => new MainWindow(new MainViewModel(controller), controller));

        Check("MappingEditWindow (Storage Box)", () =>
            new MappingEditWindow(new MappingEditViewModel(null, null, settings), controller));

        Check("MappingEditWindow (Object Storage)", () =>
        {
            var vm = new MappingEditViewModel(null, null, settings)
            {
                Product = HetznerProduct.ObjectStorage,
            };
            return new MappingEditWindow(vm, controller);
        });

        Check("SettingsWindow", () =>
            new SettingsWindow(new SettingsViewModel(settings), controller));

        Check("AboutWindow", () => new AboutWindow());

        report.AppendLine(failures == 0
            ? "All windows loaded."
            : $"{failures} window(s) failed to load.");

        var text = report.ToString();
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "hetznerdrive-selftest.txt")
            : outputPath!;
        try { File.WriteAllText(path, text); }
        catch { /* the exit code still carries the verdict */ }

        return failures == 0 ? 0 : 1;

        void Check(string label, Func<System.Windows.Window> make)
        {
            try
            {
                var window = make();
                // ApplyTemplate builds the visual tree, which is when a resource referenced inside a
                // DataTemplate or ControlTemplate — not just at the window's top level — is resolved.
                window.ApplyTemplate();
                window.Close();
                report.AppendLine($"OK   {label}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL {label} -> {Describe(ex)}");
            }
        }
    }

    /// <summary>
    /// Flattens the exception chain. WPF wraps the real cause several layers deep in
    /// XamlParseException, and only the innermost message names the missing key.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}
