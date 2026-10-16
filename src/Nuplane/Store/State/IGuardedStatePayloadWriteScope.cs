using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.State;

/// <summary>Retains lexical/referent path evidence and every supplemental guard for one state write.</summary>
/// <remarks>The caller owns this scope through writer outcome verification; no root/member lock is acquired.</remarks>
internal interface IGuardedStatePayloadWriteScope : IAsyncDisposable
{
    PhysicalStoreDirectoryHandle Parent { get; }
    StateSlotIdentity Slot { get; }
    PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease PrimaryLease { get; }
    ValueTask RevalidateAsync(CancellationToken cancellationToken);
    void RequireMarkerAbsenceAndGuards();
    PhysicalStoreEntryInfo? ObserveDestination();
    Stream OpenPayloadRead(PhysicalStoreEntryInfo expectedLexicalEntry, long maximumBytes);
    void Publish(string stagedName, PhysicalFileIdentity stagedIdentity, PhysicalStoreEntryInfo? prior);
}
