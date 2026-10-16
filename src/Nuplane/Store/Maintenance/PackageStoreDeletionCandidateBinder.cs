using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Binds one current eligible row to exact native ancestry and a bounded install baseline.</summary>
/// <remarks>The caller must already hold the live full-catalog execution operation.</remarks>
internal sealed class PackageStoreDeletionCandidateBinder
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly PackageInstallIdentityReader _identities;
    private readonly PhysicalStoreDeletionManifestReader _manifests;

    internal PackageStoreDeletionCandidateBinder(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("Deletion binding requires native file-name observations.");
        _directories = files as IPhysicalStoreDirectoryNameFileSystem
            ?? throw Unsupported("Deletion binding requires native directory-name observations.");
        _identities = new PackageInstallIdentityReader(files);
        _manifests = new PhysicalStoreDeletionManifestReader(files);
    }

    internal PackageStoreDeletionCandidate Bind(
        PackageStoreRetentionPlanEntry planEntry,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreDirectoryHandle rootHandle,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planEntry);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(revalidateOperation);
        cancellationToken.ThrowIfCancellationRequested();
        revalidateOperation(cancellationToken);

        var expectedInstall = planEntry.Install;
        if (expectedInstall.Root != expectedRoot)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "The current retention row belongs to another physical root.", expectedRoot);

        var observation = _identities.Observe(rootHandle, expectedRoot,
            expectedInstall.RootRelativeInstallPath,
            expectedInstall.PackageId,
            expectedInstall.Version,
            expectedInstall.VerifiedArchiveHash);
        try
        {
            var observedInstall = observation.InstallIdentity;
            if (observedInstall != expectedInstall)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The native completed install no longer matches the exact current retention row.", expectedRoot);

            var location = observation.ObserveCanonicalDeletionLocation();
            var manifest = _manifests.Capture(
                observation.InstallDirectory,
                observedInstall,
                location.PackageParentHandle,
                location.PackageParentIdentity,
                location.PackageParentSemantics,
                location.OriginalBasename,
                location.InstallDirectorySemantics,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            revalidateOperation(cancellationToken);
            var finalLocation = observation.ObserveCanonicalDeletionLocation();
            if (location != finalLocation || manifest.InstallDirectoryIdentity != finalLocation.InstallDirectoryIdentity)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The canonical install location changed during candidate binding.", expectedRoot);

            return new PackageStoreDeletionCandidate(planEntry, observedInstall, manifest, observation, location,
                _manifests, revalidateOperation);
        }
        catch
        {
            observation.Dispose();
            throw;
        }
    }

    internal void VerifyPreparedOriginal(
        PackageStoreDeletionJournalStore.DiscoveredJournal discovered,
        PhysicalStoreDirectoryHandle rootHandle,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken)
    {
        var journal = discovered.Journal;
        revalidateOperation(cancellationToken);
        RequireDirectory(_files.InspectHandle(rootHandle), journal.StoreRoot.HandleIdentity, journal.StoreRoot,
            "The held store root changed before verifying the original Prepared install.");
        if (RequireSupportedProfile(rootHandle, journal.StoreRoot) != discovered.RootSemantics)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The held store root profile differs from the Prepared journal discovery binding.", journal.StoreRoot);
        using var observation = _identities.Observe(rootHandle, journal.StoreRoot,
            journal.OriginalInstall.RootRelativeInstallPath,
            journal.OriginalInstall.PackageId,
            journal.OriginalInstall.Version,
            journal.OriginalInstall.VerifiedArchiveHash);
        if (observation.InstallIdentity != journal.OriginalInstall)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The original install path no longer identifies the exact journal-bound directory.", journal.StoreRoot);
        var location = observation.ObserveCanonicalDeletionLocation();
        RequireJournalLocation(journal, location, originalName: true);
        var manifest = _manifests.Capture(observation.InstallDirectory, observation.InstallIdentity,
            location.PackageParentHandle, location.PackageParentIdentity, location.PackageParentSemantics,
            location.OriginalBasename, location.InstallDirectorySemantics, cancellationToken);
        if (journal.Manifest.RequireExactSubset(manifest) != 0)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The original completed install no longer matches the full Prepared baseline.", journal.StoreRoot);
        if (_files.InspectChildNoFollow(location.PackageParentHandle, journal.QuarantineBasename) is not null)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The exact quarantine slot is occupied while the original Prepared install remains present.", journal.StoreRoot);
        revalidateOperation(cancellationToken);
        if (observation.ObserveCanonicalDeletionLocation() != location)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The original canonical install location changed during journal verification.", journal.StoreRoot);
    }

    internal void VerifyFinalizingQuarantineAbsent(
        PackageStoreDeletionJournalStore.DiscoveredJournal discovered,
        PhysicalStoreDirectoryHandle rootHandle,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken)
    {
        var journal = discovered.Journal;
        if (journal.Phase != PackageStoreDeletionPhase.Finalizing)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "Only a Finalizing journal may describe an already absent quarantine root.", journal.StoreRoot);
        var ownedParents = new List<PhysicalStoreDirectoryHandle>(2);
        var edges = new List<QuarantinedParentEdge>(2);
        try
        {
            revalidateOperation(cancellationToken);
            RequireDirectory(_files.InspectHandle(rootHandle), journal.StoreRoot.HandleIdentity, journal.StoreRoot,
                "The held store root changed before checking a Finalizing quarantine absence.");
            var parent = OpenPackageParent(discovered, rootHandle, ownedParents, edges,
                out var parentIdentity, out var parentSemantics, cancellationToken);
            if (parentIdentity != journal.PackageParentIdentity || parentSemantics != journal.PackageParentSemantics ||
                _files.InspectChildNoFollow(parent, journal.QuarantineBasename) is not null ||
                _files.InspectChildNoFollow(parent, journal.OriginalBasename) is not null)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The Finalizing quarantine is absent only when both its reserved and original names are positively absent.",
                    journal.StoreRoot);

            foreach (var edge in edges)
            {
                RequireDirectory(_files.InspectHandle(edge.Parent), edge.ParentIdentity, journal.StoreRoot,
                    "A package-parent ancestor changed while confirming quarantine absence.");
                RequireDirectory(_files.InspectChildNoFollow(edge.Parent, edge.Name), edge.ChildIdentity, journal.StoreRoot,
                    "A package-parent ancestor name changed while confirming quarantine absence.");
                var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
                    edge.Parent, edge.Name, edge.ChildIdentity);
                if (canonical.ParentIdentity != edge.ParentIdentity || canonical.Semantics != edge.ParentSemantics ||
                    canonical.FileIdentity != edge.ChildIdentity ||
                    !string.Equals(canonical.Basename, edge.Name, StringComparison.Ordinal))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A package-parent ancestor is not at its exact canonical name during absence confirmation.",
                        journal.StoreRoot);
            }
            revalidateOperation(cancellationToken);
        }
        finally
        {
            DisposeAll(marker: null, quarantine: null, parents: ownedParents);
        }
    }

    internal PackageStoreQuarantinedCandidate BindQuarantinedCandidate(
        PackageStoreDeletionJournalStore.DiscoveredJournal discovered,
        PhysicalStoreDirectoryHandle rootHandle,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken,
        PhysicalStoreDirectoryHandle? heldPackageParent = null,
        PhysicalFileIdentity? heldPackageParentIdentity = null,
        PhysicalStoreNameSemantics? heldPackageParentSemantics = null,
        Action<CancellationToken>? revalidateHeldParent = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(revalidateOperation);
        var journal = discovered.Journal;
        if ((heldPackageParent is null) != (heldPackageParentIdentity is null) ||
            (heldPackageParent is null) != (heldPackageParentSemantics is null) ||
            (heldPackageParent is null) != (revalidateHeldParent is null))
            throw new ArgumentException("A supplied package-parent binding must include its identity, profile, and revalidation callback.");
        cancellationToken.ThrowIfCancellationRequested();
        revalidateOperation(cancellationToken);

        var ownedParents = new List<PhysicalStoreDirectoryHandle>(2);
        var edges = new List<QuarantinedParentEdge>(2);
        PhysicalStoreDirectoryHandle? packageParent = null;
        PhysicalStoreFileHandle? marker = null;
        PhysicalStoreDirectoryHandle? quarantine = null;
        PackageStoreQuarantinedCandidate? result = null;
        try
        {
            RequireDirectory(_files.InspectHandle(rootHandle), journal.StoreRoot.HandleIdentity, journal.StoreRoot,
                "The held store root changed before quarantine binding.");
            if (RequireSupportedProfile(rootHandle, journal.StoreRoot) != discovered.RootSemantics)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The held store root profile differs from the quarantine journal discovery binding.", journal.StoreRoot);

            PhysicalFileIdentity packageParentIdentity;
            PhysicalStoreNameSemantics packageParentSemantics;
            if (heldPackageParent is null)
            {
                packageParent = OpenPackageParent(discovered, rootHandle, ownedParents, edges,
                    out packageParentIdentity, out packageParentSemantics, cancellationToken);
            }
            else
            {
                packageParent = heldPackageParent;
                packageParentIdentity = heldPackageParentIdentity!;
                packageParentSemantics = heldPackageParentSemantics!;
                RequireDirectory(_files.InspectHandle(packageParent), packageParentIdentity, journal.StoreRoot,
                    "The retained package parent changed before quarantine rebinding.");
                if (RequireSupportedProfile(packageParent, journal.StoreRoot) != packageParentSemantics)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The retained package parent's native profile changed before quarantine rebinding.", journal.StoreRoot);
            }

            if (packageParentIdentity != journal.PackageParentIdentity ||
                packageParentSemantics != journal.PackageParentSemantics)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The current package parent differs from the journal's exact native parent binding.", journal.StoreRoot);

            var quarantineName = journal.QuarantineBasename;
            var quarantineInfo = _files.InspectChildNoFollow(packageParent, quarantineName)
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The exact journal quarantine directory is absent.", journal.StoreRoot);
            RequireDirectory(quarantineInfo, journal.Manifest.InstallDirectoryIdentity, journal.StoreRoot,
                "The journal quarantine name does not identify its exact original install directory.");
            PhysicalStoreDirectoryPublicationChecks.ObserveExactDirectory(
                _files, _directories, packageParent, quarantineName, quarantineInfo.Identity);

            quarantine = _files.OpenDirectoryChildNoFollow(packageParent, quarantineName);
            RequireDirectory(_files.InspectHandle(quarantine), quarantineInfo.Identity, journal.StoreRoot,
                "The opened quarantine directory differs from its exact no-follow observation.");
            var quarantineSemantics = RequireSupportedProfile(quarantine, journal.StoreRoot);
            if (quarantineSemantics != journal.Manifest.InstallDirectorySemantics)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The quarantine directory profile differs from the immutable deletion baseline.", journal.StoreRoot);

            var markerInfo = _files.InspectChildNoFollow(quarantine, PackageStoreDeletionJournal.CompletionMarkerName);
            if (markerInfo is not null)
            {
                RequireMarker(markerInfo, journal, quarantineSemantics);
                marker = _files.OpenFileChildNoFollow(quarantine,
                    PackageStoreDeletionJournal.CompletionMarkerName, FileAccess.Read);
                RequireMarker(_files.InspectHandle(marker), journal, quarantineSemantics);
                RequireCanonicalMarker(quarantine, journal, quarantineSemantics);
            }

            var observation = _manifests.CapturePartial(quarantine, journal.OriginalInstall,
                packageParent, packageParentIdentity, packageParentSemantics,
                quarantineName, quarantineSemantics, journal.Manifest, cancellationToken);
            RequirePhaseObservation(journal, observation);

            result = new PackageStoreQuarantinedCandidate(
                _files, _names, _directories, _manifests, journal, rootHandle,
                packageParent, packageParentIdentity, packageParentSemantics,
                quarantine, quarantineSemantics, marker, edges.ToArray(), ownedParents.ToArray(),
                revalidateOperation, heldPackageParent is null ? null : revalidateHeldParent);
            result.Revalidate(cancellationToken);

            ownedParents.Clear();
            edges.Clear();
            quarantine = null;
            marker = null;
            return result;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                if (result is not null)
                    result.Dispose();
                else
                {
                    DisposeAll(marker, quarantine, ownedParents);
                }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Quarantine candidate binding failed and native observations also failed to close.",
                    primaryFailure, cleanupFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new InvalidOperationException("Unreachable after rethrowing the candidate-binding failure.");
        }
    }

    internal static void RequirePhaseObservation(
        PackageStoreDeletionJournal journal,
        PhysicalStoreDeletionPartialObservation observation)
    {
        var rows = observation.Manifest.Entries;
        var marker = rows.SingleOrDefault(static entry => entry.RelativeComponents.Length == 1 &&
            string.Equals(entry.RelativeComponents[0], PackageStoreDeletionJournal.CompletionMarkerName,
                StringComparison.Ordinal));
        var markerOnly = marker is not null && rows.Length == 1;
        var empty = rows.IsEmpty;

        var accepted = journal.Phase switch
        {
            PackageStoreDeletionPhase.Prepared or PackageStoreDeletionPhase.Quarantined
                => observation.PositivelyAbsentEntries.IsEmpty && marker is not null,
            PackageStoreDeletionPhase.Deleting => marker is not null,
            PackageStoreDeletionPhase.Finalizing => markerOnly || empty,
            _ => false
        };
        if (!accepted)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The native quarantine subset does not match the journal's allowed phase evidence.", journal.StoreRoot);
    }

    private PhysicalStoreDirectoryHandle OpenPackageParent(
        PackageStoreDeletionJournalStore.DiscoveredJournal discovered,
        PhysicalStoreDirectoryHandle rootHandle,
        List<PhysicalStoreDirectoryHandle> ownedParents,
        List<QuarantinedParentEdge> edges,
        out PhysicalFileIdentity packageParentIdentity,
        out PhysicalStoreNameSemantics packageParentSemantics,
        CancellationToken cancellationToken)
    {
        var journal = discovered.Journal;
        var rootInfo = _files.InspectHandle(rootHandle);
        RequireDirectory(rootInfo, journal.StoreRoot.HandleIdentity, journal.StoreRoot,
            "The held store root changed before package-parent opening.");
        var rootSemantics = RequireSupportedProfile(rootHandle, journal.StoreRoot);
        if (rootSemantics != discovered.RootSemantics)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The held store root profile differs from the journal discovery binding.", journal.StoreRoot);

        var feed = OpenExactDirectoryChild(rootHandle, journal.PackageParentRelativeComponents[0],
            rootInfo.Identity, rootSemantics, journal.StoreRoot, cancellationToken);
        ownedParents.Add(feed.Handle);
        edges.Add(feed.Edge);

        var package = OpenExactDirectoryChild(feed.Handle, journal.PackageParentRelativeComponents[1],
            feed.Identity, feed.Semantics, journal.StoreRoot, cancellationToken);
        ownedParents.Add(package.Handle);
        edges.Add(package.Edge);
        packageParentIdentity = package.Identity;
        packageParentSemantics = package.Semantics;
        return package.Handle;
    }

    private (PhysicalStoreDirectoryHandle Handle, PhysicalFileIdentity Identity,
        PhysicalStoreNameSemantics Semantics, QuarantinedParentEdge Edge) OpenExactDirectoryChild(
        PhysicalStoreDirectoryHandle parent,
        string name,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics parentSemantics,
        PhysicalRootIdentity root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhysicalStoreDeletionManifestEntry.ValidateComponent(name, parentSemantics);
        var info = _files.InspectChildNoFollow(parent, name)
            ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "A journal-bound package-parent path component is absent.", root);
        RequireDirectory(info, info.Identity, root,
            "A journal-bound package-parent component is not a safe directory.");
        var canonical = PhysicalStoreDirectoryPublicationChecks.ObserveExactDirectory(
            _files, _directories, parent, name, info.Identity);
        if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != parentSemantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "A journal-bound package-parent component is not at its exact canonical native name.", root);
        PhysicalStoreDeletionChecks.RequireSameVolume(parentIdentity, info.Identity);

        var child = _files.OpenDirectoryChildNoFollow(parent, name);
        try
        {
            RequireDirectory(_files.InspectHandle(child), info.Identity, root,
                "A package-parent directory changed during open.");
            var childSemantics = RequireSupportedProfile(child, root);
            RequireDirectory(_files.InspectHandle(parent), parentIdentity, root,
                "A retained package parent changed during open.");
            var namedAfter = _files.InspectChildNoFollow(parent, name);
            RequireDirectory(namedAfter, info.Identity, root,
                "A package-parent name changed during open.");
            var afterCanonical = PhysicalStoreDirectoryPublicationChecks.ObserveExactDirectory(
                _files, _directories, parent, name, info.Identity);
            if (afterCanonical.ParentIdentity != parentIdentity || afterCanonical.Semantics != parentSemantics ||
                !string.Equals(afterCanonical.Basename, name, StringComparison.Ordinal))
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "A package-parent name changed during canonical revalidation.", root);

            return (child, info.Identity, childSemantics,
                new QuarantinedParentEdge(parent, parentIdentity, parentSemantics,
                    name, child, info.Identity, childSemantics));
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private PhysicalStoreNameSemantics RequireSupportedProfile(
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity root)
        => PackageInstallStore.RequireSupportedNameProfile(_names.ObserveDirectoryNameSemantics(directory), root);

    private static void RequireDirectory(
        PhysicalStoreEntryInfo? info,
        PhysicalFileIdentity expectedIdentity,
        PhysicalRootIdentity root,
        string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != expectedIdentity ||
            info.ReparseTag is not (null or 0))
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority, message, root);
        PhysicalStoreDeletionChecks.RequireSameVolume(root.HandleIdentity, info.Identity);
    }

    private static void RequireMarker(
        PhysicalStoreEntryInfo? info,
        PackageStoreDeletionJournal journal,
        PhysicalStoreNameSemantics semantics)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
            info.Length != 0 || info.Identity != journal.CompletionMarkerIdentity ||
            info.ReparseTag is not (null or 0) ||
            !string.Equals(info.Identity.Provider, journal.StoreRoot.HandleIdentity.Provider, StringComparison.Ordinal) ||
            !string.Equals(info.Identity.VolumeOrDeviceId, journal.StoreRoot.HandleIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The quarantine root completion marker differs from its exact single-link empty baseline.", journal.StoreRoot);
        PhysicalStoreDeletionManifestEntry.ValidateComponent(PackageStoreDeletionJournal.CompletionMarkerName, semantics);
    }

    private void RequireCanonicalMarker(
        PhysicalStoreDirectoryHandle quarantine,
        PackageStoreDeletionJournal journal,
        PhysicalStoreNameSemantics semantics)
    {
        var canonical = _names.ObserveCanonicalFileNameNoFollow(quarantine,
            PackageStoreDeletionJournal.CompletionMarkerName, journal.CompletionMarkerIdentity);
        if (canonical.ParentIdentity != journal.Manifest.InstallDirectoryIdentity ||
            canonical.Semantics != semantics || canonical.FileIdentity != journal.CompletionMarkerIdentity ||
            !string.Equals(canonical.Basename, PackageStoreDeletionJournal.CompletionMarkerName, StringComparison.Ordinal))
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The quarantine completion marker is not at its exact canonical native name.", journal.StoreRoot);
    }

    private static void RequireJournalLocation(
        PackageStoreDeletionJournal journal,
        PackageInstallIdentityReader.PackageInstallDeletionLocation location,
        bool originalName)
    {
        var expectedName = originalName ? journal.OriginalBasename : journal.QuarantineBasename;
        if (location.RootIdentity != journal.StoreRoot.HandleIdentity ||
            location.PackageParentIdentity != journal.PackageParentIdentity ||
            location.PackageParentSemantics != journal.PackageParentSemantics ||
            !string.Equals(location.OriginalBasename, expectedName, StringComparison.Ordinal) ||
            location.InstallDirectoryIdentity != journal.Manifest.InstallDirectoryIdentity ||
            location.CompletionMarkerIdentity != journal.CompletionMarkerIdentity ||
            location.InstallIdentity != journal.OriginalInstall)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The native install location differs from its exact journal-bound parent/name/marker.", journal.StoreRoot);
    }

    private static void DisposeAll(
        PhysicalStoreFileHandle? marker,
        PhysicalStoreDirectoryHandle? quarantine,
        IEnumerable<PhysicalStoreDirectoryHandle> parents)
    {
        List<Exception>? failures = null;
        TryDispose(marker);
        TryDispose(quarantine);
        foreach (var parent in parents.Reverse())
            TryDispose(parent);
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Multiple native quarantine observations failed to close.", failures);

        void TryDispose(IDisposable? resource)
        {
            if (resource is null)
                return;
            try { resource.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity root)
        => new(reason, message, root);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);
}

