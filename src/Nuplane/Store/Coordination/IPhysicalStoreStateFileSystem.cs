using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Provides held-parent state payload reads and lexical state-entry replacement.</summary>
/// <remarks>
/// Callers retain every lexical/referent slot guard, establish marker absence before opening payloads,
/// and replay path/alias authority through final verification. These operations confer no such authority.
/// Ordinary state hardlinks and final symbolic links do not relax control or package-file contracts.
/// Namespace transitions are not a compare-and-swap against non-cooperating filesystem writers. An
/// exception may follow a completed transition; callers must inspect and preserve recovery evidence.
/// </remarks>
internal interface IPhysicalStoreStateFileSystem
{
    /// <summary>Reads one selected regular state entry, allowing ordinary hardlinks without following links.</summary>
    /// <remarks>The stream retains native handle leases and verifies parent/profile and name-to-file binding around I/O.</remarks>
    Stream OpenStatePayloadReadStream(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo expectedEntry,
        long maximumBytes);

    /// <summary>Moves a staged single-link regular file onto the original selected lexical state entry.</summary>
    /// <remarks>A prior entry may be a regular hardlink or symbolic link; the final referent is never replaced.</remarks>
    PhysicalStoreEntryInfo PublishStateFileAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        StateSlotIdentity destination,
        PhysicalStoreEntryInfo? expectedPriorEntry);
}
