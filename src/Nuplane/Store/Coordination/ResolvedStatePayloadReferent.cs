using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Descriptive no-follow metadata for one lexical or terminal state-payload slot.</summary>
/// <remarks>The parent handle is owned by the enclosing <see cref="ResolvedStatePayloadReferent"/>.</remarks>
internal sealed record StatePayloadSlotMetadata
{
    internal StatePayloadSlotMetadata(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics nameSemantics,
        string name,
        PhysicalStoreEntryInfo? entry,
        string? linkTargetText)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(parentIdentity);
        ArgumentNullException.ThrowIfNull(nameSemantics);
        PhysicalStoreNames.ValidateSingleComponent(name);
        if ((entry?.Kind == PhysicalStoreEntryKind.SymbolicLink) != (linkTargetText is not null))
            throw new ArgumentException("Only a symbolic-link slot must carry its exact target text.", nameof(linkTargetText));

        Parent = parent;
        ParentIdentity = parentIdentity;
        NameSemantics = nameSemantics;
        Name = name;
        Entry = entry;
        LinkTargetText = linkTargetText;
    }

    internal PhysicalStoreDirectoryHandle Parent { get; }
    internal PhysicalFileIdentity ParentIdentity { get; }
    internal PhysicalStoreNameSemantics NameSemantics { get; }
    internal string Name { get; }
    internal PhysicalStoreEntryInfo? Entry { get; }
    internal string? LinkTargetText { get; }
}

/// <summary>Owns a metadata-only resolution of a state payload and its terminal symbolic-link slots.</summary>
/// <remarks>
/// Revalidation replays retained ancestry, native name profiles, authority, and symbolic-link identity/target text.
/// An existing final regular-file identity is descriptive and is not pinned; the caller must verify its selected
/// leaf while holding the relevant supplemental slot guards. A positively absent final leaf remains absent on replay.
/// </remarks>
internal sealed class ResolvedStatePayloadReferent : IDisposable
{
    private readonly ResolvedPackageStorePath _resolution;
    private readonly Action<StatePayloadSlotMetadata> _revalidateAbsentSlot;
    private readonly Action<PhysicalFileIdentity> _revalidateAfterLexicalReplacement;
    private readonly object _gate = new();
    private bool _disposed;

    internal ResolvedStatePayloadReferent(
        ResolvedPackageStorePath resolution,
        IReadOnlyList<StatePayloadSlotMetadata> terminalLinkSlots,
        StatePayloadSlotMetadata finalPayloadSlot,
        Action<StatePayloadSlotMetadata> revalidateAbsentSlot,
        Action<PhysicalFileIdentity> revalidateAfterLexicalReplacement)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(terminalLinkSlots);
        ArgumentNullException.ThrowIfNull(finalPayloadSlot);
        ArgumentNullException.ThrowIfNull(revalidateAbsentSlot);
        ArgumentNullException.ThrowIfNull(revalidateAfterLexicalReplacement);
        if (resolution.Target is not PhysicalStoreDirectoryHandle ||
            (finalPayloadSlot.Entry is not null && finalPayloadSlot.Entry.Kind != PhysicalStoreEntryKind.RegularFile))
        {
            throw new ArgumentException("A state-payload referent must end at a held parent and regular or absent leaf.", nameof(resolution));
        }
        if (terminalLinkSlots.Any(static slot => slot.Entry?.Kind != PhysicalStoreEntryKind.SymbolicLink))
            throw new ArgumentException("Every terminal state-payload slot must be a symbolic link.", nameof(terminalLinkSlots));

        _resolution = resolution;
        _revalidateAbsentSlot = revalidateAbsentSlot;
        _revalidateAfterLexicalReplacement = revalidateAfterLexicalReplacement;
        TerminalLinkSlots = terminalLinkSlots.ToArray();
        FinalPayloadSlot = finalPayloadSlot;
        RequestedSlot = TerminalLinkSlots.Count == 0 ? finalPayloadSlot : TerminalLinkSlots[0];
    }

    /// <summary>Gets the held final referent parent directory.</summary>
    internal PhysicalStoreDirectoryHandle FinalParent
        => (PhysicalStoreDirectoryHandle)_resolution.Target;

    /// <summary>Gets every final-link slot traversed, excluding aliases used only in parent paths.</summary>
    internal IReadOnlyList<StatePayloadSlotMetadata> TerminalLinkSlots { get; }

    /// <summary>Gets the final regular payload slot, or the positively absent leaf reached through terminal links.</summary>
    internal StatePayloadSlotMetadata FinalPayloadSlot { get; }

    /// <summary>Gets the outer lexical slot; for a direct file this is <see cref="FinalPayloadSlot"/>.</summary>
    internal StatePayloadSlotMetadata RequestedSlot { get; }

    /// <summary>Gets the retained physical root identity, when one was observed.</summary>
    internal PhysicalRootIdentity? RootIdentity => _resolution.RootIdentity;

    /// <summary>Gets the retained authority-root handle, when one was observed.</summary>
    internal PhysicalStoreDirectoryHandle? AuthorityRoot => _resolution.AuthorityRoot;

    /// <summary>Gets descriptive membership metadata, when one was observed.</summary>
    internal RootMembershipRecord? MembershipCandidate => _resolution.MembershipCandidate;

    /// <summary>Gets the native identity associated with descriptive membership metadata.</summary>
    internal PhysicalFileIdentity? MembershipLedgerIdentity => _resolution.MembershipLedgerIdentity;

    /// <summary>Replays parent, authority, alias, and absent-leaf observations.</summary>
    internal void Revalidate(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _resolution.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();
            if (FinalPayloadSlot.Entry is null)
                _revalidateAbsentSlot(FinalPayloadSlot);
        }
    }

    /// <summary>Replays all path evidence after the caller has verified its own lexical replacement.</summary>
    /// <remarks>Only the original outer terminal-link edge and a directly replaced absent leaf may transition.</remarks>
    internal void RevalidateAfterLexicalReplacement(
        PhysicalFileIdentity publishedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishedIdentity);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _revalidateAfterLexicalReplacement(publishedIdentity);
            if (FinalPayloadSlot.Entry is null && !ReferenceEquals(RequestedSlot, FinalPayloadSlot))
                _revalidateAbsentSlot(FinalPayloadSlot);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Releases all retained path and alias handles.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _resolution.Dispose();
        }
    }
}