internal sealed record QuarantinedParentEdge(
    PhysicalStoreDirectoryHandle Parent,
    PhysicalFileIdentity ParentIdentity,
    PhysicalStoreNameSemantics ParentSemantics,
    string Name,
    PhysicalStoreDirectoryHandle Child,
    PhysicalFileIdentity ChildIdentity,
    PhysicalStoreNameSemantics ChildSemantics);

/// <summary>Call-scoped rebind of one journal's exact full baseline at its quarantine basename.</summary>
/// <remarks>It is native observation evidence only; it has no move, removal, or journal-publication method.</remarks>
internal sealed class PackageStoreQuarantinedCandidate : IDisposable
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly PhysicalStoreDeletionManifestReader _manifests;
    private readonly PhysicalStoreDirectoryHandle _root;
    private readonly PhysicalStoreDirectoryHandle _packageParent;
    private readonly PhysicalFileIdentity _packageParentIdentity;
    private readonly PhysicalStoreNameSemantics _packageParentSemantics;
    private PhysicalStoreDirectoryHandle? _quarantine;
    private readonly PhysicalStoreNameSemantics _quarantineSemantics;
    private PhysicalStoreFileHandle? _marker;
    private readonly QuarantinedParentEdge[] _edges;
    private readonly PhysicalStoreDirectoryHandle[] _ownedParents;
    private readonly Action<CancellationToken> _revalidateOperation;
    private readonly Action<CancellationToken>? _revalidateHeldParent;
    private PhysicalStoreDeletionPartialObservation? _lastObservation;
    private PackageStoreDeletionPhase? _lastObservationPhase;
    private bool _quarantineRootRemovalAttempted;
    private bool _quarantineRootRemovalReturned;
    private bool _disposed;

    internal PackageStoreQuarantinedCandidate(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryNameFileSystem directories,
        PhysicalStoreDeletionManifestReader manifests,
        PackageStoreDeletionJournal journal,
        PhysicalStoreDirectoryHandle root,
        PhysicalStoreDirectoryHandle packageParent,
        PhysicalFileIdentity packageParentIdentity,
        PhysicalStoreNameSemantics packageParentSemantics,
        PhysicalStoreDirectoryHandle quarantine,
        PhysicalStoreNameSemantics quarantineSemantics,
        PhysicalStoreFileHandle? marker,
        QuarantinedParentEdge[] edges,
        PhysicalStoreDirectoryHandle[] ownedParents,
        Action<CancellationToken> revalidateOperation,
        Action<CancellationToken>? revalidateHeldParent)
    {
        _files = files;
        _names = names;
        _directories = directories;
        _manifests = manifests;
        Journal = journal;
        _root = root;
        _packageParent = packageParent;
        _packageParentIdentity = packageParentIdentity;
        _packageParentSemantics = packageParentSemantics;
        _quarantine = quarantine;
        _quarantineSemantics = quarantineSemantics;
        _marker = marker;
        _edges = edges;
        _ownedParents = ownedParents;
        _revalidateOperation = revalidateOperation;
        _revalidateHeldParent = revalidateHeldParent;
    }

    internal PackageStoreDeletionJournal Journal { get; private set; }

    internal PhysicalStoreDeletionManifest Manifest => Journal.Manifest;

    internal PhysicalStoreDeletionPartialObservation? LastObservation => _lastObservation;

    internal PackageStoreDeletionPhase? LastObservationPhase => _lastObservationPhase;

    internal bool QuarantineRootRemovalAttempted => _quarantineRootRemovalAttempted;

    internal bool QuarantineRootRemovalReturned => _quarantineRootRemovalReturned;

    internal PhysicalStoreDirectoryHandle PackageParentHandle
    {
        get
        {
            Revalidate(CancellationToken.None);
            return _packageParent;
        }
    }

    internal PhysicalStoreDirectoryHandle QuarantineHandle
    {
        get
        {
            Revalidate(CancellationToken.None);
            return _quarantine ?? throw Refuse("The quarantine root handle has already been closed.");
        }
    }

    internal void Revalidate(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_revalidateHeldParent is not null)
            _revalidateHeldParent(cancellationToken);
        else
            foreach (var edge in _edges)
                RevalidateEdge(edge);

        RequireDirectory(_files.InspectHandle(_root), Journal.StoreRoot.HandleIdentity,
            "The held store root changed while validating a quarantine candidate.");
        RequireDirectory(_files.InspectHandle(_packageParent), _packageParentIdentity,
            "The exact package parent changed while validating a quarantine candidate.");
        if (_names.ObserveDirectoryNameSemantics(_packageParent) != _packageParentSemantics)
            throw Refuse("The exact package parent's native profile changed.");
        RequireExactDirectoryName(_packageParent, Journal.QuarantineBasename,
            Journal.Manifest.InstallDirectoryIdentity, _packageParentIdentity, _packageParentSemantics);
        if (_quarantine is { } quarantine)
        {
            RequireDirectory(_files.InspectHandle(quarantine), Journal.Manifest.InstallDirectoryIdentity,
                "The held quarantine directory changed.");
            if (_names.ObserveDirectoryNameSemantics(quarantine) != _quarantineSemantics ||
                _quarantineSemantics != Journal.Manifest.InstallDirectorySemantics)
                throw Refuse("The quarantine directory's exact native name profile changed.");
            RequireExactDirectoryName(_packageParent, Journal.QuarantineBasename,
                Journal.Manifest.InstallDirectoryIdentity, _packageParentIdentity, _packageParentSemantics);

            var markerInfo = _files.InspectChildNoFollow(quarantine, PackageStoreDeletionJournal.CompletionMarkerName);
            if (markerInfo is not null)
            {
                RequireMarker(markerInfo);
                var canonicalMarker = _names.ObserveCanonicalFileNameNoFollow(quarantine,
                    PackageStoreDeletionJournal.CompletionMarkerName, Journal.CompletionMarkerIdentity);
                if (canonicalMarker.ParentIdentity != Journal.Manifest.InstallDirectoryIdentity ||
                    canonicalMarker.Semantics != _quarantineSemantics ||
                    canonicalMarker.FileIdentity != Journal.CompletionMarkerIdentity ||
                    !string.Equals(canonicalMarker.Basename, PackageStoreDeletionJournal.CompletionMarkerName,
                        StringComparison.Ordinal))
                    throw Refuse("The quarantine completion marker is not at its exact canonical native name.");
            }
            if (_marker is { } marker)
            {
                RequireMarker(_files.InspectHandle(marker));
                if (markerInfo is null || markerInfo.Identity != Journal.CompletionMarkerIdentity)
                    throw Refuse("The retained completion-marker handle is no longer bound to the exact marker name.");
            }

            var observed = _manifests.CapturePartial(quarantine, Journal.OriginalInstall,
                _packageParent, _packageParentIdentity, _packageParentSemantics,
                Journal.QuarantineBasename, _quarantineSemantics, Journal.Manifest, cancellationToken);
            PackageStoreDeletionCandidateBinder.RequirePhaseObservation(Journal, observed);
        }
        else
        {
            RevalidatePackageParent(cancellationToken);
        }
        EnsureLive(cancellationToken);
    }

    internal PhysicalStoreDeletionPartialObservation ObserveCurrentSubset(CancellationToken cancellationToken)
    {
        Revalidate(cancellationToken);
        var quarantine = _quarantine ?? throw Refuse("The quarantine root handle has already been closed.");
        var journal = Journal;
        var observation = _manifests.CapturePartial(quarantine, journal.OriginalInstall,
            _packageParent, _packageParentIdentity, _packageParentSemantics,
            journal.QuarantineBasename, _quarantineSemantics, journal.Manifest, cancellationToken);
        PackageStoreDeletionCandidateBinder.RequirePhaseObservation(journal, observation);
        _lastObservation = observation;
        _lastObservationPhase = journal.Phase;
        return observation;
    }

    internal void MarkQuarantineRootRemovalAttempted()
    {
        if (_quarantineRootRemovalAttempted)
            throw new InvalidOperationException("The exact quarantine root may be removed only once per candidate execution.");
        _quarantineRootRemovalAttempted = true;
    }

    internal void MarkQuarantineRootRemovalReturned()
    {
        if (!_quarantineRootRemovalAttempted || _quarantineRootRemovalReturned)
            throw new InvalidOperationException("A quarantine-root removal receipt requires one active exact removal attempt.");
        _quarantineRootRemovalReturned = true;
    }

    internal void ReleaseMarkerObservation(CancellationToken cancellationToken)
    {
        Revalidate(cancellationToken);
        _marker?.Dispose();
        _marker = null;
    }

    internal void AdvanceJournal(PackageStoreDeletionJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Revalidate(cancellationToken);
        if (journal.CandidateId != Journal.CandidateId || journal.OperationId != Journal.OperationId ||
            journal.OriginalInstall != Journal.OriginalInstall || journal.Manifest.RequireExactSubset(Journal.Manifest) != 0 ||
            Journal.Manifest.RequireExactSubset(journal.Manifest) != 0 ||
            (Journal.Phase, journal.Phase) is not
                (PackageStoreDeletionPhase.Prepared, PackageStoreDeletionPhase.Quarantined) and not
                (PackageStoreDeletionPhase.Quarantined, PackageStoreDeletionPhase.Deleting) and not
                (PackageStoreDeletionPhase.Deleting, PackageStoreDeletionPhase.Finalizing))
            throw Refuse("A replacement deletion phase does not preserve this exact candidate and immutable baseline.");
        Journal = journal;
        Revalidate(cancellationToken);
    }

    internal void CloseQuarantineForRootRemoval(CancellationToken cancellationToken)
    {
        if (Journal.Phase != PackageStoreDeletionPhase.Finalizing)
            throw Refuse("Only a Finalizing candidate can close its quarantine root for removal.");
        var observation = ObserveCurrentSubset(cancellationToken);
        if (!observation.Manifest.Entries.IsEmpty)
            throw Refuse("The Finalizing quarantine root is not positively empty before root removal.");
        ReleaseMarkerObservation(cancellationToken);
        var quarantine = _quarantine ?? throw Refuse("The quarantine root handle has already been closed.");
        quarantine.Dispose();
        _quarantine = null;
        RevalidatePackageParent(cancellationToken);
        RequireDirectory(_files.InspectChildNoFollow(_packageParent, Journal.QuarantineBasename),
            Journal.Manifest.InstallDirectoryIdentity,
            "The exact quarantine root changed after its handle was closed.");
    }

    internal void VerifyQuarantineRemoved(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_quarantine is not null)
            throw Refuse("The quarantine root handle must be closed before verifying its removal.");
        RevalidatePackageParent(cancellationToken);
        if (_files.InspectChildNoFollow(_packageParent, Journal.QuarantineBasename) is not null)
            throw Refuse("The exact quarantine root is still present after native removal.");
        if (_files.InspectChildNoFollow(_packageParent, Journal.OriginalBasename) is not null)
            throw Refuse("The original logical install slot became occupied during quarantine removal.");
        EnsureLive(cancellationToken);
    }

    /// <summary>Checks the independent original-name slot without following it or granting replacement authority.</summary>
    internal PhysicalStoreEntryInfo? InspectOriginalSlot(CancellationToken cancellationToken)
    {
        Revalidate(cancellationToken);
        var info = _files.InspectChildNoFollow(_packageParent, Journal.OriginalBasename);
        if (info is null)
            return null;
        RequireDirectory(info, info.Identity,
            "The original logical install slot is occupied by an unsafe native entry.");
        var canonical = PhysicalStoreDirectoryPublicationChecks.ObserveExactDirectory(
            _files, _directories, _packageParent, Journal.OriginalBasename, info.Identity);
        if (canonical.ParentIdentity != _packageParentIdentity || canonical.Semantics != _packageParentSemantics ||
            !string.Equals(canonical.Basename, Journal.OriginalBasename, StringComparison.Ordinal))
            throw Refuse("The original logical install slot is not the exact canonical name expected by the journal.");
        EnsureLive(cancellationToken);
        return info;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception>? failures = null;
        if (_marker is not null)
            TryDispose(_marker);
        if (_quarantine is not null)
            TryDispose(_quarantine);
        foreach (var parent in _ownedParents.Reverse())
            TryDispose(parent);
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Multiple native quarantine observations failed to close.", failures);

        void TryDispose(IDisposable handle)
        {
            try { handle.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }

    private void RevalidateEdge(QuarantinedParentEdge edge)
    {
        RequireDirectory(_files.InspectHandle(edge.Parent), edge.ParentIdentity,
            "A retained quarantine ancestor changed.");
        if (_names.ObserveDirectoryNameSemantics(edge.Parent) != edge.ParentSemantics)
            throw Refuse("A retained quarantine ancestor profile changed.");
        RequireDirectory(_files.InspectChildNoFollow(edge.Parent, edge.Name), edge.ChildIdentity,
            "A retained quarantine ancestor name changed.");
        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
            edge.Parent, edge.Name, edge.ChildIdentity);
        if (canonical.ParentIdentity != edge.ParentIdentity || canonical.Semantics != edge.ParentSemantics ||
            canonical.FileIdentity != edge.ChildIdentity || !string.Equals(canonical.Basename, edge.Name, StringComparison.Ordinal))
            throw Refuse("A retained quarantine ancestor is not at its exact canonical name.");
        RequireDirectory(_files.InspectHandle(edge.Child), edge.ChildIdentity,
            "A retained quarantine ancestor handle changed.");
        if (_names.ObserveDirectoryNameSemantics(edge.Child) != edge.ChildSemantics)
            throw Refuse("A retained quarantine ancestor child profile changed.");
    }

    private void RevalidatePackageParent(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_revalidateHeldParent is not null)
            _revalidateHeldParent(cancellationToken);
        else
            foreach (var edge in _edges)
                RevalidateEdge(edge);
        RequireDirectory(_files.InspectHandle(_root), Journal.StoreRoot.HandleIdentity,
            "The held store root changed while validating the quarantine package parent.");
        RequireDirectory(_files.InspectHandle(_packageParent), _packageParentIdentity,
            "The exact package parent changed while validating the quarantine package parent.");
        if (_names.ObserveDirectoryNameSemantics(_packageParent) != _packageParentSemantics)
            throw Refuse("The exact package parent's native profile changed.");
        EnsureLive(cancellationToken);
    }

    private void RequireExactDirectoryName(
        PhysicalStoreDirectoryHandle parent,
        string name,
        PhysicalFileIdentity identity,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics)
    {
        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(parent, name, identity);
        if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != semantics ||
            canonical.FileIdentity != identity || !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refuse("The quarantine directory is not at its exact canonical native name.");
    }

    private void RequireMarker(PhysicalStoreEntryInfo? info)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
            info.Length != 0 || info.Identity != Journal.CompletionMarkerIdentity ||
            info.ReparseTag is not (null or 0) ||
            !string.Equals(info.Identity.Provider, Journal.StoreRoot.HandleIdentity.Provider, StringComparison.Ordinal) ||
            !string.Equals(info.Identity.VolumeOrDeviceId, Journal.StoreRoot.HandleIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse("The quarantine root completion marker differs from its exact single-link empty baseline.");
    }

    private void RequireDirectory(PhysicalStoreEntryInfo? info, PhysicalFileIdentity identity, string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != identity ||
            info.ReparseTag is not (null or 0) ||
            !string.Equals(info.Identity.Provider, Journal.StoreRoot.HandleIdentity.Provider, StringComparison.Ordinal) ||
            !string.Equals(info.Identity.VolumeOrDeviceId, Journal.StoreRoot.HandleIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse(message);
    }

    private void EnsureLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                "The quarantine candidate observation has expired.", Journal.StoreRoot);
        _revalidateOperation(cancellationToken);
    }

    private PackageStoreAdmissionException Refuse(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, Journal.StoreRoot);
}

