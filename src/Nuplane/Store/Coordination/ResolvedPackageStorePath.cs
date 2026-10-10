using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Owns a metadata-only configured-path resolution and its retained native evidence.</summary>
/// <remarks>
/// A null authority and root identity means resolution positively observed no reserved authority on the
/// resolved namespace. A retained graph-use result may carry a physical root bound by an immutable lease
/// without carrying a fresh membership candidate. This result is not an admission capability.
/// </remarks>
internal sealed class ResolvedPackageStorePath : IDisposable
{
    private readonly IReadOnlyList<PhysicalStoreHandle> _ownedHandles;
    private readonly Action _revalidate;
    private readonly object _gate = new();
    private bool _disposed;

    internal ResolvedPackageStorePath(
        PhysicalStoreHandle target,
        PhysicalStoreDirectoryHandle? authorityRoot,
        PhysicalRootIdentity? rootIdentity,
        RootMembershipRecord? membershipCandidate,
        PhysicalFileIdentity? membershipLedgerIdentity,
        IReadOnlyList<PhysicalStoreHandle> ownedHandles,
        Action revalidate,
        bool isProspectiveConfiguredRoot = false,
        bool isProspectiveMissingSuffix = false,
        bool isRetainedGraphUseRoot = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedHandles);
        ArgumentNullException.ThrowIfNull(revalidate);
        if ((authorityRoot is null) != (rootIdentity is null) ||
            (membershipCandidate is null) != (membershipLedgerIdentity is null) ||
            (membershipCandidate is null && rootIdentity is not null && !isRetainedGraphUseRoot) ||
            (isRetainedGraphUseRoot && (authorityRoot is null || rootIdentity is null || membershipCandidate is not null)))
            throw new ArgumentException("Authority handle, root identity, and membership candidate must be present together.", nameof(authorityRoot));
        if (isProspectiveMissingSuffix && (rootIdentity is not null || target is not PhysicalStoreDirectoryHandle))
            throw new ArgumentException("A prospective directory target requires an absent authority and a held existing parent.", nameof(isProspectiveMissingSuffix));
        if (isProspectiveConfiguredRoot && !isProspectiveMissingSuffix)
            throw new ArgumentException("A prospective configured root must retain a missing-suffix observation.", nameof(isProspectiveConfiguredRoot));

        Target = target;
        AuthorityRoot = authorityRoot;
        RootIdentity = rootIdentity;
        MembershipCandidate = membershipCandidate;
        MembershipLedgerIdentity = membershipLedgerIdentity;
        IsProspectiveConfiguredRoot = isProspectiveConfiguredRoot;
        IsProspectiveMissingSuffix = isProspectiveMissingSuffix;
        IsRetainedGraphUseRoot = isRetainedGraphUseRoot;
        _ownedHandles = ownedHandles.ToArray();
        _revalidate = revalidate;
    }

    /// <summary>Gets the held final target, or the nearest existing parent for a permitted missing suffix.</summary>
    internal PhysicalStoreHandle Target { get; }

    /// <summary>Gets the held authority-root directory, when one was positively observed.</summary>
    internal PhysicalStoreDirectoryHandle? AuthorityRoot { get; }

    /// <summary>Gets the physical authority-root identity, when one was positively observed.</summary>
    internal PhysicalRootIdentity? RootIdentity { get; }

    /// <summary>Gets the validated structural membership candidate, never an admission capability.</summary>
    internal RootMembershipRecord? MembershipCandidate { get; }

    /// <summary>Gets the exact native ledger-file identity observed with the candidate.</summary>
    internal PhysicalFileIdentity? MembershipLedgerIdentity { get; }

    /// <summary>Whether the configured-root result ends at an absent suffix below its held existing parent.</summary>
    internal bool IsProspectiveConfiguredRoot { get; }

    /// <summary>Whether resolution ended at a verified absent suffix below its held existing parent.</summary>
    internal bool IsProspectiveMissingSuffix { get; }

    /// <summary>Whether the physical root is supplied by an already-live graph-use lease rather than a fresh ledger read.</summary>
    internal bool IsRetainedGraphUseRoot { get; }

    /// <summary>Rechecks the retained namespace, edge, alias, authority, and target observations.</summary>
    internal void Revalidate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _revalidate();
        }
    }

    /// <summary>Releases all retained native handles in reverse acquisition order.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            for (var index = _ownedHandles.Count - 1; index >= 0; index--)
                _ownedHandles[index].Dispose();
        }
    }
}
