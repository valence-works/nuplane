namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Identifies the package-store operation requesting admission.</summary>
public enum PackageStoreAdmissionKind
{
    /// <summary>Reconcile desired and active package state.</summary>
    Reconciliation,
    /// <summary>Install or acquire package content.</summary>
    Installation,
    /// <summary>Restore package state outside a host reconciliation cycle.</summary>
    Restore,
    /// <summary>Recover persisted startup state.</summary>
    StartupRecovery,
    /// <summary>Read package content for loading or metadata inspection.</summary>
    Loading,
    /// <summary>Inspect or execute package-store maintenance.</summary>
    Maintenance
}