/// <summary>Call-scoped exact binding for one freshly planned eligible completed install.</summary>
/// <remarks>Its manifest is descriptive; no property or method performs a namespace mutation.</remarks>
internal sealed class PackageStoreDeletionCandidate : IDisposable
{
    private readonly PackageInstallIdentityReader.PackageInstallIdentityObservation _observation;
    private readonly PackageInstallIdentityReader.PackageInstallDeletionLocation _location;
    private readonly PhysicalStoreDeletionManifestReader _manifests;
    private readonly Action<CancellationToken> _revalidateOperation;
    private bool _preparedForMove;
    private bool _disposed;

    internal PackageStoreDeletionCandidate(
        PackageStoreRetentionPlanEntry planEntry,
        PackageInstallIdentity installIdentity,
        PhysicalStoreDeletionManifest manifest,
        PackageInstallIdentityReader.PackageInstallIdentityObservation observation,
        PackageInstallIdentityReader.PackageInstallDeletionLocation location,
        PhysicalStoreDeletionManifestReader manifests,
        Action<CancellationToken> revalidateOperation)
    {
        PlanEntry = planEntry;
        InstallIdentity = installIdentity;
        Manifest = manifest;
        _observation = observation;
        _location = location;
        _manifests = manifests;
        _revalidateOperation = revalidateOperation;
    }

