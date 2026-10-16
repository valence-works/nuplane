using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using System.Runtime.ExceptionServices;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Maintenance;

/// <summary>Builds descriptive, non-quiescent state observations from frozen trusted member declarations.</summary>
internal sealed class PackageStoreEnrollmentPreviewService : IPackageStoreEnrollmentPreviewService
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly RootMembershipRegistry? _registry;
    private readonly ITrustedPackageStoreRootCatalog _rootCatalog;
    private readonly IPackageStoreRootResolver _rootResolver;
    private readonly ITrustedPackageStoreMemberDeclarationCatalog _declarations;
    private readonly IStoreStateSerializer _serializer;

    internal PackageStoreEnrollmentPreviewService(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry? registry,
        ITrustedPackageStoreRootCatalog rootCatalog,
        IPackageStoreRootResolver rootResolver,
        ITrustedPackageStoreMemberDeclarationCatalog declarations,
        IStoreStateSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(rootCatalog);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(serializer);
        _files = files;
        _registry = registry;
        _rootCatalog = rootCatalog;
        _rootResolver = rootResolver;
        _declarations = declarations;
        _serializer = serializer;
    }

    public async Task<PackageStoreEnrollmentPreviewSnapshot> PreviewRootAsync(
        string? rootLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var label = rootLabel ?? _rootResolver.ConfiguredDefaultLabel ?? "default";
        if (!_rootResolver.TryResolve(label, out var configuredRoot) || configuredRoot is null)
            return Snapshot(label, null, null, null, [], "UnknownRootLabel");
        if (!_declarations.IsConfigured)
            return Snapshot(label, null, null, null, [], "TrustedMemberDeclarationsMissing");
        if (!_declarations.IsComplete)
            return Snapshot(label, null, null, null, [], "TrustedMemberDeclarationsNotAssertedComplete");
        if (_registry is null || !_registry.UsesFileSystem(_files))
            return Snapshot(label, null, null, null, [], "NativeStoreObservationUnavailable");

        var selectedDeclarations = _declarations.ForRoot(label);
        if (selectedDeclarations.Count == 0)
            return Snapshot(label, null, null, null, [], "TrustedMemberDeclarationsMissingForRoot");
        var labelDiagnostic = ValidateDeclarationLabels();
        if (labelDiagnostic is not null)
            return Snapshot(label, null, null, null, [], labelDiagnostic);

        var authority = new PackageStoreAuthorityResolver(_files, _registry);
        ResolvedPackageStorePath? rootResolution = null;
        Exception? operationFailure = null;
        try
        {
            // This metadata-only catalog candidate deliberately retains exact Incomplete/pending structure;
            // ordinary path resolution rejects those states before a descriptive preview can explain them.
            rootResolution = authority.ResolveTrustedCatalogRoot(configuredRoot.RootPath);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = rootResolution.MembershipCandidate;
            if (candidate is null)
            {
                rootResolution.Revalidate();
                // An unenrolled root has no authority identity. Report only the held directory's
                // observed identity for an explicit future cutover confirmation, never admission.
                var observedRoot = !rootResolution.IsProspectiveMissingSuffix &&
                                   rootResolution.Target is PhysicalStoreDirectoryHandle directory
                    ? new PhysicalRootIdentity(_files.InspectHandle(directory).Identity)
                    : null;
                var preview = await PreviewUnenrolledAsync(authority, configuredRoot, label, selectedDeclarations,
                    observedRoot, cancellationToken).ConfigureAwait(false);
                rootResolution.Revalidate();
                return preview;
            }

            if (candidate.Status == RootMembershipStatus.Complete && candidate.PendingStateCommit is null &&
                candidate.PendingGroupPublicationV2 is null && candidate.PendingMembershipMigration is null)
                return await PreviewCompleteAsync(authority, configuredRoot, label, selectedDeclarations,
                    candidate, cancellationToken).ConfigureAwait(false);

            rootResolution.Revalidate();
            return await PreviewIncompleteAsync(authority, configuredRoot, label, selectedDeclarations,
                candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            operationFailure = exception;
            throw;
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            operationFailure = exception;
            return Snapshot(label, rootResolution?.RootIdentity, null, null, [],
                "RootMetadataUnavailable: " + exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            DisposePathPreservingPrimary(rootResolution, operationFailure);
        }
    }

    private async Task<PackageStoreEnrollmentPreviewSnapshot> PreviewUnenrolledAsync(
        PackageStoreAuthorityResolver authority,
        TrustedPackageStoreRoot root,
        string label,
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        PhysicalRootIdentity? rootIdentity,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveDeclarations(authority, declarations, cancellationToken);
        Exception? operationFailure = null;
        try
        {
            if (resolved.Diagnostic is not null)
                return Snapshot(label, rootIdentity, PackageStoreEnrollmentPreviewMembership.Unenrolled, null, [],
                    resolved.Diagnostic);
            var members = new List<PackageStoreEnrollmentPreviewMember>(declarations.Count);
            for (var index = 0; index < declarations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declaration = declarations[index];
                var referent = resolved.Referents[index];
                var observation = await ObserveAsync(referent, expectedIdentity: null, expectedBodyDigest: null,
                    cancellationToken).ConfigureAwait(false);
                var state = observation.State;
                var closures = DescribeClosures(state, observation.Status, rootIdentity);
                members.Add(new PackageStoreEnrollmentPreviewMember(declaration.MemberId,
                    declaration.StateFileLocator, "Unenrolled", Map(observation.Status), DateTimeOffset.UtcNow,
                    observation.Slot, observation.StateFileIdentity, observation.RawPayloadDigest,
                    observation.StateBodyDigest, state, closures.Active, closures.Recoverable,
                    PackageStoreEnrollmentPreviewMigration.EnrollmentRequired,
                    ["No membership ledger was observed; this is an independent non-quiescent observation."],
                    GetRetiredGraphEvidence(state, rootIdentity)));
            }
            return new PackageStoreEnrollmentPreviewSnapshot(label, rootIdentity,
                PackageStoreEnrollmentPreviewMembership.Unenrolled, null, true, members);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            DisposeReferents(resolved.Referents, operationFailure);
        }
    }

    private async Task<PackageStoreEnrollmentPreviewSnapshot> PreviewIncompleteAsync(
        PackageStoreAuthorityResolver authority,
        TrustedPackageStoreRoot root,
        string label,
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        RootMembershipRecord candidate,
        CancellationToken cancellationToken)
    {
        var mismatch = CompareDeclarations(declarations, candidate);
        if (mismatch is not null)
            return Snapshot(label, candidate.RootIdentity, MapMembership(candidate.Status),
                candidate.LedgerDigest, [], candidate.PendingMembershipMigration is null ? mismatch
                    : "PendingMembershipMigrationRecoveryRequired: " + mismatch, candidate.EnrollmentEpoch,
                candidate.Members.Select(static member => member.MemberId));

        var resolved = ResolveDeclarations(authority, declarations, cancellationToken);
        Exception? operationFailure = null;
        try
        {
            if (resolved.Diagnostic is not null)
                return Snapshot(label, candidate.RootIdentity, MapMembership(candidate.Status),
                    candidate.LedgerDigest, [], resolved.Diagnostic, candidate.EnrollmentEpoch,
                    candidate.Members.Select(static member => member.MemberId));
            var members = new List<PackageStoreEnrollmentPreviewMember>(declarations.Count);
            var pending = candidate.PendingStateCommit is not null || candidate.PendingGroupPublicationV2 is not null ||
                          candidate.PendingMembershipMigration is not null;
            for (var index = 0; index < declarations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declaration = declarations[index];
                var ledgerMember = candidate.Members.Single(member => member.MemberId == declaration.MemberId);
                var referent = resolved.Referents[index];
                var expected = ExpectedBinding(ledgerMember.Binding);
                if (expected.Slot is not null && GetSlot(referent) != expected.Slot)
                {
                    members.Add(UnavailableMember(declaration, ledgerMember.Binding,
                        pending ? PackageStoreEnrollmentPreviewMigration.RecoveryRequired : expected.Migration,
                        "The trusted locator resolves to a different native slot than the incomplete ledger binding."));
                    continue;
                }

                var observation = await ObserveAsync(referent, expected.Identity, expected.BodyDigest,
                    cancellationToken).ConfigureAwait(false);
                var state = observation.State;
                var migration = pending
                    ? PackageStoreEnrollmentPreviewMigration.RecoveryRequired
                    : expected.Migration;
                var closures = pending
                    ? (Active: Unknown("PendingPublicationRecoveryRequired"),
                        Recoverable: Unknown("PendingPublicationRecoveryRequired"))
                    : DescribeBoundClosures(state, observation.Status, candidate.RootIdentity, ledgerMember);
                var diagnostics = new List<string>
                {
                    "Incomplete membership is descriptive only; preview does not recover or mutate pending state."
                };
                if (observation.Diagnostic is not null)
                    diagnostics.Add(observation.Diagnostic);
                members.Add(new PackageStoreEnrollmentPreviewMember(declaration.MemberId,
                    declaration.StateFileLocator, ledgerMember.Binding.GetType().Name, Map(observation.Status),
                    DateTimeOffset.UtcNow, observation.Slot, observation.StateFileIdentity,
                    observation.RawPayloadDigest, observation.StateBodyDigest, state, closures.Active,
                    closures.Recoverable, migration, diagnostics,
                    GetRetiredGraphEvidence(state, candidate.RootIdentity)));
            }
            return new PackageStoreEnrollmentPreviewSnapshot(label, candidate.RootIdentity,
                MapMembership(candidate.Status), candidate.LedgerDigest, true, members,
                pending ? ["Pending local, group or membership migration publication requires recovery review; no next-slot binding was inferred."] : [],
                candidate.EnrollmentEpoch, candidate.Members.Select(static member => member.MemberId));
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            DisposeReferents(resolved.Referents, operationFailure);
        }
    }

    private async Task<PackageStoreEnrollmentPreviewSnapshot> PreviewCompleteAsync(
        PackageStoreAuthorityResolver authority,
        TrustedPackageStoreRoot selectedRoot,
        string label,
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        RootMembershipRecord candidate,
        CancellationToken cancellationToken)
    {
        var owner = await _registry!.AcquireNativeCatalogOwnerAsync(_rootCatalog, cancellationToken)
            .ConfigureAwait(false);
        RootMembershipRegistry.NativeCatalogOwnerBorrow? borrow = null;
        var members = new List<PackageStoreEnrollmentPreviewMember>();
        string? failure = null;
        Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        PhysicalRootIdentity? rootIdentity = candidate.RootIdentity;
        string? ledgerDigest = candidate.LedgerDigest;
        var enrollmentEpoch = candidate.EnrollmentEpoch;
        IEnumerable<string> currentMemberIds = candidate.Members.Select(static member => member.MemberId);
        try
        {
            borrow = await owner.BorrowAsync(cancellationToken).ConfigureAwait(false);
            var selected = owner.ResolveConfiguredRoot(borrow, selectedRoot);
            if (selected is null)
                failure = "CatalogMembershipChangedBeforeObservation";
            else
            {
                rootIdentity = selected.RootIdentity;
                ledgerDigest = selected.Ledger.LedgerDigest;
                enrollmentEpoch = selected.Ledger.EnrollmentEpoch;
                currentMemberIds = selected.Ledger.Members.Select(static member => member.MemberId).ToArray();
                failure = ValidateRetainedCatalogDeclarations(owner.SnapshotRetainedRoots());
                if (failure is null)
                {
                    var selectedLedger = selected.Ledger;
                    failure = CompareDeclarations(declarations, selectedLedger);
                    if (failure is null)
                    {
                        var initialVerification = await _registry.VerifyCatalogMemberProtectionAsync(borrow, cancellationToken)
                            .ConfigureAwait(false);
                        var selectedVerified = initialVerification.Where(member => member.RootIdentity == selected.RootIdentity)
                            .OrderBy(static member => member.MemberId, StringComparer.Ordinal).ToArray();
                        if (selectedVerified.Length != selectedLedger.Members.Count)
                            failure = "FullCatalogVerificationOmittedSelectedMember";
                        else
                        {
                            foreach (var declaration in declarations)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var ledgerMember = selectedLedger.Members.Single(member => member.MemberId == declaration.MemberId);
                                var location = selected.Locations![declaration.MemberId];
                                var verified = selectedVerified.Single(member => member.MemberId == declaration.MemberId);
                                var observation = await ObserveAsync(location.Parent, location.Slot, verified.StateFileIdentity,
                                    verified.StateBodyDigest, cancellationToken).ConfigureAwait(false);
                                try
                                {
                                    location.Revalidate();
                                }
                                catch (Exception exception) when (IsObservationFailure(exception))
                                {
                                    observation = observation.AsChanged("The retained complete-member locator failed replay after decoding.");
                                }

                                var closure = VerifiedClosures(observation.State, verified, observation.Status);
                                var diagnostics = observation.Diagnostic is null ? Array.Empty<string>() : [observation.Diagnostic];
                                members.Add(new PackageStoreEnrollmentPreviewMember(declaration.MemberId,
                                    declaration.StateFileLocator, ledgerMember.Binding.GetType().Name, Map(observation.Status),
                                    DateTimeOffset.UtcNow, observation.Slot, observation.StateFileIdentity,
                                    observation.RawPayloadDigest, observation.StateBodyDigest, observation.State,
                                    closure.Active, closure.Recoverable, PackageStoreEnrollmentPreviewMigration.None,
                                    diagnostics, GetRetiredGraphEvidence(observation.State, verified.RootIdentity)));
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            var replay = await _registry.VerifyCatalogMemberProtectionAsync(borrow, cancellationToken)
                                .ConfigureAwait(false);
                            if (!NativeCatalogMemberVerificationComparer.Same(initialVerification, replay))
                            {
                                failure = "FullCatalogVerificationChangedDuringObservation";
                                members = members.Select(static member => member.WithChanged(
                                    "Full catalog state or install evidence changed during the independent preview reads.")).ToList();
                            }
                            owner.RevalidateRetainedEvidence();
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (borrow is not null)
            {
                try { await borrow.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        if (operationFailure is not null)
        {
            if (operationFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                if (cleanupFailures.Count > 0)
                    throw new AggregateException("Enrollment preview was canceled and retained catalog ownership cleanup also failed.",
                        new[] { operationFailure }.Concat(cleanupFailures));
                ExceptionDispatchInfo.Capture(operationFailure).Throw();
            }
            if (IsObservationFailure(operationFailure))
            {
                failure = "FullCatalogObservationUnavailable: " + operationFailure.GetType().Name + ": " + operationFailure.Message;
                members = members.Select(member => member.WithChanged(failure)).ToList();
            }
            else if (cleanupFailures.Count > 0)
                throw new AggregateException("Enrollment preview failed and retained catalog ownership cleanup also failed.",
                    new[] { operationFailure }.Concat(cleanupFailures));
            else
                ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (cleanupFailures.Count > 0)
        {
            failure = string.Join("; ", new[] { failure }.Where(static item => item is not null)
                .Concat(cleanupFailures.Select(static exception => "CatalogOwnershipReleaseUnavailable: " + exception.GetType().Name)));
            members = members.Select(member => member.WithChanged(
                "Retained catalog ownership did not release cleanly; closure certainty is withheld.")).ToList();
        }

        return new PackageStoreEnrollmentPreviewSnapshot(label, rootIdentity,
            MapMembership(candidate.Status), ledgerDigest, true, members,
            failure is null ? [] : [failure], enrollmentEpoch, currentMemberIds);
    }

    private (ResolvedStatePayloadReferent[] Referents, string? Diagnostic) ResolveDeclarations(
        PackageStoreAuthorityResolver authority,
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        var referents = new List<ResolvedStatePayloadReferent>(declarations.Count);
        try
        {
            foreach (var declaration in declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var referent = authority.ResolveStatePayloadReferentMetadata(declaration.StateFileLocator, cancellationToken);
                try
                {
                    referent.Revalidate(cancellationToken);
                    referents.Add(referent);
                }
                catch (Exception primaryFailure)
                {
                    DisposeReferents([referent], primaryFailure);
                    throw;
                }
            }

            var duplicates = referents.Select(GetSlot).GroupBy(static slot => slot).Any(static group => group.Count() > 1);
            if (duplicates)
                return (referents.ToArray(), "TrustedDeclarationsResolveToDuplicateNativeStateSlots");
            return (referents.ToArray(), null);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            DisposeReferents(referents, exception);
            throw;
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            DisposeReferents(referents, exception);
            return ([], "TrustedStateLocatorUnavailable: " + exception.GetType().Name);
        }
    }

    private async Task<NativeStoreStatePayloadObservation> ObserveAsync(
        ResolvedStatePayloadReferent referent,
        PhysicalFileIdentity? expectedIdentity,
        string? expectedBodyDigest,
        CancellationToken cancellationToken)
    {
        var slot = GetSlot(referent);
        var metadataEntry = referent.FinalPayloadSlot.Entry;
        if (metadataEntry is not null && metadataEntry.Identity != expectedIdentity && expectedIdentity is not null)
            return new NativeStoreStatePayloadObservation(NativeStoreStatePayloadStatus.Changed, slot,
                metadataEntry.Identity, null, null, null,
                "The state payload identity differs from its trusted membership binding.");
        var observation = await NativeStoreStatePayloadReader.ReadAsync(_files, _serializer,
            referent.FinalParent, slot, expectedIdentity, expectedBodyDigest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            referent.Revalidate(cancellationToken);
            var current = _files.InspectChildNoFollow(referent.FinalParent, slot.CanonicalBasename);
            var retainedIdentity = referent.FinalPayloadSlot.Entry?.Identity;
            var matchesRetained = retainedIdentity is null
                ? current is null
                : current is { Kind: PhysicalStoreEntryKind.RegularFile } && current.Identity == retainedIdentity;
            var matchesObservation = observation.Status switch
            {
                NativeStoreStatePayloadStatus.Missing => current is null,
                _ => observation.StateFileIdentity is { } identity &&
                     current is { Kind: PhysicalStoreEntryKind.RegularFile } && current.Identity == identity
            };
            if (!matchesRetained || !matchesObservation)
                return observation.AsChanged("The exact state leaf identity changed during locator replay.");
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return observation.AsChanged("The retained state locator or native leaf failed replay after serializer completion.");
        }
        return observation;
    }

    private async Task<NativeStoreStatePayloadObservation> ObserveAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalFileIdentity expectedIdentity,
        string expectedBodyDigest,
        CancellationToken cancellationToken)
    {
        var observation = await NativeStoreStatePayloadReader.ReadAsync(_files, _serializer,
            parent, slot, expectedIdentity, expectedBodyDigest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return observation;
    }

    private string? ValidateDeclarationLabels()
    {
        var roots = _rootCatalog.Roots;
        var known = roots.Select(root => DeclarationLabel(root.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in _declarations.All)
        {
            if (!known.Contains(declaration.RootLabel))
                return "TrustedMemberDeclarationUsesUnknownRootLabel";
        }
        foreach (var root in roots)
        {
            var declarationLabel = DeclarationLabel(root.Label);
            if (_declarations.ForRoot(declarationLabel).Count == 0)
                return "TrustedMemberDeclarationsMissingForConfiguredRoot";
        }
        return null;
    }

    private string? ValidateRetainedCatalogDeclarations(IReadOnlyList<NativeCatalogLockedRoot> retainedRoots)
    {
        foreach (var retainedRoot in retainedRoots)
        {
            var ledger = retainedRoot.Ledger;
            foreach (var alias in retainedRoot.Aliases)
            {
                var label = DeclarationLabel(alias.Configured.Label);
                var declarations = _declarations.ForRoot(label);
                if (declarations.Count == 0)
                    return "TrustedMemberDeclarationsMissingForEnrolledCatalogRoot";
                if (declarations.Count != ledger.Members.Count ||
                    !declarations.Select(static declaration => declaration.MemberId).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(ledger.TargetMemberIds))
                    return "TrustedDeclarationsDoNotMatchCompleteCatalogMembers";
                foreach (var member in ledger.Members)
                {
                    var declaration = declarations.SingleOrDefault(item => item.MemberId == member.MemberId);
                    if (declaration is null || !string.Equals(declaration.StateFileLocator, member.ConfiguredLocator,
                            StringComparison.Ordinal))
                        return "TrustedDeclarationLocatorDoesNotMatchCompleteCatalogMember";
                }
            }
        }
        return null;
    }

    private static string? CompareDeclarations(
        IReadOnlyList<TrustedPackageStoreMemberDeclaration> declarations,
        RootMembershipRecord candidate)
    {
        if (declarations.Count != candidate.Members.Count ||
            !declarations.Select(static declaration => declaration.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(candidate.TargetMemberIds))
            return "TrustedDeclarationsDoNotMatchLedgerMembersAndTargets";
        foreach (var member in candidate.Members)
        {
            var declaration = declarations.SingleOrDefault(item => item.MemberId == member.MemberId);
            if (declaration is null || !string.Equals(declaration.StateFileLocator, member.ConfiguredLocator,
                    StringComparison.Ordinal))
                return "TrustedDeclarationLocatorDoesNotMatchLedgerMember";
        }
        return null;
    }

    private static (PhysicalFileIdentity? Identity, string? BodyDigest,
        PackageStoreEnrollmentPreviewMigration Migration, StateSlotIdentity? Slot) ExpectedBinding(
        RootMemberRecord.MemberBinding binding)
        => binding switch
        {
            RootMemberRecord.ExistingUnprotectedBinding unprotected => (unprotected.ObservedStateFileIdentity,
                unprotected.StateBodyDigest, PackageStoreEnrollmentPreviewMigration.ProtectionAcknowledgementRequired,
                unprotected.StateSlot),
            RootMemberRecord.AcknowledgedBinding acknowledged => (acknowledged.ObservedStateFileIdentity,
                acknowledged.ProtectionRecord.StateBodyDigest, PackageStoreEnrollmentPreviewMigration.None,
                acknowledged.StateSlot),
            RootMemberRecord.BundleAcknowledgedBinding bundle => (bundle.ObservedStateFileIdentity,
                bundle.StateBodyDigest, PackageStoreEnrollmentPreviewMigration.None, bundle.StateSlot),
            RootMemberRecord.ProspectiveBinding prospective => (null, null,
                PackageStoreEnrollmentPreviewMigration.BindingRequired,
                new StateSlotIdentity(prospective.VerifiedParentIdentity, prospective.NameSemantics,
                    prospective.RequestedBasename)),
            _ => (null, null, PackageStoreEnrollmentPreviewMigration.BindingRequired, null)
        };

    private static (PackageStoreEnrollmentPreviewClosure Active, PackageStoreEnrollmentPreviewClosure Recoverable)
        DescribeBoundClosures(StoreStateRecord? state, NativeStoreStatePayloadStatus status,
            PhysicalRootIdentity expectedRootIdentity, RootMemberRecord member)
    {
        if (status != NativeStoreStatePayloadStatus.Observed || state is null)
            return DescribeClosures(state, status, expectedRootIdentity);

        var boundProtectionMatches = member.Binding switch
        {
            RootMemberRecord.AcknowledgedBinding acknowledged =>
                state.ProtectionBundle is null && state.ProtectionRecord is { } actual &&
                actual.HasSamePayloadAs(acknowledged.ProtectionRecord),
            RootMemberRecord.BundleAcknowledgedBinding bundle => BundleBindingMatches(
                state, expectedRootIdentity, bundle),
            RootMemberRecord.ExistingUnprotectedBinding =>
                state.ProtectionRecord is null && state.ProtectionBundle is null,
            _ => false
        };
        if (!boundProtectionMatches)
            return (Unknown("ProtectionBindingMismatch"), Unknown("ProtectionBindingMismatch"));

        return DescribeClosures(state, status, expectedRootIdentity);
    }

    private static bool BundleBindingMatches(StoreStateRecord state, PhysicalRootIdentity expectedRootIdentity,
        RootMemberRecord.BundleAcknowledgedBinding binding)
    {
        var bundle = state.ProtectionBundle;
        if (state.ProtectionRecord is not null || bundle is null ||
            bundle.LogicalMemberId != binding.LogicalMemberId || bundle.PublicationId != binding.PublicationId ||
            bundle.StateGeneration != binding.StateGeneration ||
            !string.Equals(bundle.ParticipantSetDigest, binding.ParticipantSetDigest, StringComparison.Ordinal) ||
            !string.Equals(bundle.StateBodyDigest, binding.StateBodyDigest, StringComparison.Ordinal) ||
            !string.Equals(bundle.BundleDigest, binding.BundleDigest, StringComparison.Ordinal))
            return false;

        var row = bundle.Rows.SingleOrDefault(candidate => candidate.RootIdentity == expectedRootIdentity);
        return row is not null && row.HasSamePayloadAs(binding.RootRow);
    }

    private static PackageStoreEnrollmentPreviewMember UnavailableMember(
        TrustedPackageStoreMemberDeclaration declaration,
        RootMemberRecord.MemberBinding binding,
        PackageStoreEnrollmentPreviewMigration migration,
        string diagnostic)
        => new(declaration.MemberId, declaration.StateFileLocator, binding.GetType().Name,
            PackageStoreEnrollmentPreviewObservation.Changed, DateTimeOffset.UtcNow, null, null,
            null, null, null, Unknown("StateChangedDuringObservation"),
            Unknown("StateChangedDuringObservation"), migration, [diagnostic]);

    private static StateSlotIdentity GetSlot(ResolvedStatePayloadReferent referent)
    {
        var slot = referent.FinalPayloadSlot;
        return new StateSlotIdentity(slot.ParentIdentity, slot.NameSemantics, slot.Name);
    }

    private static IReadOnlyList<RetiredGraphEvidence> GetRetiredGraphEvidence(
        StoreStateRecord? state, PhysicalRootIdentity? expectedRootIdentity)
    {
        if (state is null || expectedRootIdentity is null ||
            state.ProtectionRecord is not null && state.ProtectionBundle is not null)
            return Array.Empty<RetiredGraphEvidence>();
        if (state.ProtectionRecord is { } protection && protection.RootIdentity == expectedRootIdentity)
            return Array.AsReadOnly(protection.RetiredGraphs.Select(static graph => graph.Copy()).ToArray());
        var row = state.ProtectionBundle?.Rows.SingleOrDefault(candidate =>
            candidate.RootIdentity == expectedRootIdentity);
        return row is null
            ? Array.Empty<RetiredGraphEvidence>()
            : Array.AsReadOnly(row.RetiredGraphs.Select(static graph => graph.Copy()).ToArray());
    }

    private static PackageStoreEnrollmentPreviewSnapshot Snapshot(
        string label,
        PhysicalRootIdentity? root,
        PackageStoreEnrollmentPreviewMembership? membership,
        string? ledgerDigest,
        IEnumerable<PackageStoreEnrollmentPreviewMember> members,
        string diagnostic,
        long? enrollmentEpoch = null,
        IEnumerable<string>? currentMemberIds = null)
        => new(label, root, membership, ledgerDigest, true, members, [diagnostic], enrollmentEpoch, currentMemberIds);

    private static PackageStoreEnrollmentPreviewObservation Map(NativeStoreStatePayloadStatus status)
        => status switch
        {
            NativeStoreStatePayloadStatus.Observed => PackageStoreEnrollmentPreviewObservation.Observed,
            NativeStoreStatePayloadStatus.Missing => PackageStoreEnrollmentPreviewObservation.Missing,
            NativeStoreStatePayloadStatus.NullPayload => PackageStoreEnrollmentPreviewObservation.NullPayload,
            NativeStoreStatePayloadStatus.Malformed => PackageStoreEnrollmentPreviewObservation.Malformed,
            NativeStoreStatePayloadStatus.Changed => PackageStoreEnrollmentPreviewObservation.Changed,
            _ => PackageStoreEnrollmentPreviewObservation.Unavailable
        };

    private static (PackageStoreEnrollmentPreviewClosure Active, PackageStoreEnrollmentPreviewClosure Recoverable)
        DescribeClosures(StoreStateRecord? state, NativeStoreStatePayloadStatus status,
            PhysicalRootIdentity? expectedRootIdentity)
    {
        if (status != NativeStoreStatePayloadStatus.Observed || state is null)
        {
            var reason = status switch
            {
                NativeStoreStatePayloadStatus.Missing => "StateMissing",
                NativeStoreStatePayloadStatus.NullPayload => "StateNull",
                NativeStoreStatePayloadStatus.Malformed => "StateMalformed",
                NativeStoreStatePayloadStatus.Changed => "StateChangedDuringObservation",
                _ => "StateUnavailable"
            };
            return (Unknown(reason), Unknown(reason));
        }

        if (state.ProtectionBundle is { } bundle)
        {
            if (expectedRootIdentity is null)
                return (Unknown("ProtectionRootIdentityUnavailable"), Unknown("ProtectionRootIdentityUnavailable"));
            var row = bundle.Rows.SingleOrDefault(candidate => candidate.RootIdentity == expectedRootIdentity);
            if (row is null)
                return (Unknown("ProtectionRootMismatch"), Unknown("ProtectionRootMismatch"));
            if (bundle.Rows.Any(static participant =>
                    participant.ActiveClosure.Knowledge == PackageProtectionClosureKnowledge.Unknown ||
                    participant.RecoverableClosure.Knowledge == PackageProtectionClosureKnowledge.Unknown))
            {
                if (!HasValidBundleDigests(state, bundle))
                    return (Unknown("MalformedPersistedProtection"), Unknown("MalformedPersistedProtection"));
                return (UnknownUnlessVerified(row.ActiveClosure, "ProtectionClosureUnverified"),
                    UnknownUnlessVerified(row.RecoverableClosure, "ProtectionClosureUnverified"));
            }
            try
            {
                var participants = bundle.Rows.Select(static participant => participant.RootIdentity).ToHashSet();
                _ = PersistedStoreStateBundleGraphVerifier.Verify(state, bundle, participants);
                return (Known(row.ActiveClosure.Graphs!.Select(static graph => graph.GraphId)),
                    Known(row.RecoverableClosure.Graphs!.Select(static graph => graph.GraphId)));
            }
            catch (PackageStoreAdmissionException)
            {
                return (Unknown("MalformedPersistedProtection"), Unknown("MalformedPersistedProtection"));
            }
        }
        if (state.ProtectionRecord is { } protection)
        {
            if (expectedRootIdentity is null)
                return (Unknown("ProtectionRootIdentityUnavailable"), Unknown("ProtectionRootIdentityUnavailable"));
            if (protection.ActiveClosure.Knowledge == PackageProtectionClosureKnowledge.Unknown ||
                protection.RecoverableClosure.Knowledge == PackageProtectionClosureKnowledge.Unknown)
            {
                if (protection.RootIdentity != expectedRootIdentity)
                    return (Unknown("ProtectionRootMismatch"), Unknown("ProtectionRootMismatch"));
                if (!HasValidV1ProtectionDigests(state, protection))
                    return (Unknown("MalformedPersistedProtection"), Unknown("MalformedPersistedProtection"));
                return (UnknownUnlessVerified(protection.ActiveClosure, "ProtectionClosureUnverified"),
                    UnknownUnlessVerified(protection.RecoverableClosure, "ProtectionClosureUnverified"));
            }
            if (protection.RootIdentity != expectedRootIdentity)
                return (Unknown("ProtectionRootMismatch"), Unknown("ProtectionRootMismatch"));
            try
            {
                var verified = PersistedStoreStateGraphVerifier.Verify(state);
                return (Known(verified.ActiveGraphs.Select(static graph => graph.GraphId)),
                    Known(verified.RecoverableGraphs.Select(static graph => graph.GraphId)));
            }
            catch (PackageStoreAdmissionException)
            {
                return (Unknown("MalformedPersistedProtection"), Unknown("MalformedPersistedProtection"));
            }
        }

        var recoveryReason = state.LastKnownGoodById.Count > 0
            ? "RecoveryClosureUnavailable"
            : "LegacyProtectionMissing";
        return (Unknown("LegacyProtectionMissing"), Unknown(recoveryReason));
    }

    private static (PackageStoreEnrollmentPreviewClosure Active, PackageStoreEnrollmentPreviewClosure Recoverable)
        VerifiedClosures(StoreStateRecord? state, NativeCatalogMemberVerification verified,
            NativeStoreStatePayloadStatus observationStatus)
    {
        if (observationStatus != NativeStoreStatePayloadStatus.Observed)
            return (Unknown(observationStatus == NativeStoreStatePayloadStatus.Changed
                    ? "StateChangedDuringObservation" : "FullCatalogObservationUnavailable"),
                Unknown(observationStatus == NativeStoreStatePayloadStatus.Changed
                    ? "StateChangedDuringObservation" : "FullCatalogObservationUnavailable"));
        if (state is null)
            return (Unknown("StateUnavailable"), Unknown("StateUnavailable"));
        if (verified.V1Graphs is { } v1)
        {
            var protection = state.ProtectionRecord;
            if (state.ProtectionBundle is not null || protection is null ||
                protection.RootIdentity != verified.RootIdentity || protection.EnrollmentEpoch != verified.EnrollmentEpoch ||
                protection.MemberId != verified.MemberId || protection.Revision != verified.Revision ||
                !string.Equals(protection.StateBodyDigest, verified.StateBodyDigest, StringComparison.Ordinal) ||
                !string.Equals(protection.ProtectionDigest, verified.ProtectionDigest, StringComparison.Ordinal) ||
                !string.Equals(ProtectionDigest.Protection(protection), verified.ProtectionDigest, StringComparison.Ordinal))
                return (Unknown("SelectedCatalogProtectionProjectionMismatch"),
                    Unknown("SelectedCatalogProtectionProjectionMismatch"));
            return (Known(v1.ActiveGraphs.Select(static graph => graph.GraphId)),
                Known(v1.RecoverableGraphs.Select(static graph => graph.GraphId)));
        }
        if (verified.V2Graphs is { } v2)
        {
            var bundle = state.ProtectionBundle;
            var row = bundle?.Rows.SingleOrDefault(candidate => candidate.RootIdentity == verified.RootIdentity);
            if (state.ProtectionRecord is not null || bundle is null || row is null ||
                bundle.SchemaVersion != PackageProtectionBundle.CurrentSchemaVersion ||
                bundle.StateGeneration != row.StateGeneration ||
                bundle.StateBodyDigest != row.StateBodyDigest ||
                !string.Equals(ProtectionDigest.StateBody(state), verified.StateBodyDigest, StringComparison.Ordinal) ||
                !string.Equals(ProtectionDigest.PackageProtectionParticipantSet(bundle.LogicalMemberId,
                    bundle.Rows.Select(static participant => participant.RootIdentity)), bundle.ParticipantSetDigest,
                    StringComparison.Ordinal) ||
                row.EnrollmentEpoch != verified.EnrollmentEpoch || row.MemberId != verified.MemberId ||
                row.Revision != verified.Revision || row.StateBodyDigest != verified.StateBodyDigest ||
                row.ProtectionDigest != verified.ProtectionDigest || bundle.BundleDigest != verified.BundleDigest ||
                !string.Equals(ProtectionDigest.PackageProtectionBundleRow(row), verified.ProtectionDigest,
                    StringComparison.Ordinal) ||
                !string.Equals(ProtectionDigest.PackageProtectionBundle(bundle), verified.BundleDigest,
                    StringComparison.Ordinal) ||
                row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                !SameGraphIds(row.ActiveClosure.Graphs!.Select(static graph => graph.GraphId),
                    v2.ActiveGraphs.Select(static graph => graph.GraphId)) ||
                !SameGraphIds(row.RecoverableClosure.Graphs!.Select(static graph => graph.GraphId),
                    v2.RecoverableGraphs.Select(static graph => graph.GraphId)))
                return (Unknown("SelectedCatalogProtectionProjectionMismatch"),
                    Unknown("SelectedCatalogProtectionProjectionMismatch"));
            return (Known(row.ActiveClosure.Graphs!.Select(static graph => graph.GraphId)),
                Known(row.RecoverableClosure.Graphs!.Select(static graph => graph.GraphId)));
        }
        return (Unknown("MalformedPersistedProtection"), Unknown("MalformedPersistedProtection"));
    }

    private static bool HasValidV1ProtectionDigests(StoreStateRecord state, PackageProtectionRecord protection)
        => string.Equals(ProtectionDigest.StateBody(state), protection.StateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ProtectionDigest.Protection(protection), protection.ProtectionDigest, StringComparison.Ordinal);

    private static bool HasValidBundleDigests(StoreStateRecord state, PackageProtectionBundle bundle)
        => string.Equals(ProtectionDigest.StateBody(state), bundle.StateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ProtectionDigest.PackageProtectionParticipantSet(bundle.LogicalMemberId,
                   bundle.Rows.Select(static row => row.RootIdentity)), bundle.ParticipantSetDigest,
               StringComparison.Ordinal) &&
           string.Equals(ProtectionDigest.PackageProtectionBundle(bundle), bundle.BundleDigest, StringComparison.Ordinal) &&
           bundle.Rows.All(row => string.Equals(ProtectionDigest.PackageProtectionBundleRow(row),
               row.ProtectionDigest, StringComparison.Ordinal));

    private static PackageStoreEnrollmentPreviewClosure UnknownUnlessVerified(
        PackageProtectionClosure closure, string reason)
        => closure.Knowledge == PackageProtectionClosureKnowledge.Unknown
            ? CopyClosure(closure)
            : Unknown(reason);

    private static PackageStoreEnrollmentPreviewClosure UnknownUnlessVerified(
        PackageProtectionClosureV2 closure, string reason)
        => closure.Knowledge == PackageProtectionClosureKnowledge.Unknown
            ? CopyClosure(closure)
            : Unknown(reason);

    private static bool SameGraphIds(IEnumerable<string> first, IEnumerable<string> second)
        => first.OrderBy(static id => id, StringComparer.Ordinal)
            .SequenceEqual(second.OrderBy(static id => id, StringComparer.Ordinal), StringComparer.Ordinal);

    private static PackageStoreEnrollmentPreviewClosure CopyClosure(PackageProtectionClosure closure)
        => closure.Knowledge == PackageProtectionClosureKnowledge.Known
            ? Known(closure.Graphs!.Select(static graph => graph.GraphId))
            : Unknown(closure.UnknownReason!.Value.ToString());

    private static PackageStoreEnrollmentPreviewClosure CopyClosure(PackageProtectionClosureV2 closure)
        => closure.Knowledge == PackageProtectionClosureKnowledge.Known
            ? Known(closure.Graphs!.Select(static graph => graph.GraphId))
            : Unknown(closure.UnknownReason!.Value.ToString());

    private static PackageStoreEnrollmentPreviewClosure Known(IEnumerable<string> graphIds)
        => new(PackageProtectionClosureKnowledge.Known, null, graphIds.Distinct(StringComparer.Ordinal).ToArray());

    private static PackageStoreEnrollmentPreviewClosure Unknown(string reason)
        => new(PackageProtectionClosureKnowledge.Unknown, reason, null);

    private static PackageStoreEnrollmentPreviewMembership MapMembership(RootMembershipStatus status)
        => status == RootMembershipStatus.Complete
            ? PackageStoreEnrollmentPreviewMembership.Complete
            : PackageStoreEnrollmentPreviewMembership.Incomplete;

    private string DeclarationLabel(string catalogLabel)
        => string.Equals(catalogLabel, "default", StringComparison.OrdinalIgnoreCase)
            ? _rootResolver.ConfiguredDefaultLabel ?? catalogLabel
            : catalogLabel;

    private static bool IsObservationFailure(Exception exception)
        => exception is PackageStoreAdmissionException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException;

    private static void DisposeReferents(
        IEnumerable<ResolvedStatePayloadReferent> referents,
        Exception? primaryFailure = null)
    {
        var failures = new List<Exception>();
        foreach (var referent in referents)
        {
            try { referent.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count == 0)
            return;
        if (primaryFailure is not null)
            throw new AggregateException("Enrollment preview failed and one or more retained state locators did not release.",
                new[] { primaryFailure }.Concat(failures));
        throw new AggregateException("One or more retained state locators did not release after enrollment preview.", failures);
    }

    private static void DisposePathPreservingPrimary(ResolvedPackageStorePath? path, Exception? primaryFailure)
    {
        if (path is null)
            return;
        try { path.Dispose(); }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is not null)
                throw new AggregateException("Root metadata observation failed and its retained path did not release.",
                    primaryFailure, cleanupFailure);
            throw;
        }
    }
}

internal static class NativeStoreStatePayloadObservationExtensions
{
    internal static NativeStoreStatePayloadObservation AsChanged(
        this NativeStoreStatePayloadObservation observation,
        string diagnostic)
        => new(NativeStoreStatePayloadStatus.Changed, observation.Slot,
            observation.StateFileIdentity, observation.RawPayloadDigest, observation.StateBodyDigest,
            observation.State, diagnostic);
}

internal static class PackageStoreEnrollmentPreviewMemberExtensions
{
    internal static PackageStoreEnrollmentPreviewMember WithChanged(
        this PackageStoreEnrollmentPreviewMember member,
        string diagnostic)
    {
        var state = new StoreStateRecord(
            new Dictionary<string, string>(member.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(member.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            member.StateUpdatedAtUtc ?? DateTimeOffset.UnixEpoch,
            member.ActivePackageDescriptorsById.ToDictionary(static pair => pair.Key, static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            member.ActivationHistoryById.ToDictionary(static pair => pair.Key, static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase));
        return new(member.MemberId, member.StateFileLocator, member.BindingKind,
            PackageStoreEnrollmentPreviewObservation.Changed, member.ObservedAtUtc,
            member.StateSlot, member.StateFileIdentity, member.RawPayloadDigest, member.StateBodyDigest,
            state, Unknown("StateChangedDuringObservation"), Unknown("StateChangedDuringObservation"),
            member.Migration, member.Diagnostics.Append(diagnostic), member.RetiredGraphs);
    }

    private static PackageStoreEnrollmentPreviewClosure Unknown(string reason)
        => new(PackageProtectionClosureKnowledge.Unknown, reason, null);
}
