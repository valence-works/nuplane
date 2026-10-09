using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Binds every member of an all-Declared Incomplete ledger to its verified current state slot.</summary>
    /// <remarks>
    /// This is an explicit quiescent migration step. It reads no member state until root and all member locks
    /// are held, publishes the whole bound union as Incomplete, and grants no graph-completeness or admission
    /// authority. Existing protected payloads require their separate verified acknowledgement path.
    /// </remarks>
    internal async Task<RootMembershipRecord> BindDeclaredMembersAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        IReadOnlyList<RootMemberRecord> expectedDeclarations,
        IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> memberLocations,
        bool quiescentCutoverConfirmed,
        CancellationToken cancellationToken,
        Action<RootMembershipBindingPoint>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(expectedDeclarations);
        ArgumentNullException.ThrowIfNull(memberLocations);
        cancellationToken.ThrowIfCancellationRequested();
        if (!quiescentCutoverConfirmed)
            throw Refused("Binding declared members requires explicit confirmation that the cutover remains quiescent.");
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));

        // Copy all caller-owned collections and records before the first await.
        var declarations = expectedDeclarations.Select(member =>
            member?.Copy() ?? throw Refused("Expected declarations cannot contain null members.")).ToArray();
        var declarationById = new Dictionary<string, RootMemberRecord>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (declaration.Binding is not RootMemberRecord.DeclaredBinding ||
                !declarationById.TryAdd(declaration.MemberId, declaration))
            {
                throw Refused("Expected declarations must be a unique set of Declared members.");
            }
        }
        if (declarationById.Count == 0)
            throw Refused("Binding requires the complete non-empty declared member set.");

        var locations = new Dictionary<string, MemberLocation>(StringComparer.Ordinal);
        foreach (var pair in memberLocations)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value.Parent is null ||
                !locations.TryAdd(pair.Key, new MemberLocation(pair.Value.Parent, pair.Value.RequestedBasename)))
            {
                throw Refused("Every member location must have one non-null held parent and exact basename.");
            }

            PhysicalStoreNames.ValidateSingleComponent(pair.Value.RequestedBasename);
        }
        if (!declarationById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(locations.Keys))
            throw Refused("Expected declarations and held member locations must contain the exact same member IDs.");

        RequireRoot(root, expectedRoot);
        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        var transactionOwnsHandles = false;
        try
        {
            RootMembershipRecord? callbackLedger = null;
            Dictionary<string, CapturedMemberSlot>? capturedSlots = null;
            owner = await _locks.AcquireBootstrapAsync(control, token =>
            {
                token.ThrowIfCancellationRequested();
                var ledger = ReadLedger(root, control);
                RequireDeclaredLedger(ledger, expectedRoot, expectedEnrollmentEpoch, declarationById, locations);

                var identity = new PhysicalStoreIdentity(_files);
                var resolved = new Dictionary<string, CapturedMemberSlot>(StringComparer.Ordinal);
                var slots = new List<StateSlotIdentity>(ledger.Members.Count);
                foreach (var member in ledger.Members)
                {
                    token.ThrowIfCancellationRequested();
                    var location = locations[member.MemberId];
                    var captured = ObserveDeclaredMemberSlot(identity, member.MemberId, location);
                    resolved.Add(member.MemberId, captured);
                    slots.Add(captured.Slot);
                }

                // Establish one stable metadata snapshot before the lock provider validates and provisions slots.
                RevalidateCapturedMap(identity, resolved.Values);
                callbackLedger = ledger;
                capturedSlots = resolved;
                checkpoint?.Invoke(RootMembershipBindingPoint.SlotsResolvedUnderRoot);
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StateSlotIdentity>>(slots);
            }, cancellationToken).ConfigureAwait(false);

            checkpoint?.Invoke(RootMembershipBindingPoint.AllMemberLocksAcquired);
            cancellationToken.ThrowIfCancellationRequested();
            var initialLedger = callbackLedger ?? throw Refused("Bootstrap did not capture the declared ledger.");
            var resolvedSlots = capturedSlots ?? throw Refused("Bootstrap did not capture every declared state slot.");

            // This exact digest check is the boundary before any member-state payload can be read.
            var lockedLedger = ReadLedger(root, control);
            if (!string.Equals(initialLedger.LedgerDigest, lockedLedger.LedgerDigest, StringComparison.Ordinal))
                throw Refused("The declared membership ledger changed while acquiring the complete lock set.");
            RequireDeclaredLedger(lockedLedger, expectedRoot, expectedEnrollmentEpoch, declarationById, locations);

            var identityUnderLock = new PhysicalStoreIdentity(_files);
            RevalidateCapturedMap(identityUnderLock, resolvedSlots.Values);
            cancellationToken.ThrowIfCancellationRequested();

            var bindings = new Dictionary<string, RootMemberRecord.MemberBinding>(StringComparer.Ordinal);
            foreach (var member in lockedLedger.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captured = resolvedSlots[member.MemberId];
                if (captured.ExistingFileIdentity is not { } existingFileIdentity)
                {
                    RevalidateCapturedSlot(identityUnderLock, captured);
                    bindings.Add(member.MemberId, new RootMemberRecord.ProspectiveBinding(
                        captured.Slot.ParentIdentity, captured.Slot.NameSemantics, captured.RequestedBasename));
                    continue;
                }

                var observed = await ReadStateAsync(captured.Parent, captured.Slot, existingFileIdentity,
                    cancellationToken).ConfigureAwait(false);
                if (observed.State.ProtectionRecord is not null)
                    throw Refused("An existing protected payload cannot be adopted by declared-member binding.");

                bindings.Add(member.MemberId, new RootMemberRecord.ExistingUnprotectedBinding(
                    captured.Slot, observed.Identity, ProtectionDigest.StateBody(observed.State),
                    protectionMetadataAbsent: true));
                RevalidateCapturedSlot(identityUnderLock, captured);
            }

            // Revalidate the entire member map after the final payload read and before publishing any binding.
            RevalidateCapturedMap(identityUnderLock, resolvedSlots.Values);
            cancellationToken.ThrowIfCancellationRequested();

            var boundMembers = new List<RootMemberRecord>(lockedLedger.Members.Count);
            foreach (var member in lockedLedger.Members)
            {
                if (!bindings.TryGetValue(member.MemberId, out var binding))
                    throw Refused("Binding did not prepare every declared member.");
                boundMembers.Add(new RootMemberRecord(member.MemberId, member.ConfiguredLocator, binding));
            }
            var boundLedger = Rebuild(lockedLedger, RootMembershipStatus.Incomplete, boundMembers, pending: null);
            RequireBoundUnion(boundLedger, declarationById, locations);
            checkpoint?.Invoke(RootMembershipBindingPoint.BindingsPrepared);
            cancellationToken.ThrowIfCancellationRequested();

            var transactionOwner = owner ?? throw Refused("Bootstrap did not return the complete lock owner.");
            var transaction = new Transaction(this, root, control, transactionOwner, lockedLedger);
            owner = null;
            transactionOwnsHandles = true;
            await using var ownedTransaction = transaction;
            transaction.Publish(boundLedger);
            checkpoint?.Invoke(RootMembershipBindingPoint.BoundLedgerPublished);
            return transaction.Ledger;
        }
        finally
        {
            try
            {
                if (owner is not null)
                    await owner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!transactionOwnsHandles)
                    control.Dispose();
            }
        }
    }

    private void RequireDeclaredLedger(
        RootMembershipRecord ledger,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        IReadOnlyDictionary<string, RootMemberRecord> declarations,
        IReadOnlyDictionary<string, MemberLocation> locations)
    {
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEnrollmentEpoch ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.RetiredMembers.Count != 0 || ledger.Members.Count == 0 ||
            ledger.Members.Any(member => member.Binding is not RootMemberRecord.DeclaredBinding))
        {
            throw Refused("Binding requires the expected root and epoch in an all-Declared, non-pending Incomplete ledger.");
        }

        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var targetIds = ledger.TargetMemberIds.ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(targetIds) ||
            !memberIds.SetEquals(declarations.Keys) || !memberIds.SetEquals(locations.Keys))
        {
            throw Refused("Ledger members, target members, expected declarations, and held locations must be exact sets.");
        }

        foreach (var member in ledger.Members)
        {
            if (!declarations.TryGetValue(member.MemberId, out var declaration) ||
                !string.Equals(member.ConfiguredLocator, declaration.ConfiguredLocator, StringComparison.Ordinal))
            {
                throw Refused("The configured locator for a declared member differs from the explicit expected declaration.");
            }
        }
    }

    private CapturedMemberSlot ObserveDeclaredMemberSlot(
        PhysicalStoreIdentity identity,
        string memberId,
        MemberLocation location)
    {
        var parentBefore = _files.InspectHandle(location.Parent);
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Refused("A declared member location requires a live held directory parent.");

        var entry = _files.InspectChildNoFollow(location.Parent, location.RequestedBasename);
        if (entry is not null)
        {
            var observation = identity.ObserveStateSlot(location.Parent, location.RequestedBasename);
            if (entry.Identity != observation.FileIdentity || observation.Slot.ParentIdentity != parentBefore.Identity)
                throw Refused("An existing declared state slot changed during native metadata observation.");

            // The requested spelling may be an alias. Persist and read through the native canonical slot.
            return new CapturedMemberSlot(memberId, location.Parent, location.RequestedBasename,
                observation.Slot, observation.FileIdentity);
        }

        var semantics = _names.ObserveDirectoryNameSemantics(location.Parent);
        PhysicalStorePublicationChecks.RequireParent(_files, location.Parent, parentBefore.Identity);
        RequireAbsent(location.Parent, location.RequestedBasename);
        var slot = new StateSlotIdentity(parentBefore.Identity, semantics, location.RequestedBasename);
        return new CapturedMemberSlot(memberId, location.Parent, location.RequestedBasename, slot,
            ExistingFileIdentity: null);
    }

    private void RevalidateCapturedMap(
        PhysicalStoreIdentity identity,
        IEnumerable<CapturedMemberSlot> slots)
    {
        foreach (var slot in slots)
            RevalidateCapturedSlot(identity, slot);
    }

    private void RevalidateCapturedSlot(PhysicalStoreIdentity identity, CapturedMemberSlot captured)
    {
        PhysicalStorePublicationChecks.RequireParent(_files, captured.Parent, captured.Slot.ParentIdentity);
        if (captured.ExistingFileIdentity is { } expectedFileIdentity)
        {
            var observation = identity.ObserveStateSlot(captured.Parent, captured.RequestedBasename);
            if (observation.Slot != captured.Slot || observation.FileIdentity != expectedFileIdentity)
                throw Refused("An existing state slot changed after declared-member resolution.");
            return;
        }

        var currentSemantics = _names.ObserveDirectoryNameSemantics(captured.Parent);
        if (currentSemantics != captured.Slot.NameSemantics)
            throw Refused("An absent state slot's native parent-name profile changed after resolution.");
        RequireAbsent(captured.Parent, captured.RequestedBasename);
        PhysicalStorePublicationChecks.RequireParent(_files, captured.Parent, captured.Slot.ParentIdentity);
        RequireAbsent(captured.Parent, captured.RequestedBasename);
    }

    private static void RequireBoundUnion(
        RootMembershipRecord ledger,
        IReadOnlyDictionary<string, RootMemberRecord> declarations,
        IReadOnlyDictionary<string, MemberLocation> locations)
    {
        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            !memberIds.SetEquals(ledger.TargetMemberIds) || !memberIds.SetEquals(declarations.Keys) ||
            !memberIds.SetEquals(locations.Keys) ||
            ledger.Members.Any(member => member.Binding is not RootMemberRecord.ProspectiveBinding &&
                member.Binding is not RootMemberRecord.ExistingUnprotectedBinding))
        {
            throw Refused("The prepared binding must retain every target in one fully bound Incomplete union.");
        }

        foreach (var member in ledger.Members)
        {
            if (!string.Equals(member.ConfiguredLocator, declarations[member.MemberId].ConfiguredLocator, StringComparison.Ordinal))
                throw Refused("The prepared binding changed a declared member's configured locator association.");
        }
    }

    private sealed record MemberLocation(PhysicalStoreDirectoryHandle Parent, string RequestedBasename);

    private sealed record CapturedMemberSlot(
        string MemberId,
        PhysicalStoreDirectoryHandle Parent,
        string RequestedBasename,
        StateSlotIdentity Slot,
        PhysicalFileIdentity? ExistingFileIdentity);
}