    internal PackageStoreRetentionPlanEntry PlanEntry { get; }

    internal PackageInstallIdentity InstallIdentity { get; }

    internal PhysicalStoreDeletionManifest Manifest { get; }

    internal PackageStoreDeletionJournal CreatePreparedJournal(
        PhysicalRootIdentity storeRoot,
        long enrollmentEpoch,
        Guid operationId,
        Guid candidateId,
        int keepNewestInactiveVersionsPerPackage,
        CancellationToken cancellationToken)
    {
        RequireUnprepared(cancellationToken);
        RequireCurrentBaseline(cancellationToken);
        var pathComponents = PackageInstallIdentityReader.ValidateRelativeInstallPath(
            InstallIdentity.RootRelativeInstallPath, InstallIdentity.PackageId, InstallIdentity.Version);
        return new PackageStoreDeletionJournal(
            PackageStoreDeletionJournal.CurrentSchemaVersion,
            storeRoot,
            enrollmentEpoch,
            operationId,
            candidateId,
            InstallIdentity,
            pathComponents.Take(2),
            _location.PackageParentIdentity,
            _location.PackageParentSemantics,
            _location.OriginalBasename,
            PackageStoreDeletionJournal.QuarantineBasenameFor(candidateId),
            _location.CompletionMarkerIdentity,
            keepNewestInactiveVersionsPerPackage,
            PackageStoreDeletionPhase.Prepared,
            Manifest);
    }

