using Nuplane.Abstractions;

namespace Nuplane.Hosting;

internal sealed record LastKnownGoodStartupRecoveryResult(
    bool Succeeded,
    IReadOnlyList<ResolvedPackage> RecoveredPackages,
    IReadOnlyList<string> FailedPackageIds,
    string Reason)
{
    public static LastKnownGoodStartupRecoveryResult Failed(
        IReadOnlyList<string> failedPackageIds,
        string reason) =>
        new(false, [], failedPackageIds, reason);
}
