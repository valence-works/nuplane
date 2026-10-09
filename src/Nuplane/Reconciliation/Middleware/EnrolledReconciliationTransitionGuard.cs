using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation.Middleware;

internal static class EnrolledReconciliationTransitionGuard
{
    internal static void RefuseNonemptyTransition(
        PackageResolutionResult resolution,
        PackageChangeSet? changeSet,
        PackageStoreOperationOwner owner)
    {
        if (resolution.ResolvedPackages.Count == 0 && resolution.ResolvedGraphs.Count == 0 &&
            (changeSet is null || changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count == 0))
            return;

        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
            "This enrolled reconciliation slice cannot execute a nonempty package transition.", owner.Root);
    }
}
