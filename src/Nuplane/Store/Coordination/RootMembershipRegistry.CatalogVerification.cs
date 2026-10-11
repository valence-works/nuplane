using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Verifies every acknowledged state held by the exact retained native catalog borrow.</summary>
    /// <remarks>
    /// This operation is descriptive only. It reads each unique physical state slot once, preserves the verified
    /// v1 and v2 graph formats separately, and retains the catalog's complete root/member lock union through the
    /// final state-slot, install, and ledger replay.
    /// </remarks>
    internal async Task<IReadOnlyList<NativeCatalogMemberVerification>> VerifyCatalogMemberProtectionAsync(
        NativeCatalogOwnerBorrow borrow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        borrow.RequireRegistry(this);
        using var operation = borrow.EnterOperation(this);
        cancellationToken.ThrowIfCancellationRequested();

        var owner = borrow.Owner;
        owner.RevalidateRetainedEvidence();
        var roots = owner.SnapshotRetainedRoots()
            .OrderBy(static root => root.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ToArray();
        if (roots.Length == 0)
            throw Refused("Full catalog verification requires at least one positively enrolled root.");

        var members = PreflightCatalogMembers(roots);
        var slots = PreflightCatalogSlots(members);
        var observations = new List<IDisposable>();
        var installRevalidations = new List<Action>();
        var verifiedBySlot = new Dictionary<StateSlotIdentity, VerifiedCatalogSlot>();
        try
        {
            foreach (var slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.RevalidateRetainedEvidence();
                var first = slot.Members[0];
                var expectedIdentity = first.Location.ExistingFileIdentity
                    ?? throw RefuseCatalog("A settled catalog acknowledgement requires an existing state file.", first.Root.RootIdentity);
                var state = (await ReadStateAsync(first.Location.Parent, slot.Slot, expectedIdentity, cancellationToken)
                    .ConfigureAwait(false)).State;

                if (slot.IsBundle)
                {
                    VerifyCatalogBundleState(slot, state);
                    var bundle = state.ProtectionBundle!;
                    var participants = slot.Members.Select(static item => item.Root.RootIdentity).ToHashSet();
                    var graphs = PersistedStoreStateBundleGraphVerifier.Verify(state, bundle, participants);
                    foreach (var member in slot.Members)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var binding = (RootMemberRecord.BundleAcknowledgedBinding)member.Member.Binding;
                        var row = bundle.Rows.SingleOrDefault(candidate => candidate.RootIdentity == member.Root.RootIdentity)
                            ?? throw RefuseCatalog("The shared bundle omits a retained catalog participant row.", member.Root.RootIdentity);
                        if (!row.HasSamePayloadAs(binding.RootRow) ||
                            binding.LogicalMemberId != bundle.LogicalMemberId ||
                            binding.PublicationId != bundle.PublicationId ||
                            binding.StateGeneration != bundle.StateGeneration ||
                            !string.Equals(binding.ParticipantSetDigest, bundle.ParticipantSetDigest, StringComparison.Ordinal) ||
                            !string.Equals(binding.StateBodyDigest, bundle.StateBodyDigest, StringComparison.Ordinal) ||
                            !string.Equals(binding.BundleDigest, bundle.BundleDigest, StringComparison.Ordinal))
                            throw RefuseCatalog("A v2 state bundle differs from a retained root-local catalog acknowledgement.", member.Root.RootIdentity);

                        ObserveCatalogBundleInstalls(member.Root, state, graphs, observations, installRevalidations,
                            cancellationToken);
                    }
                    verifiedBySlot.Add(slot.Slot, new VerifiedCatalogSlot(null, graphs));
                }
                else
                {
                    var member = first;
                    if (slot.Members.Count != 1)
                        throw Refused("A v1 state slot cannot be acknowledged by multiple retained catalog members.");
                    if (state.ProtectionBundle is not null)
                        throw RefuseCatalog("A v1 catalog acknowledgement cannot verify a state that also carries a v2 bundle.",
                            member.Root.RootIdentity);
                    var binding = (RootMemberRecord.AcknowledgedBinding)member.Member.Binding;
                    PersistedStoreStateGraphVerifier.VerifiedStoreStateGraphs graphs;
                    if (member.Root.Ledger.SchemaVersion == RootMembershipRecord.CurrentSchemaVersion)
                    {
                        graphs = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(state, member.Root.Ledger,
                            member.Member, member.Root.RootIdentity, member.Root.EnrollmentEpoch,
                            RootMembershipStatus.Complete);
                    }
                    else
                    {
                        graphs = PersistedStoreStateGraphVerifier.Verify(state);
                        RequireCatalogV1Acknowledgement(state, binding, member.Root);
                    }

                    ObserveProtectedInstalls(member.Root.PrimaryResolution.AuthorityRoot!, member.Root.RootIdentity,
                        state, graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs), member.Root.Scope!, observations,
                        installRevalidations, cancellationToken);
                    verifiedBySlot.Add(slot.Slot, new VerifiedCatalogSlot(graphs, null));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            owner.RevalidateRetainedEvidence();
            foreach (var revalidate in installRevalidations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                revalidate();
            }
            owner.RevalidateRetainedEvidence();

            var result = members.Select(member =>
            {
                var verified = verifiedBySlot[member.Location.Slot];
                var revision = member.Member.Binding switch
                {
                    RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ProtectionRecord.Revision,
                    RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.RootRow.Revision,
                    _ => throw RefuseCatalog("A catalog member lost its acknowledged binding during verification.", member.Root.RootIdentity)
                };
                return new NativeCatalogMemberVerification(member.Root.RootIdentity, member.Root.EnrollmentEpoch,
                    member.Member.MemberId, member.Location.Slot, member.Location.ExistingFileIdentity!, revision,
                    verified.V1Graphs, verified.V2Graphs);
            }).ToArray();
            return new ReadOnlyCollection<NativeCatalogMemberVerification>(result);
        }
        finally
        {
            for (var index = observations.Count - 1; index >= 0; index--)
                observations[index].Dispose();
        }
    }

    private static CatalogMemberEvidence[] PreflightCatalogMembers(IReadOnlyList<NativeCatalogLockedRoot> roots)
    {
        var result = new List<CatalogMemberEvidence>();
        foreach (var root in roots)
        {
            var ledger = root.Ledger;
            if (root.ReplayPolicy is not (LocatorReplayBindingPolicy.Acknowledged or LocatorReplayBindingPolicy.GroupAcknowledged) ||
                ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
                ledger.PendingGroupPublicationV2 is not null || ledger.RootIdentity != root.RootIdentity ||
                ledger.EnrollmentEpoch != root.EnrollmentEpoch ||
                !string.Equals(ProtectionDigest.Ledger(ledger), ledger.LedgerDigest, StringComparison.Ordinal))
                throw RefuseCatalog("Full catalog verification requires every retained root to be exact, Complete, and non-pending.",
                    root.RootIdentity);

            var memberIds = ledger.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
            var locations = root.Locations;
            if (memberIds.Count == 0 || memberIds.Count != ledger.Members.Count ||
                !memberIds.SetEquals(ledger.TargetMemberIds) || locations is null ||
                !memberIds.SetEquals(locations.Keys))
                throw RefuseCatalog("Full catalog verification requires the exact non-empty member/target/location union.",
                    root.RootIdentity);

            var seenSlots = new HashSet<StateSlotIdentity>();
            foreach (var member in ledger.Members)
            {
                var location = locations[member.MemberId];
                var expected = member.Binding switch
                {
                    RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
                    RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.ObservedStateFileIdentity,
                    _ => throw RefuseCatalog("Full catalog verification refuses unacknowledged or unsupported member bindings.", root.RootIdentity)
                };
                var expectedSlot = member.Binding switch
                {
                    RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
                    RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.StateSlot,
                    _ => throw RefuseCatalog("Full catalog verification refuses unacknowledged or unsupported member bindings.", root.RootIdentity)
                };
                if (location.Slot != expectedSlot || location.ExistingFileIdentity != expected ||
                    !seenSlots.Add(location.Slot))
                    throw RefuseCatalog("A retained catalog member does not bind its exact existing native state slot.", root.RootIdentity);
                location.Revalidate();

                switch (member.Binding)
                {
                    case RootMemberRecord.AcknowledgedBinding acknowledged:
                        RequireCatalogV1BindingShape(acknowledged, root, member.MemberId);
                        break;
                    case RootMemberRecord.BundleAcknowledgedBinding bundle:
                        RequireCatalogV2BindingShape(bundle, root, member.MemberId);
                        break;
                }
                result.Add(new CatalogMemberEvidence(root, member, location));
            }
        }
        return result.ToArray();
    }

    private static CatalogSlotEvidence[] PreflightCatalogSlots(IReadOnlyList<CatalogMemberEvidence> members)
    {
        var result = new List<CatalogSlotEvidence>();
        foreach (var group in members.GroupBy(static member => member.Location.Slot))
        {
            var shared = group.ToArray();
            var bundle = shared.Any(static member => member.Member.Binding is RootMemberRecord.BundleAcknowledgedBinding);
            if (!bundle)
            {
                if (shared.Length != 1)
                    throw Refused("A v1 state slot cannot be shared across retained catalog members.");
                result.Add(new CatalogSlotEvidence(group.Key, shared, false));
                continue;
            }

            if (shared.Any(static member => member.Member.Binding is not RootMemberRecord.BundleAcknowledgedBinding) ||
                shared.Select(static member => member.Root.RootIdentity).Distinct().Count() != shared.Length)
                throw Refused("A v2 shared slot must be acknowledged by every and only distinct catalog participant.");

            var bindings = shared.Select(static member => (RootMemberRecord.BundleAcknowledgedBinding)member.Member.Binding).ToArray();
            var first = bindings[0];
            var participantRoots = shared.Select(static member => member.Root.RootIdentity).ToHashSet();
            var participantDigest = ProtectionDigest.PackageProtectionParticipantSet(first.LogicalMemberId, participantRoots);
            if (!string.Equals(participantDigest, first.ParticipantSetDigest, StringComparison.Ordinal) ||
                bindings.Any(binding => binding.LogicalMemberId != first.LogicalMemberId ||
                    binding.PublicationId != first.PublicationId || binding.StateGeneration != first.StateGeneration ||
                    !string.Equals(binding.ParticipantSetDigest, first.ParticipantSetDigest, StringComparison.Ordinal) ||
                    !string.Equals(binding.StateBodyDigest, first.StateBodyDigest, StringComparison.Ordinal) ||
                    !string.Equals(binding.BundleDigest, first.BundleDigest, StringComparison.Ordinal) ||
                    binding.ObservedStateFileIdentity != first.ObservedStateFileIdentity))
                throw Refused("The catalog-derived v2 participant closure or common bundle acknowledgement is inconsistent.");

            foreach (var item in shared)
            {
                var binding = (RootMemberRecord.BundleAcknowledgedBinding)item.Member.Binding;
                var row = binding.RootRow;
                if (row.RootIdentity != item.Root.RootIdentity || row.EnrollmentEpoch != item.Root.EnrollmentEpoch ||
                    !string.Equals(row.MemberId, item.Member.MemberId, StringComparison.Ordinal) ||
                    row.StateGeneration != first.StateGeneration || row.StateBodyDigest != first.StateBodyDigest ||
                    row.LegacyUnknownRecovery || row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    !string.Equals(ProtectionDigest.PackageProtectionBundleRow(row), row.ProtectionDigest, StringComparison.Ordinal))
                    throw RefuseCatalog("A retained v2 row does not exactly acknowledge its catalog root, member, epoch, and known graph closure.",
                        item.Root.RootIdentity);
            }

            try
            {
                _ = new PackageProtectionBundle(PackageProtectionBundle.CurrentSchemaVersion, first.LogicalMemberId,
                    first.PublicationId, first.StateGeneration, first.StateBodyDigest,
                    bindings.Select(static binding => binding.RootRow), first.ParticipantSetDigest, first.BundleDigest);
            }
            catch (ArgumentException exception)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "The catalog-derived v2 acknowledgement rows do not form the exact common bundle.",
                    shared[0].Root.RootIdentity, exception);
            }

            result.Add(new CatalogSlotEvidence(group.Key, shared, true));
        }
        if (result.Where(static slot => slot.IsBundle)
            .GroupBy(static slot => ((RootMemberRecord.BundleAcknowledgedBinding)slot.Members[0].Member.Binding).LogicalMemberId)
            .Any(static group => group.Count() != 1))
            throw Refused("One logical v2 member cannot be acknowledged from multiple native state slots.");
        return result.ToArray();
    }

    private static void RequireCatalogV1BindingShape(
        RootMemberRecord.AcknowledgedBinding binding,
        NativeCatalogLockedRoot root,
        string memberId)
    {
        var record = binding.ProtectionRecord;
        if (record.RootIdentity != root.RootIdentity || record.EnrollmentEpoch != root.EnrollmentEpoch ||
            !string.Equals(record.MemberId, memberId, StringComparison.Ordinal) || record.Revision <= 0 ||
            record.LegacyUnknownRecovery || record.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            record.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            !string.Equals(ProtectionDigest.Protection(record), record.ProtectionDigest, StringComparison.Ordinal))
            throw RefuseCatalog("A v1 catalog acknowledgement has an invalid root/member/epoch, digest, or graph closure.",
                root.RootIdentity);
    }

    private static void RequireCatalogV2BindingShape(
        RootMemberRecord.BundleAcknowledgedBinding binding,
        NativeCatalogLockedRoot root,
        string memberId)
    {
        var row = binding.RootRow;
        if (binding.LogicalMemberId == Guid.Empty || binding.PublicationId == Guid.Empty || binding.StateGeneration <= 0 ||
            row.RootIdentity != root.RootIdentity || row.EnrollmentEpoch != root.EnrollmentEpoch ||
            !string.Equals(row.MemberId, memberId, StringComparison.Ordinal) || row.Revision <= 0 ||
            binding.StateGeneration != row.StateGeneration ||
            !string.Equals(binding.StateBodyDigest, row.StateBodyDigest, StringComparison.Ordinal) ||
            row.LegacyUnknownRecovery || row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            !string.Equals(ProtectionDigest.PackageProtectionBundleRow(row), row.ProtectionDigest, StringComparison.Ordinal))
            throw RefuseCatalog("A v2 catalog acknowledgement has an invalid header, root/member/epoch, digest, or graph closure.",
                root.RootIdentity);
    }

    private static void RequireCatalogV1Acknowledgement(
        StoreStateRecord state,
        RootMemberRecord.AcknowledgedBinding binding,
        NativeCatalogLockedRoot root)
    {
        var protection = state.ProtectionRecord;
        if (state.ProtectionBundle is not null || protection is null ||
            protection.RootIdentity != root.RootIdentity || protection.EnrollmentEpoch != root.EnrollmentEpoch ||
            !string.Equals(protection.MemberId, binding.ProtectionRecord.MemberId, StringComparison.Ordinal) ||
            protection.Revision != binding.ProtectionRecord.Revision || !protection.HasSamePayloadAs(binding.ProtectionRecord))
            throw RefuseCatalog("The schema-2 root's independent v1 sibling state does not match its exact acknowledgement.",
                root.RootIdentity);
    }

    private static void VerifyCatalogBundleState(CatalogSlotEvidence slot, StoreStateRecord state)
    {
        if (state.ProtectionRecord is not null || state.ProtectionBundle is not { } bundle)
            throw Refused("A v2 catalog participant slot must contain only one shared v2 protection bundle.");
        var first = (RootMemberRecord.BundleAcknowledgedBinding)slot.Members[0].Member.Binding;
        if (bundle.LogicalMemberId != first.LogicalMemberId || bundle.PublicationId != first.PublicationId ||
            bundle.StateGeneration != first.StateGeneration ||
            !string.Equals(bundle.ParticipantSetDigest, first.ParticipantSetDigest, StringComparison.Ordinal) ||
            !string.Equals(bundle.StateBodyDigest, first.StateBodyDigest, StringComparison.Ordinal) ||
            !string.Equals(bundle.BundleDigest, first.BundleDigest, StringComparison.Ordinal))
            throw Refused("The shared v2 payload header differs from the complete catalog-derived acknowledgement closure.");
    }

    private void ObserveCatalogBundleInstalls(
        NativeCatalogLockedRoot root,
        StoreStateRecord state,
        PersistedStoreStateBundleGraphVerifier.VerifiedStoreStateBundleGraphs graphs,
        ICollection<IDisposable> observations,
        ICollection<Action> revalidations,
        CancellationToken cancellationToken)
    {
        var allGraphs = graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs).ToArray();
        var installs = allGraphs.SelectMany(static graph => graph.Nodes).Select(static node => node.Install)
            .Where(install => install.Root == root.RootIdentity).Distinct().ToArray();
        var paths = new List<ActiveInstallPathEvidence>();
        foreach (var descriptor in state.ActivePackageDescriptorsByIdNormalized.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var associated = installs.Where(install =>
                string.Equals(install.PackageId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(install.Version, descriptor.Version, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (associated.Length == 0)
                continue;
            paths.Add(new ActiveInstallPathEvidence(descriptor, associated));
        }
        ObserveNativeInstallEvidence(root.PrimaryResolution.AuthorityRoot!, root.RootIdentity, installs, paths,
            root.Scope!, observations, revalidations, cancellationToken);
    }

    private static PackageStoreAdmissionException RefuseCatalog(string message, PhysicalRootIdentity? rootIdentity = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, rootIdentity);

    private sealed record CatalogMemberEvidence(
        NativeCatalogLockedRoot Root,
        RootMemberRecord Member,
        ResolvedMemberStateLocation Location);

    private sealed record CatalogSlotEvidence(
        StateSlotIdentity Slot,
        IReadOnlyList<CatalogMemberEvidence> Members,
        bool IsBundle);

    private sealed record VerifiedCatalogSlot(
        PersistedStoreStateGraphVerifier.VerifiedStoreStateGraphs? V1Graphs,
        PersistedStoreStateBundleGraphVerifier.VerifiedStoreStateBundleGraphs? V2Graphs);
}

/// <summary>Detached descriptive verification for one exact member in a retained native catalog.</summary>
internal sealed record NativeCatalogMemberVerification(
    PhysicalRootIdentity RootIdentity,
    long EnrollmentEpoch,
    string MemberId,
    StateSlotIdentity StateSlot,
    PhysicalFileIdentity StateFileIdentity,
    long Revision,
    PersistedStoreStateGraphVerifier.VerifiedStoreStateGraphs? V1Graphs,
    PersistedStoreStateBundleGraphVerifier.VerifiedStoreStateBundleGraphs? V2Graphs);
