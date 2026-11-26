namespace Nuplane.Reconciliation;

/// <summary>Stages package loads from pending leases once the admitted store locks have been released.</summary>
internal interface ICoordinatedPackageLoadingActivation
{
    bool Enabled { get; }

    Task<PackageLoadingPreparation> StageAsync(
        LeasedPackageGraphLoadingHandoff handoff,
        IReadOnlyDictionary<string, string> prospectiveActiveVersions,
        CancellationToken cancellationToken);
}