    internal void RequireCurrentBaseline(CancellationToken cancellationToken)
    {
        RequireUnprepared(cancellationToken);
        Revalidate(cancellationToken);
        var current = _manifests.Capture(
            _observation.InstallDirectory,
            InstallIdentity,
            _location.PackageParentHandle,
            _location.PackageParentIdentity,
            _location.PackageParentSemantics,
            _location.OriginalBasename,
            _location.InstallDirectorySemantics,
            cancellationToken);
        if (Manifest.RequireExactSubset(current) != 0)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The current complete install manifest differs from the bound Prepared baseline.", InstallIdentity.Root);
        Revalidate(cancellationToken);
    }

    internal void RequireJournalScopeLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw Expired("The deletion candidate has been disposed.");
        _revalidateOperation(cancellationToken);
    }

    internal void RequireUnprepared(CancellationToken cancellationToken)
    {
        RequireJournalScopeLive(cancellationToken);
        if (_preparedForMove)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "Prepared publication requires the candidate's open, exact install observation.", InstallIdentity.Root);
    }

    internal void Revalidate(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_preparedForMove)
            _observation.RevalidateDeletionParent();
        else if (_observation.ObserveCanonicalDeletionLocation() != _location)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The deletion candidate's canonical install location changed.", InstallIdentity.Root);
    }

    /// <summary>Closes the install leaf and marker, retaining a revalidatable exact package-parent binding.</summary>
    /// <remarks>The returned binding does not authorize a move; a future executor must gate it on durable Prepared evidence.</remarks>
    internal PackageStoreDeletionParentBinding PrepareForMove(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_preparedForMove)
            throw Expired("The candidate leaf was already closed for its parent binding.");

        var current = _observation.PrepareForMove(_location);
        if (current != _location)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The candidate's canonical parent binding changed before leaf closure.", InstallIdentity.Root);
        _preparedForMove = true;
        return new PackageStoreDeletionParentBinding(_observation, _location, EnsureLive);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _observation.Dispose();
    }

    private void EnsureLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw Expired("The deletion candidate has been disposed.");
        _revalidateOperation(cancellationToken);
    }

    private PackageStoreAdmissionException Expired(string message)
        => new(PackageStoreAdmissionReason.ExpiredScope, message, InstallIdentity.Root);
}

