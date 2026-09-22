using Nuplane.Abstractions;

namespace Nuplane.Hosting;

internal sealed record LastKnownGoodStartupRecoveryResult(
    bool Succeeded,
    IReadOnlyList<ResolvedPackage> RecoveredPackages,
    IReadOnlyList<string> FailedPackageIds,
    string Reason)
{
    /// <summary>
    /// The <see cref="Reason"/> reported when recovery could not take the store lock even after
    /// waiting up to
    /// <see cref="Nuplane.Reconciliation.Configuration.ReconciliationOptions.StartupRecoveryStoreLockTimeout"/>
    /// — the same condition
    /// <see cref="Nuplane.Reconciliation.Models.ReconciliationSkipReason.StoreLockUnavailable"/>
    /// reports for a reconciliation cycle, which does not wait. Recovery read nothing and republished
    /// nothing: a host configured with <c>StartupFailurePolicy.UseLastKnownGood</c> should expect
    /// startup to fail when it sees this reason, because recovery genuinely could not establish a
    /// package set — not because of a transient, short-lived race it could otherwise have waited out.
    /// </summary>
    public const string StoreLockUnavailableReason = "last-known-good-store-lock-unavailable";

    public static LastKnownGoodStartupRecoveryResult Failed(
        IReadOnlyList<string> failedPackageIds,
        string reason) =>
        new(false, [], failedPackageIds, reason);
}
