using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HetznerDrive.Core.Models;

namespace HetznerDrive.App.Views;

/// <summary>Friendly label for a <see cref="MappingMode"/> in the Mode dropdown.</summary>
public sealed class MappingModeToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MappingMode.OnDemandFolder
            ? "On-demand folder (like OneDrive / Google Drive)"
            : "Drive letter (mapped drive)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Friendly label for a <see cref="HetznerProduct"/>. The two products are easy to confuse, so the
/// label names the hostname pattern rather than just the product.
/// </summary>
public sealed class ProductToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HetznerProduct.ObjectStorage
            ? "Object Storage (S3 buckets — *.your-objectstorage.com)"
            : "Storage Box (SFTP / SMB / WebDAV — *.your-storagebox.de)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Friendly label for a <see cref="StorageProtocol"/> in the protocol dropdown.</summary>
public sealed class ProtocolToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            StorageProtocol.Auto => "Auto — measure and use the fastest (recommended)",
            StorageProtocol.Sftp => $"SFTP / SSH (port {HetznerEndpoints.SshPort})",
            StorageProtocol.Smb => $"SMB / CIFS (port {HetznerEndpoints.SmbPort})",
            StorageProtocol.WebDav => $"WebDAV over HTTPS (port {HetznerEndpoints.WebDavPort})",
            StorageProtocol.S3 => "S3",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value is non-null (used to enable Edit/Delete on selection).</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a bool to Visibility (true → Visible, false → Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
