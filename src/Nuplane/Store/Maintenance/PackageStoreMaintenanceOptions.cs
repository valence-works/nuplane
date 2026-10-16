namespace Nuplane.Store.Maintenance;

/// <summary>Configures the maintenance alias for Nuplane's default package-store root.</summary>
public sealed class PackageStoreMaintenanceOptions
{
    /// <summary>Gets or sets the label used to address the configured default package-store root.</summary>
    public string? RootLabel { get; set; } = "default";
}
