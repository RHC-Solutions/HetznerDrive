using System.Windows;
using Microsoft.Win32;
using HetznerDrive.App.Services;
using HetznerDrive.App.ViewModels;
using HetznerDrive.Core;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly AppController _controller;

    public SettingsWindow(SettingsViewModel vm, AppController controller)
    {
        InitializeComponent();
        _vm = vm;
        _controller = controller;
        DataContext = vm;
        RefreshServiceStatus();
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// Reflects both halves of the service's state: whether it is installed and running, and
    /// whether what it has been told to mount still matches what is configured here. The second is
    /// the one that actually catches people out — editing a mapping does not reach the service
    /// until the elevated publish runs.
    /// </summary>
    private void RefreshServiceStatus()
    {
        var count = _controller.ServiceMappings.Count;
        var state = ServiceControl.GetState();

        var status = state switch
        {
            ServiceState.NotInstalled => "Not installed.",
            ServiceState.Running => "Installed and running.",
            ServiceState.Stopped => "Installed but stopped.",
            ServiceState.Pending => "Installed; changing state…",
            _ => "Installed; state unknown.",
        };

        var detail = count == 0
            ? " No mappings are marked for the service."
            : $" {count} mapping(s) marked for the service.";

        if (state != ServiceState.NotInstalled && _controller.ServiceNeedsPublish())
            detail += " Changes are pending — choose Apply changes.";

        ServiceStatusText.Text = status + detail;
        ServiceStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            state == ServiceState.Running ? "WC.Brush.Success" : "WC.Brush.TextSecondary");

        UninstallServiceButton.IsEnabled = state != ServiceState.NotInstalled;
        PublishServiceButton.IsEnabled = state != ServiceState.NotInstalled;
        InstallServiceButton.Content = state == ServiceState.NotInstalled ? "Install" : "Update";
    }

    private async void OnInstallService(object sender, RoutedEventArgs e) => await RunServiceActionAsync(
        ServiceAction.Install,
        "Install the HetznerDrive service?\n\n"
        + "It runs as LocalSystem and starts at boot, so the mappings you marked stay mounted "
        + "without anyone signed in. Windows will ask for administrator approval.");

    private async void OnPublishService(object sender, RoutedEventArgs e) => await RunServiceActionAsync(
        ServiceAction.Publish,
        "Send the current mappings and credentials to the service and restart it?\n\n"
        + "Windows will ask for administrator approval.");

    private async void OnUninstallService(object sender, RoutedEventArgs e) => await RunServiceActionAsync(
        ServiceAction.Uninstall,
        "Uninstall the HetznerDrive service?\n\n"
        + "Its mounts are unmounted and the machine-wide copy of your credentials is deleted. The "
        + "mappings themselves are kept, and revert to being mounted by this app while you are "
        + "signed in.");

    private async Task RunServiceActionAsync(ServiceAction action, string prompt)
    {
        if (MessageBox.Show(this, prompt, "HetznerDrive service",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        SetServiceButtonsEnabled(false);
        try
        {
            var error = await ServiceSync.RunElevatedAsync(action);
            if (error is not null)
            {
                MessageBox.Show(this, error, "HetznerDrive service",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SetServiceButtonsEnabled(true);
            RefreshServiceStatus();
        }
    }

    private void SetServiceButtonsEnabled(bool enabled)
    {
        InstallServiceButton.IsEnabled = enabled;
        PublishServiceButton.IsEnabled = enabled;
        UninstallServiceButton.IsEnabled = enabled;
    }

    private void OnOpenServiceLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(MachinePaths.LogsDir);
            UpdateCoordinator.OpenUrl(MachinePaths.LogsDir);
        }
        catch (Exception ex)
        {
            // The directory is ACL'd to administrators, so a standard user may not be able to
            // create it — say so rather than failing silently.
            MessageBox.Show(this,
                $"Could not open {MachinePaths.LogsDir}:\n{ex.Message}",
                "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowseCacheDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose default cache location" };
        if (!string.IsNullOrWhiteSpace(_vm.DefaultCacheDir) && System.IO.Directory.Exists(_vm.DefaultCacheDir))
            dlg.InitialDirectory = _vm.DefaultCacheDir;
        if (dlg.ShowDialog(this) == true)
            _vm.DefaultCacheDir = dlg.FolderName;
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_controller.LogsDirectory);
        UpdateCoordinator.OpenUrl(_controller.LogsDirectory);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export settings",
            FileName = "HetznerDrive-settings.json",
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json|XML (*.xml)|*.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        // Snapshot the on-screen edits so the export matches what the user sees.
        var snapshot = new AppSettings();
        _vm.ApplyTo(snapshot);

        try
        {
            SettingsPortability.Export(dlg.FileName, snapshot, _controller.Mappings.Select(m => m.Model));
            MessageBox.Show(this,
                $"Exported {_controller.Mappings.Count} mapping(s) and settings to:\n{dlg.FileName}\n\n" +
                "Passwords and keys were not included — re-enter them after importing.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}",
                "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "Settings files (*.json;*.xml)|*.json;*.xml|JSON (*.json)|*.json|XML (*.xml)|*.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        SettingsBundle bundle;
        try
        {
            bundle = SettingsPortability.Import(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed:\n{ex.Message}",
                "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Import {bundle.Mappings.Count} mapping(s) and app settings from:\n{dlg.FileName}\n\n" +
            "Existing mappings with the same id are updated; others are added. " +
            "Passwords and keys are not imported — you'll re-enter them per mapping.\n\nContinue?",
            "Confirm import", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _controller.ApplyImportedBundle(bundle);
        App.ApplyAutoMountSetting(_controller.Settings.StartAtLogin);

        MessageBox.Show(this, "Import complete.",
            "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Information);

        // Settings were applied and persisted directly; close without the normal save path.
        DialogResult = false;
    }
}
