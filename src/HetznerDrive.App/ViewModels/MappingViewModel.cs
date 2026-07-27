using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HetznerDrive.App.Services;
using HetznerDrive.CloudFiles;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.ViewModels;

/// <summary>Row view model: one mapping plus its live mount state and per-row commands.</summary>
public sealed partial class MappingViewModel : ObservableObject
{
    private readonly AppController _controller;

    public MappingViewModel(AppController controller, Mapping model)
    {
        _controller = controller;
        Model = model;
    }

    public Mapping Model { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsMounted))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(MountCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnmountCommand))]
    private MountState _state = MountState.Unmounted;

    [ObservableProperty]
    private string? _statusMessage;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.RemoteDescription : Model.Name;
    public string Source => Model.RemoteDescription;
    public bool AutoMount => Model.AutoMount;

    public string ModeText => Model.Mode == MappingMode.OnDemandFolder ? "On-demand" : "Drive";

    /// <summary>
    /// The protocol column. An Auto mapping shows what it settled on so the choice is visible
    /// rather than mysterious — "Auto (SFTP)" beats a bare "Auto" when someone is wondering why
    /// transfers look the way they do.
    /// </summary>
    public string ProtocolText => Model.Protocol == StorageProtocol.Auto
        ? Model.ResolvedProtocol is { } resolved ? $"Auto ({resolved})" : "Auto (not measured)"
        : Model.EffectiveProtocol.ToString();

    /// <summary>Drive letter for drive mode, or the on-demand folder path.</summary>
    public string Location => Model.Mode == MappingMode.OnDemandFolder
        ? OnDemandSyncManager.ResolveFolderPath(Model)
        : Model.MountPoint;

    public bool IsMounted => State == MountState.Mounted;
    public bool IsBusy => State is MountState.Mounting or MountState.Unmounting;

    public string StateText => State switch
    {
        MountState.Mounted => "Mounted",
        MountState.Mounting => "Mounting…",
        MountState.Unmounting => "Unmounting…",
        MountState.Error => "Error",
        _ => "Not mounted",
    };

    private bool CanMount() => State is MountState.Unmounted or MountState.Error;
    private bool CanUnmount() => State == MountState.Mounted;

    [RelayCommand(CanExecute = nameof(CanMount))]
    private async Task MountAsync()
    {
        try { await _controller.MountAsync(this); }
        catch (Exception ex)
        {
            ApplyStatus(MountState.Error, ex.Message);
            MessageBox.Show(ex.Message, "Mount failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnmount))]
    private async Task UnmountAsync()
    {
        try { await _controller.UnmountAsync(this); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unmount failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens the drive or on-demand folder in Explorer.</summary>
    [RelayCommand]
    private void OpenInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Location) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {Location}: {ex.Message}",
                "HetznerDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Copies the drive letter / folder path so it can be pasted elsewhere.</summary>
    [RelayCommand]
    private void CopyPath()
    {
        try { Clipboard.SetText(Location); }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; not worth interrupting the user for.
            StatusMessage = $"Could not copy path: {ex.Message}";
        }
    }

    /// <summary>Called by the controller (on the UI thread) when the mount state changes.</summary>
    public void ApplyStatus(MountState state, string? message)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(message))
            StatusMessage = message;
    }

    /// <summary>Refreshes the protocol column after an Auto benchmark picks a winner.</summary>
    public void RefreshProtocol() => OnPropertyChanged(nameof(ProtocolText));

    /// <summary>Replaces the underlying model after an edit and refreshes bound fields.</summary>
    public void UpdateModel(Mapping model)
    {
        Model = model;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(AutoMount));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ProtocolText));
        OnPropertyChanged(nameof(Location));
    }
}
