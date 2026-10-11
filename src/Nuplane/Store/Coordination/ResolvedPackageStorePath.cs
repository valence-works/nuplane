using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Owns a metadata-only configured-path resolution and its retained native evidence.</summary>
/// <remarks>
/// A null authority and root identity means resolution positively observed no reserved authority on the
/// resolved namespace. An explicit admitted-operation missing-suffix result may retain a Complete membership
/// candidate and the nearest existing parent. A retained graph-use result may carry a physical root bound by an
/// immutable lease without carrying a fresh membership candidate. This result is not an admission capability.
/// </remarks>
internal sealed class ResolvedPackageStorePath : IDisposable
{
    private readonly IReadOnlyList<PhysicalStoreHandle> _ownedHandles;
    private readonly Action<bool> _revalidate;
    private readonly Action<RootMembershipRecord, PhysicalFileIdentity> _revalidateOwnedLedgerOutcome;
    private readonly object _gate = new();
    private bool _disposed;

    internal ResolvedPackageStorePath(
        PhysicalStoreHandle target,
        PhysicalStoreDirectoryHandle? authorityRoot,
        PhysicalRootIdentity? rootIdentity,
        RootMembershipRecord? membershipCandidate,
        PhysicalFileIdentity? membershipLedgerIdentity,
        IReadOnlyList<PhysicalStoreHandle> ownedHandles,
        Action<bool> revalidate,
        Action<RootMembershipRecord, PhysicalFileIdentity> revalidateOwnedLedgerOutcome,
        PhysicalStoreDirectoryHandle? targetParent = null,
        string? targetName = null,
        bool isProspectiveConfiguredRoot = false,
        bool isProspectiveMissingSuffix = false,
        bool isRetainedGraphUseRoot = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedHandles);
        ArgumentNullException.ThrowIfNull(revalidate);
        ArgumentNullException.ThrowIfNull(revalidateOwnedLedgerOutcome);
        if ((authorityRoot is null) != (rootIdentity is null) ||
            (membershipCandidate is null) != (membershipLedgerIdentity is null) ||
            (membershipCandidate is null && rootIdentity is not null && !isRetainedGraphUseRoot) ||
            (isRetainedGraphUseRoot && (authorityRoot is null || rootIdentity is null || membershipCandidate is not null)))
            throw new ArgumentException("Authority handle, root identity, and membership candidate must be present together.", nameof(authorityRoot));
        if (isProspectiveMissingSuffix && target is not PhysicalStoreDirectoryHandle)
            throw new ArgumentException("A prospective directory target requires a held existing parent.", nameof(isProspectiveMissingSuffix));
        if (isProspectiveConfiguredRoot && !isProspectiveMissingSuffix)
            throw new ArgumentException("A prospective configured root must retain a missing-suffix observation.", nameof(isProspectiveConfiguredRoot));
        if ((target is PhysicalStoreFileHandle) != (targetParent is not null && targetName is not null))
            throw new ArgumentException("A held file target must retain its exact held parent and child name.", nameof(targetParent));
        if (targetName is not null)
            PhysicalStoreNames.ValidateSingleComponent(targetName);

        Target = target;
        TargetParent = targetParent;
        TargetName = targetName;
        AuthorityRoot = authorityRoot;
        RootIdentity = rootIdentity;
        MembershipCandidate = membershipCandidate;
        MembershipLedgerIdentity = membershipLedgerIdentity;
        IsProspectiveConfiguredRoot = isProspectiveConfiguredRoot;
        IsProspectiveMissingSuffix = isProspectiveMissingSuffix;
        IsRetainedGraphUseRoot = isRetainedGraphUseRoot;
        _ownedHandles = ownedHandles.ToArray();
        _revalidate = revalidate;
        _revalidateOwnedLedgerOutcome = revalidateOwnedLedgerOutcome;
    }

    /// <summary>Gets the held final target, or the nearest existing parent for a permitted missing suffix.</summary>
    internal PhysicalStoreHandle Target { get; }

    /// <summary>Gets the held parent edge for a final archive-file target.</summary>
    internal PhysicalStoreDirectoryHandle? TargetParent { get; }

    /// <summary>Gets the exact final child name for a final archive-file target.</summary>
    internal string? TargetName { get; }

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
        => Revalidate(allowStableMissingDirectoryRetry: false);

    /// <summary>Rechecks evidence for the one unscoped pre-callback directory-classification retry.</summary>
    /// <remarks>Post-callback and all other callers must use strict <see cref="Revalidate()"/>.</remarks>
    internal void RevalidateForUnenrolledPackageProbe()
        => Revalidate(allowStableMissingDirectoryRetry: true);

    /// <summary>Revalidates original native path evidence after one registry-owned exact ledger replacement.</summary>
    internal void RevalidateOwnedLedgerOutcome(RootMembershipRecord expectedLedger, PhysicalFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(expectedLedger);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (MembershipCandidate is null || RootIdentity != expectedLedger.RootIdentity ||
                MembershipCandidate.EnrollmentEpoch != expectedLedger.EnrollmentEpoch)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "An owned ledger refresh does not match the retained native root and epoch.", expectedLedger.RootIdentity);
            _revalidateOwnedLedgerOutcome(expectedLedger, expectedIdentity);
        }
    }

    private void Revalidate(bool allowStableMissingDirectoryRetry)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _revalidate(allowStableMissingDirectoryRetry);
        }
    }

    /// <summary>Releases all retained native handles in reverse acquisition order.</summary>
    public void Dispose()
    {
        List<Exception>? errors = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            for (var index = _ownedHandles.Count - 1; index >= 0; index--)
            {
                try { _ownedHandles[index].Dispose(); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
            }
        }

        if (errors is { Count: 1 })
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 })
            throw new AggregateException("Configured path native handles could not be fully released.", errors);
    }
}
