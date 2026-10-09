using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Owns a metadata-only configured-path resolution and its retained native evidence.</summary>
/// <remarks>
/// A null authority, root identity, and membership candidate means only that resolution positively
/// observed no reserved authority on the complete path. This result is not an admission capability.
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
        IReadOnlyList<PhysicalStoreHandle> ownedHandles,
        Action revalidate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedHandles);
        ArgumentNullException.ThrowIfNull(revalidate);
        if ((authorityRoot is null) != (rootIdentity is null) || (rootIdentity is null) != (membershipCandidate is null))
            throw new ArgumentException("Authority handle, root identity, and membership candidate must be present together.", nameof(authorityRoot));

        Target = target;
        AuthorityRoot = authorityRoot;
        RootIdentity = rootIdentity;
        MembershipCandidate = membershipCandidate;
        _ownedHandles = ownedHandles.ToArray();
        _revalidate = revalidate;
    }

    /// <summary>Gets the held final directory or archive handle.</summary>
    internal PhysicalStoreHandle Target { get; }

    /// <summary>Gets the held authority-root directory, when one was positively observed.</summary>
    internal PhysicalStoreDirectoryHandle? AuthorityRoot { get; }

    /// <summary>Gets the physical authority-root identity, when one was positively observed.</summary>
    internal PhysicalRootIdentity? RootIdentity { get; }

    /// <summary>Gets the validated structural membership candidate, never an admission capability.</summary>
    internal RootMembershipRecord? MembershipCandidate { get; }

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
