using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Execution-only install rows joined from deletion journals beside the ordinary inventory.</summary>
/// <param name="Installs">Journal-bound original installs to plan beside the current completed installs.</param>
/// <param name="PositivelyAbsent">The subset whose original install and quarantine were proven absent.</param>
internal sealed record PackageStoreExecutionInstallOverlay(
    IReadOnlyList<PackageInstallIdentity> Installs,
    IReadOnlyCollection<PackageInstallIdentity> PositivelyAbsent);
