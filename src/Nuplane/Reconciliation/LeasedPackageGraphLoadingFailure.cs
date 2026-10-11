namespace Nuplane.Reconciliation;

/// <summary>Describes one package failure produced by deferred Loading.</summary>
/// <param name="PackageId">The failed package identifier.</param>
/// <param name="Reason">The failure diagnostic.</param>
public sealed record LeasedPackageGraphLoadingFailure(string PackageId, string Reason);
