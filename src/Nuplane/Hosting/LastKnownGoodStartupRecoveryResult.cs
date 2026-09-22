using Nuplane.Abstractions;

namespace Nuplane.Hosting;

internal sealed record LastKnownGoodStartupRecoveryResult(
    bool Succeeded,
    IReadOnlyList<ResolvedPackage> RecoveredPackages,
    IReadOnlyList<string> FailedPackageIds,
    string Reason)
{
    /// <summary>
    /// The <see cref="Reason"/> reported when recovery could not take the store lock — mirroring
    /// <see cref="Nuplane.Reconciliation.Models.ReconciliationSkipReason.StoreLockUnavailable"/> for
    /// the same condition on a reconciliation cycle. Recovery read nothing and republished nothing.
    /// </summary>
    public const string StoreLockUnavailableReason = "last-known-good-store-lock-unavailable";

    public static LastKnownGoodStartupRecoveryResult Failed(
        IReadOnlyList<string> failedPackageIds,
        string reason) =>
        new(false, [], failedPackageIds, reason);
}
