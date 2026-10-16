using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Recovers stale graph-use control artifacts under an already retained Complete root owner.</summary>
internal static class StalePackageGraphUseRecovery
{
    internal static async Task RecoverAsync(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry.LockedMemberLocations context,
        PhysicalRootIdentity root,
        RootMembershipRecord verifiedLedger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(verifiedLedger);

        // Capture the verified ledger before entering the serialized context callback. Reading it from
        // inside the callback would reenter the context's operation gate.
        await context.WithValidatedRootAsync(async (_, heldRoot, token) =>
        {
            await new PackageGraphUseRecordStore(files).RecoverAsync(
                heldRoot, root, verifiedLedger, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}