/// <summary>Revalidatable held root/feed/package ancestry after the install leaf has closed.</summary>
/// <remarks>This is a native observation only; it has no publication, rename or removal method.</remarks>
internal sealed class PackageStoreDeletionParentBinding
{
    private readonly PackageInstallIdentityReader.PackageInstallIdentityObservation _observation;
    private readonly PackageInstallIdentityReader.PackageInstallDeletionLocation _location;
    private readonly Action<CancellationToken> _ensureCandidateLive;

    internal PackageStoreDeletionParentBinding(
        PackageInstallIdentityReader.PackageInstallIdentityObservation observation,
        PackageInstallIdentityReader.PackageInstallDeletionLocation location,
        Action<CancellationToken> ensureCandidateLive)
    {
        _observation = observation;
        _location = location;
        _ensureCandidateLive = ensureCandidateLive;
    }

    internal PhysicalStoreDirectoryHandle PackageParentHandle
    {
        get
        {
            Revalidate(CancellationToken.None);
            return _location.PackageParentHandle;
        }
    }

    internal PhysicalFileIdentity PackageParentIdentity => _location.PackageParentIdentity;

    internal PhysicalStoreNameSemantics PackageParentSemantics => _location.PackageParentSemantics;

    internal string OriginalBasename => _location.OriginalBasename;

    internal PhysicalFileIdentity InstallDirectoryIdentity => _location.InstallDirectoryIdentity;

    internal PhysicalStoreNameSemantics InstallDirectorySemantics => _location.InstallDirectorySemantics;

    internal PhysicalFileIdentity CompletionMarkerIdentity => _location.CompletionMarkerIdentity;

    internal PackageInstallIdentity InstallIdentity => _location.InstallIdentity;

    internal void Revalidate(CancellationToken cancellationToken)
    {
        _ensureCandidateLive(cancellationToken);
        _observation.RevalidateDeletionParent();
    }
}
