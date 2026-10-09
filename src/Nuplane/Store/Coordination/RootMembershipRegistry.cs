using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Persists membership and protected state through held native directories under root-before-state ownership.</summary>
/// <remarks>
/// This internal publication seam requires independently resolved held root/member parents. It supplies
/// no configured-path authority discovery, graph-completeness validation, enrollment completion, or runtime admission.
/// Initial enrollment can publish a verified Incomplete declaration; completing enrollment and ordinary
/// authority discovery require separate graph and configured-path validation.
/// </remarks>
internal sealed partial class RootMembershipRegistry
{
    internal const string ControlDirectoryName = ".nuplane-store";
    internal const string LedgerName = "membership.json";
    internal const int MaximumStateBytes = 4 * 1024 * 1024;
    private const string PlaceholderDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStorePublicationFileSystem _publication;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPackageProtectionStatePayloadSerializer _stateSerializer;
    private readonly RootMembershipPayloadSerializer _ledgerSerializer = new();
    private readonly PhysicalStoreLock _locks;

    internal RootMembershipRegistry(IPhysicalStoreFileSystem files, IStoreStateSerializer stateSerializer)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(stateSerializer);
        _files = files;
        _publication = files as IPhysicalStorePublicationFileSystem ?? throw Refused("The provider lacks held-parent publication.");
        _names = files as IPhysicalStoreNameFileSystem ?? throw Refused("The provider lacks native name observations.");
        _stateSerializer = stateSerializer as IPackageProtectionStatePayloadSerializer ?? throw new PackageStoreAdmissionException(
            PackageStoreAdmissionReason.UnsupportedParticipant, "Protected publication requires a caller-stream protection serializer.");
        _locks = new PhysicalStoreLock(files);
    }

    /// <summary>Publishes one member's actual payload, retaining prior/next evidence until verified acknowledgement.</summary>
    internal async Task<RootMembershipRecord> PublishStateAsync(
        PhysicalStoreDirectoryHandle root,
        IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> memberParents,
        string memberId,
        StoreStateRecord nextState,
        CancellationToken cancellationToken,
        Action<RootMembershipPublicationPoint>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        await using var transaction = await OpenLockedAsync(root, cancellationToken).ConfigureAwait(false);
        var ledger = transaction.Ledger;
        if (ledger.PendingStateCommit is not null)
            throw Refused("Pending membership requires recovery before another state publication.");
        var member = FindMember(ledger, memberId);
        var parents = CopyParents(memberParents);
        var parent = FindParent(parents, memberId);
        await RequireAllPriorStatesAsync(ledger, parents, cancellationToken).ConfigureAwait(false);
        var nextProtection = nextState.ProtectionRecord ?? throw Refused("The next state must carry explicit protection.");
        RequireProtection(nextState, nextProtection);
        var pending = new PendingStateCommit(ledger.RootIdentity, ledger.EnrollmentEpoch, ledger.Status,
            ledger.LedgerDigest, Guid.NewGuid(), member, nextProtection);
        var nextBytes = await EncodeStateAsync(nextState, cancellationToken).ConfigureAwait(false);
        var nextReopened = await DecodeStateAsync(nextBytes, cancellationToken).ConfigureAwait(false);
        RequireExactProtection(nextReopened, nextProtection);
        var prior = await ReadPriorAsync(member.Binding, parent, cancellationToken).ConfigureAwait(false);
        var slot = GetSlot(member);
        var stagedName = StageName(pending);
        var backupName = BackupName(pending);
        RequireAbsent(parent, stagedName);
        RequireAbsent(parent, backupName);

        transaction.Publish(WithPending(ledger, pending));
        checkpoint?.Invoke(RootMembershipPublicationPoint.PendingPublished);
        cancellationToken.ThrowIfCancellationRequested();
        var stagedIdentity = CreateFile(parent, stagedName, nextBytes);
        await RequireArtifactAsync(parent, stagedName, stagedIdentity, nextProtection, cancellationToken).ConfigureAwait(false);
        RequireProfile(parent, stagedName, stagedIdentity, slot);
        checkpoint?.Invoke(RootMembershipPublicationPoint.StageFlushed);

        PhysicalFileIdentity? backupIdentity = null;
        if (prior is not null)
        {
            backupIdentity = CreateFile(parent, backupName, prior.Bytes);
            await RequirePriorArtifactAsync(parent, backupName, backupIdentity, member.Binding, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(RootMembershipPublicationPoint.BackupFlushed);
        }

        pending = new PendingStateCommit(ledger.RootIdentity, ledger.EnrollmentEpoch, ledger.Status,
            ledger.LedgerDigest, pending.PublicationId, member, nextProtection, stagedIdentity, backupIdentity);
        transaction.Publish(WithPending(ledger, pending));
        checkpoint?.Invoke(RootMembershipPublicationPoint.ArtifactsBound);
        cancellationToken.ThrowIfCancellationRequested();
        await ReadPriorAsync(member.Binding, parent, cancellationToken).ConfigureAwait(false);
        _publication.PublishControlFileAt(parent, stagedName, stagedIdentity, slot.CanonicalBasename, prior?.Identity);
        checkpoint?.Invoke(RootMembershipPublicationPoint.StatePublished);
        var actual = await ReadStateAsync(parent, slot, stagedIdentity, cancellationToken).ConfigureAwait(false);
        RequireExactProtection(actual.State, nextProtection);
        checkpoint?.Invoke(RootMembershipPublicationPoint.StateVerified);

        return await CompleteResolutionAsync(transaction, ledger, member, parents, parent, pending,
            next: true, cancellationToken, checkpoint).ConfigureAwait(false);
    }

    /// <summary>Reconciles exact prior/next evidence and resumes only durably selected artifact cleanup.</summary>
    internal async Task<RootMembershipRecord> RecoverAsync(
        PhysicalStoreDirectoryHandle root,
        IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> memberParents,
        CancellationToken cancellationToken,
        Action<RootMembershipPublicationPoint>? checkpoint = null)
    {
        await using var transaction = await OpenLockedAsync(root, cancellationToken).ConfigureAwait(false);
        var ledger = transaction.Ledger;
        var pending = ledger.PendingStateCommit ?? throw Refused("There is no pending state publication to recover.");
        var member = FindMember(ledger, pending.MemberId);
        var parents = CopyParents(memberParents);
        var parent = FindParent(parents, member.MemberId);
        var priorLedger = Rebuild(ledger, pending.PriorMembershipStatus, ledger.Members, pending: null);
        if (!string.Equals(priorLedger.LedgerDigest, pending.PriorLedgerDigest, StringComparison.Ordinal))
            throw Refused("Pending evidence does not reconstruct the exact prior membership ledger.");
        var current = _files.InspectChildNoFollow(parent, GetSlot(member).CanonicalBasename);
        var next = pending.Resolution switch
        {
            PendingStateCommitResolution.Next => true,
            PendingStateCommitResolution.Prior => false,
            _ => pending.StagedStateFileIdentity is not null && current?.Identity == pending.StagedStateFileIdentity
        };
        return await CompleteResolutionAsync(transaction, priorLedger, member, parents, parent, pending,
            next, cancellationToken, checkpoint).ConfigureAwait(false);
    }

    private async Task<RootMembershipRecord> CompleteResolutionAsync(Transaction transaction,
        RootMembershipRecord priorLedger, RootMemberRecord member,
        IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> parents,
        PhysicalStoreDirectoryHandle parent, PendingStateCommit pending, bool next,
        CancellationToken cancellationToken, Action<RootMembershipPublicationPoint>? checkpoint)
    {
        var resolved = await ValidateResolutionAsync(priorLedger, member, parents, parent, pending, next,
            allowMissingArtifacts: pending.Resolution != PendingStateCommitResolution.Unresolved, cancellationToken).ConfigureAwait(false);
        if (pending.Resolution == PendingStateCommitResolution.Unresolved)
        {
            pending = new PendingStateCommit(pending.RootIdentity, pending.EnrollmentEpoch, pending.PriorMembershipStatus,
                pending.PriorLedgerDigest, pending.PublicationId, member, pending.NextProtectionRecord,
                pending.StagedStateFileIdentity, pending.BackupStateFileIdentity,
                next ? PendingStateCommitResolution.Next : PendingStateCommitResolution.Prior);
            // Keep prior members and Incomplete status until bound artifact cleanup is durably resolved.
            transaction.Publish(WithPending(priorLedger, pending));
        }
        checkpoint?.Invoke(RootMembershipPublicationPoint.ResolutionPublished);
        cancellationToken.ThrowIfCancellationRequested();

        // Reopen all selected states and artifacts after the durable marker, before the first removal.
        await ValidateResolutionAsync(priorLedger, member, parents, parent, pending, next,
            allowMissingArtifacts: true, cancellationToken).ConfigureAwait(false);
        if (!next && pending.StagedStateFileIdentity is not null)
        {
            await RemoveArtifactAsync(parent, StageName(pending), pending.StagedStateFileIdentity, member.Binding,
                pending.NextProtectionRecord, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(RootMembershipPublicationPoint.StageRemoved);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (pending.BackupStateFileIdentity is not null)
        {
            // A prior-stage removal may have been interrupted; retain backup if selected state or a sibling changed.
            await ValidateResolutionAsync(priorLedger, member, parents, parent, pending, next,
                allowMissingArtifacts: true, cancellationToken).ConfigureAwait(false);
            await RemoveArtifactAsync(parent, BackupName(pending), pending.BackupStateFileIdentity, member.Binding,
                nextProtection: null, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(RootMembershipPublicationPoint.BackupRemoved);
            cancellationToken.ThrowIfCancellationRequested();
        }
        RequireAbsent(parent, StageName(pending));
        RequireAbsent(parent, BackupName(pending));
        checkpoint?.Invoke(RootMembershipPublicationPoint.ArtifactsRemoved);
        cancellationToken.ThrowIfCancellationRequested();
        // Complete can only be retained after every member still reopens correctly and cleanup is finished.
        await RequireAllPriorStatesAsync(resolved, parents, cancellationToken).ConfigureAwait(false);
        RequireAbsent(parent, StageName(pending));
        RequireAbsent(parent, BackupName(pending));
        transaction.Publish(resolved);
        checkpoint?.Invoke(RootMembershipPublicationPoint.Acknowledged);
        return resolved;
    }

    private async Task<RootMembershipRecord> ValidateResolutionAsync(RootMembershipRecord priorLedger,
        RootMemberRecord member, IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> parents,
        PhysicalStoreDirectoryHandle parent, PendingStateCommit pending, bool next,
        bool allowMissingArtifacts, CancellationToken cancellationToken)
    {
        var slot = GetSlot(member);
        RootMembershipRecord resolved;
        if (next)
        {
            var actual = await ReadStateAsync(parent, slot, pending.StagedStateFileIdentity
                ?? throw Refused("Next-state resolution requires a durably bound staged identity."), cancellationToken).ConfigureAwait(false);
            RequireExactProtection(actual.State, pending.NextProtectionRecord);
            RequireAbsent(parent, StageName(pending));
            resolved = Acknowledge(priorLedger, member, slot, actual.Identity, pending.NextProtectionRecord);
        }
        else
        {
            await ReadPriorAsync(member.Binding, parent, cancellationToken).ConfigureAwait(false);
            if (RequireCleanupArtifact(parent, StageName(pending), pending.StagedStateFileIdentity, allowMissingArtifacts))
            {
                await RequireArtifactAsync(parent, StageName(pending), pending.StagedStateFileIdentity!,
                    pending.NextProtectionRecord, cancellationToken).ConfigureAwait(false);
                RequireProfile(parent, StageName(pending), pending.StagedStateFileIdentity!, slot);
            }
            resolved = priorLedger;
        }
        if (RequireCleanupArtifact(parent, BackupName(pending), pending.BackupStateFileIdentity, allowMissingArtifacts))
            await RequirePriorArtifactAsync(parent, BackupName(pending), pending.BackupStateFileIdentity!,
                member.Binding, cancellationToken).ConfigureAwait(false);
        await RequireAllPriorStatesAsync(resolved, parents, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private bool RequireCleanupArtifact(PhysicalStoreDirectoryHandle parent, string name,
        PhysicalFileIdentity? expected, bool allowMissing)
    {
        var actual = _files.InspectChildNoFollow(parent, name);
        if (actual is null && (expected is null || allowMissing))
            return false;
        PhysicalStorePublicationChecks.RequireExpectedEntry(actual, expected);
        return actual is not null;
    }

    private async Task RemoveArtifactAsync(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expected,
        RootMemberRecord.MemberBinding prior, PackageProtectionRecord? nextProtection, CancellationToken cancellationToken)
    {
        if (!RequireCleanupArtifact(parent, name, expected, allowMissing: true))
            return;
        if (nextProtection is null)
            await RequirePriorArtifactAsync(parent, name, expected, prior, cancellationToken).ConfigureAwait(false);
        else
            await RequireArtifactAsync(parent, name, expected, nextProtection, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _publication.RemoveControlFileAt(parent, name, expected);
    }

    /// <summary>Reads a structurally verified ledger without granting admission or completeness authority.</summary>
    internal RootMembershipRecord ReadCandidate(PhysicalStoreDirectoryHandle root)
    {
        using var control = OpenControl(root);
        return ReadLedger(root, control);
    }

    private async Task<Transaction> OpenLockedAsync(PhysicalStoreDirectoryHandle root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        try
        {
            // This first payload supplies untrusted lock-order hints only. Authority is reread under ownership.
            var hints = ReadLedger(root, control);
            owner = await _locks.AcquireAsync(control, hints.Members.Select(GetSlot).ToArray(), cancellationToken).ConfigureAwait(false);
            var actual = ReadLedger(root, control, out var ledgerIdentity);
            if (!string.Equals(hints.LedgerDigest, actual.LedgerDigest, StringComparison.Ordinal))
                throw Refused("Membership changed while acquiring root/state ownership; retry from fresh evidence.");
            return new Transaction(this, root, control, owner, actual, ledgerIdentity);
        }
        catch
        {
            try
            {
                if (owner is not null)
                    await owner.DisposeAsync().ConfigureAwait(false);
            }
            finally { control.Dispose(); }
            throw;
        }
    }

    private PhysicalStoreDirectoryHandle OpenControl(PhysicalStoreDirectoryHandle root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var entry = _files.InspectChildNoFollow(root, ControlDirectoryName);
        if (entry?.Kind != PhysicalStoreEntryKind.Directory)
            throw Refused("The reserved control namespace is missing, linked, or not a directory.");
        var control = _files.OpenDirectoryChildNoFollow(root, ControlDirectoryName);
        try
        {
            PhysicalStorePublicationChecks.RequireParent(_files, control, entry.Identity);
            return control;
        }
        catch { control.Dispose(); throw; }
    }

    private RootMembershipRecord ReadLedger(PhysicalStoreDirectoryHandle root, PhysicalStoreDirectoryHandle control)
        => ReadLedger(root, control, out _);

    private RootMembershipRecord ReadLedger(PhysicalStoreDirectoryHandle root, PhysicalStoreDirectoryHandle control,
        out PhysicalFileIdentity ledgerIdentity, PhysicalFileIdentity? expectedIdentity = null)
    {
        var observed = ReadFile(control, LedgerName, expectedIdentity, MaximumStateBytes);
        ledgerIdentity = observed.Identity;
        var bytes = observed.Bytes;
        RootMembershipRecord ledger;
        try { ledger = _ledgerSerializer.Deserialize(bytes); }
        catch (JsonException exception)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The held membership payload is malformed or fails integrity validation.", innerException: exception);
        }
        RequireRoot(root, ledger.RootIdentity);
        var entry = _files.InspectChildNoFollow(root, ControlDirectoryName);
        if (entry?.Kind != PhysicalStoreEntryKind.Directory || entry.Identity != _files.InspectHandle(control).Identity)
            throw Refused("The reserved control directory no longer binds the held root.");
        return ledger;
    }

    private void RequireRoot(PhysicalStoreDirectoryHandle root, PhysicalRootIdentity expected)
        => PhysicalStorePublicationChecks.RequireParent(_files, root, expected.HandleIdentity);

    private async Task RequireAllPriorStatesAsync(RootMembershipRecord ledger,
        IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> parents, CancellationToken cancellationToken)
    {
        foreach (var member in ledger.Members)
            await ReadPriorAsync(member.Binding, FindParent(parents, member.MemberId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<StateObservation?> ReadPriorAsync(RootMemberRecord.MemberBinding binding,
        PhysicalStoreDirectoryHandle parent, CancellationToken cancellationToken)
    {
        if (binding is RootMemberRecord.ProspectiveBinding prospective)
        {
            PhysicalStorePublicationChecks.RequireParent(_files, parent, prospective.VerifiedParentIdentity);
            RequireAbsent(parent, prospective.RequestedBasename);
            return null;
        }

        var slot = GetSlot(binding);
        var expected = binding switch
        {
            RootMemberRecord.ExistingUnprotectedBinding prior => prior.ObservedStateFileIdentity,
            RootMemberRecord.AcknowledgedBinding prior => prior.ObservedStateFileIdentity,
            _ => throw Refused("A declared member has no verified state binding.")
        };
        var actual = await ReadStateAsync(parent, slot, expected, cancellationToken).ConfigureAwait(false);
        RequirePriorPayload(actual.State, binding);
        return actual;
    }

    private async Task<StateObservation> ReadStateAsync(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot,
        PhysicalFileIdentity expectedIdentity, CancellationToken cancellationToken)
    {
        PhysicalStorePublicationChecks.RequireParent(_files, parent, slot.ParentIdentity);
        var read = ReadFile(parent, slot.CanonicalBasename, expectedIdentity, MaximumStateBytes);
        var observed = new PhysicalStoreIdentity(_files).ObserveStateSlot(parent, slot.CanonicalBasename);
        if (observed.Slot != slot || observed.FileIdentity != read.Identity)
            throw Refused("State payload does not bind the unchanged canonical slot.");
        var state = await DecodeStateAsync(read.Bytes, cancellationToken).ConfigureAwait(false);
        return new StateObservation(read.Identity, read.Bytes, state);
    }

    private async Task RequirePriorArtifactAsync(PhysicalStoreDirectoryHandle parent, string name,
        PhysicalFileIdentity identity, RootMemberRecord.MemberBinding prior, CancellationToken cancellationToken)
    {
        var state = await DecodeStateAsync(ReadFile(parent, name, identity, MaximumStateBytes).Bytes, cancellationToken).ConfigureAwait(false);
        RequirePriorPayload(state, prior);
    }

    private async Task RequireArtifactAsync(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity,
        PackageProtectionRecord expectedProtection, CancellationToken cancellationToken)
    {
        var state = await DecodeStateAsync(ReadFile(parent, name, identity, MaximumStateBytes).Bytes, cancellationToken).ConfigureAwait(false);
        RequireExactProtection(state, expectedProtection);
    }

    private void RequireProfile(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity, StateSlotIdentity slot)
    {
        var observed = _names.ObserveCanonicalFileNameNoFollow(parent, name, identity);
        if (observed.ParentIdentity != slot.ParentIdentity || observed.Semantics != slot.NameSemantics ||
            !string.Equals(observed.Basename, name, StringComparison.Ordinal))
            throw Refused("The staged payload does not bind the prior parent and native name profile.");
    }

    private static void RequirePriorPayload(StoreStateRecord state, RootMemberRecord.MemberBinding prior)
    {
        if (prior is RootMemberRecord.ExistingUnprotectedBinding legacy)
        {
            if (state.ProtectionRecord is not null || !string.Equals(ProtectionDigest.StateBody(state), legacy.StateBodyDigest, StringComparison.Ordinal))
                throw Refused("The legacy prior payload changed or acquired unacknowledged protection.");
        }
        else if (prior is RootMemberRecord.AcknowledgedBinding acknowledged)
            RequireExactProtection(state, acknowledged.ProtectionRecord);
        else
            throw Refused("Only an existing prior binding has backup payloads.");
    }

    private static void RequireExactProtection(StoreStateRecord state, PackageProtectionRecord expected)
    {
        var protection = state.ProtectionRecord ?? throw Refused("The saved/reopened payload lost its protection metadata.");
        RequireProtection(state, protection);
        if (!protection.HasSamePayloadAs(expected))
            throw Refused("The saved/reopened payload does not match the exact expected protection.");
    }

    private static void RequireProtection(StoreStateRecord state, PackageProtectionRecord protection)
    {
        if (!string.Equals(ProtectionDigest.StateBody(state), protection.StateBodyDigest, StringComparison.Ordinal) ||
            !string.Equals(ProtectionDigest.Protection(protection), protection.ProtectionDigest, StringComparison.Ordinal))
            throw Refused("Actual state or protection content disagrees with its canonical digest.");
    }

    private async Task<byte[]> EncodeStateAsync(StoreStateRecord state, CancellationToken cancellationToken)
    {
        using var buffer = new BoundedControlPayloadStream(MaximumStateBytes);
        await _stateSerializer.WritePayloadAsync(buffer, state, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private async Task<StoreStateRecord> DecodeStateAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(bytes, writable: false);
        StoreStateRecord state;
        try { state = await _stateSerializer.ReadPayloadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The actual held state payload is malformed or unsupported.", innerException: exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return state ?? throw Refused("The state serializer returned no payload.");
    }

    private (PhysicalFileIdentity Identity, byte[] Bytes) ReadFile(PhysicalStoreDirectoryHandle parent,
        string name, PhysicalFileIdentity? expected, int maximumBytes)
    {
        var entry = _files.InspectChildNoFollow(parent, name) ?? throw Refused("A required control payload is absent.");
        PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expected ?? entry.Identity);
        using var file = _files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        var opened = _files.InspectHandle(file);
        PhysicalStorePublicationChecks.RequireExpectedEntry(opened, expected ?? entry.Identity);
        var bytes = _files.ReadControlFile(file, maximumBytes);
        PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, opened.Identity);
        return (opened.Identity, bytes);
    }

    private PhysicalFileIdentity CreateFile(PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
    {
        using var file = _files.CreateFileExclusiveAt(parent, name);
        _files.WriteNewControlFile(file, bytes);
        var identity = _files.InspectHandle(file).Identity;
        PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, identity);
        return identity;
    }

    private void RequireAbsent(PhysicalStoreDirectoryHandle parent, string name)
        => PhysicalStorePublicationChecks.RequireExpectedEntry(_files.InspectChildNoFollow(parent, name), expectedIdentity: null);

    private static RootMemberRecord FindMember(RootMembershipRecord ledger, string memberId)
        => ledger.Members.SingleOrDefault(member => string.Equals(member.MemberId, memberId, StringComparison.Ordinal))
           ?? throw Refused("The requested state member is not in the exact membership union.");

    private static Dictionary<string, PhysicalStoreDirectoryHandle> CopyParents(IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        return parents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static PhysicalStoreDirectoryHandle FindParent(IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> parents, string memberId)
        => parents.TryGetValue(memberId, out var parent) && parent is not null ? parent
            : throw Refused("Every ledger member requires an independently resolved held parent.");

    private static StateSlotIdentity GetSlot(RootMemberRecord member) => GetSlot(member.Binding);

    private static StateSlotIdentity GetSlot(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.ProspectiveBinding prospective => new StateSlotIdentity(prospective.VerifiedParentIdentity, prospective.NameSemantics, prospective.RequestedBasename),
        RootMemberRecord.ExistingUnprotectedBinding prior => prior.StateSlot,
        RootMemberRecord.AcknowledgedBinding prior => prior.StateSlot,
        _ => throw Refused("Publication requires every member to have a known existing or prospective state slot.")
    };

    internal static string StageName(PendingStateCommit pending) => $".nuplane-{pending.PublicationId:N}.tmp";
    internal static string BackupName(PendingStateCommit pending) => $".nuplane-{pending.PublicationId:N}.bak";

    private static RootMembershipRecord WithPending(RootMembershipRecord prior, PendingStateCommit pending)
        => Rebuild(prior, RootMembershipStatus.Incomplete, prior.Members, pending);

    private static RootMembershipRecord Acknowledge(RootMembershipRecord prior, RootMemberRecord member,
        StateSlotIdentity slot, PhysicalFileIdentity identity, PackageProtectionRecord protection)
        => Rebuild(prior, prior.Status, prior.Members.Select(existing => existing.MemberId == member.MemberId
            ? new RootMemberRecord(member.MemberId, member.ConfiguredLocator, new RootMemberRecord.AcknowledgedBinding(slot, identity, protection))
            : existing), pending: null);

    internal static RootMembershipRecord Rebuild(RootMembershipRecord source, RootMembershipStatus status,
        IEnumerable<RootMemberRecord> members, PendingStateCommit? pending)
    {
        var candidate = new RootMembershipRecord(source.SchemaVersion, source.RootIdentity, source.EnrollmentEpoch,
            status, members, source.TargetMemberIds, source.RetiredMembers, pending, PlaceholderDigest);
        return new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch,
            candidate.Status, candidate.Members, candidate.TargetMemberIds, candidate.RetiredMembers, pending, ProtectionDigest.Ledger(candidate));
    }

    private static PackageStoreAdmissionException Refused(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    private sealed record StateObservation(PhysicalFileIdentity Identity, byte[] Bytes, StoreStateRecord State);

    private sealed class Transaction(RootMembershipRegistry registry, PhysicalStoreDirectoryHandle root,
        PhysicalStoreDirectoryHandle control, IAsyncDisposable owner, RootMembershipRecord ledger,
        PhysicalFileIdentity ledgerIdentity) : IAsyncDisposable
    {
        private PhysicalFileIdentity _ledgerIdentity = ledgerIdentity;
        internal RootMembershipRecord Ledger { get; private set; } = ledger;

        internal RootMembershipRecord ReadCurrent()
        {
            var actual = registry.ReadLedger(root, control, out _, _ledgerIdentity);
            RequireSameDigest(Ledger, actual);
            return actual;
        }

        internal void Publish(RootMembershipRecord next)
        {
            ReadCurrent();
            var priorIdentity = _ledgerIdentity;
            var bytes = registry._ledgerSerializer.Serialize(next);
            var stage = $"membership-{Guid.NewGuid():N}.tmp";
            var stagedIdentity = registry.CreateFile(control, stage, bytes);
            var reopened = registry._ledgerSerializer.Deserialize(registry.ReadFile(control, stage, stagedIdentity, MaximumStateBytes).Bytes);
            if (!string.Equals(reopened.LedgerDigest, next.LedgerDigest, StringComparison.Ordinal))
                throw Refused("Staged membership content did not verify.");
            registry._publication.PublishControlFileAt(control, stage, stagedIdentity, LedgerName, priorIdentity);
            var actual = registry.ReadLedger(root, control, out var publishedIdentity, stagedIdentity);
            if (!string.Equals(actual.LedgerDigest, next.LedgerDigest, StringComparison.Ordinal))
                throw Refused("Published membership content did not verify.");
            Ledger = actual;
            _ledgerIdentity = publishedIdentity;
        }

        public async ValueTask DisposeAsync()
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            finally { control.Dispose(); }
        }
    }
}
