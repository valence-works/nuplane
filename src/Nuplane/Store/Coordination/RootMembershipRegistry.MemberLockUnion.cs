using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Describes the exact path-observed root authority to retain in a multiroot lock union.</summary>
    internal sealed record CompleteMemberLocationsRequest(
        PhysicalStoreDirectoryHandle RootHandle,
        PhysicalRootIdentity RootIdentity,
        long EnrollmentEpoch,
        string LedgerDigest,
        PhysicalFileIdentity LedgerIdentity);

    /// <summary>
    /// Acquires every Complete root lock before deriving and acquiring any member lock, then returns one
    /// root-specific owner per participant sharing a single native lock lifetime.
    /// </summary>
    internal async Task<IReadOnlyList<CompleteMemberLocationsOwner>> AcquireCompleteMemberLocationsUnionAsync(
        IReadOnlyList<CompleteMemberLocationsRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0)
            throw new ArgumentException("A Complete member-location union requires at least one root.", nameof(requests));

        var orderedRequests = requests.ToArray();
        foreach (var request in orderedRequests)
        {
            if (request is null)
                throw new ArgumentException("Complete member-location requests cannot contain null entries.", nameof(requests));
            ArgumentNullException.ThrowIfNull(request.RootHandle);
            ArgumentNullException.ThrowIfNull(request.RootIdentity);
            ArgumentNullException.ThrowIfNull(request.LedgerIdentity);
        }
        Array.Sort(orderedRequests, CompleteMemberLocationsRequestComparer.Instance);
        for (var index = 1; index < orderedRequests.Length; index++)
        {
            if (orderedRequests[index - 1].RootIdentity == orderedRequests[index].RootIdentity)
                throw Refused("A Complete member-location union cannot merge duplicate physical-root observations.");
        }

        var participants = new List<UnionRootParticipant>(orderedRequests.Length);
        var owners = new List<CompleteMemberLocationsOwner>(orderedRequests.Length);
        PhysicalStoreLock.PhysicalStoreLockUnionOwner? unionOwner = null;
        var ownershipTransferred = false;
        Exception? admissionError = null;
        try
        {
            // Phase one: retain every canonical root lock. Do not acquire any member lock in this loop.
            foreach (var request in orderedRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(request.RootHandle);
                ArgumentNullException.ThrowIfNull(request.RootIdentity);
                ArgumentNullException.ThrowIfNull(request.LedgerIdentity);
                if (string.IsNullOrWhiteSpace(request.LedgerDigest))
                    throw new ArgumentException("A retained ledger digest is required.", nameof(requests));
                RequireEpoch(request.EnrollmentEpoch);
                RequireRoot(request.RootHandle, request.RootIdentity);

                var control = OpenControl(request.RootHandle);
                PhysicalStoreLock.RootLockScope? rootScope = null;
                try
                {
                    var observedLedger = ReadLedger(request.RootHandle, control, out var observedIdentity);
                    RequireCompleteLocatorLedger(observedLedger, request.RootIdentity, request.EnrollmentEpoch);
                    RequireExpectedLedgerObservation(observedLedger, observedIdentity,
                        request.LedgerDigest, request.LedgerIdentity);

                    rootScope = await _locks.AcquireRootLockAsync(control, request.RootIdentity, cancellationToken)
                        .ConfigureAwait(false);
                    control = null!; // The root scope now owns this control handle through the group lifetime.
                    participants.Add(new UnionRootParticipant(request, rootScope, rootScope.ControlDirectory,
                        observedLedger, observedIdentity));
                    rootScope = null;
                }
                finally
                {
                    if (rootScope is not null)
                        await rootScope.DisposeAsync().ConfigureAwait(false);
                    else if (control is not null)
                        control.Dispose();
                }
            }

            // Phase two: root locks are all held. Re-read authoritative ledgers and derive the complete
            // root-local member-lock hints from these locked copies, never from the path observations.
            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                participant.RootScope.VerifyCanonical();
                var authoritative = ReadLedger(participant.Request.RootHandle, participant.ControlDirectory,
                    out var authoritativeIdentity);
                RequireCompleteLocatorLedger(authoritative, participant.Request.RootIdentity,
                    participant.Request.EnrollmentEpoch);
                RequireExpectedLedgerObservation(authoritative, authoritativeIdentity,
                    participant.Request.LedgerDigest, participant.Request.LedgerIdentity);
                RequireSameDigest(participant.InitialLedger, authoritative);
                if (participant.InitialLedgerIdentity != authoritativeIdentity)
                    throw Refused("A membership ledger file identity changed while acquiring the complete root-lock set.");

                participant.LockedLedger = authoritative;
                participant.LockedLedgerIdentity = authoritativeIdentity;
            }

            var rootScopes = participants.Select(static participant => participant.RootScope).ToArray();
            var memberRequests = participants.Select(static participant =>
                new PhysicalStoreLock.RootMemberLockRequest(
                    participant.RootScope,
                    participant.LockedLedger!.Members.Select(GetSlot).ToArray())).ToArray();
            unionOwner = await _locks.AcquireMemberLockUnionAsync(rootScopes, memberRequests, cancellationToken)
                .ConfigureAwait(false);

            // Phase three: after every root-local member lock is held, compare all ledgers and native ledger
            // identities again. Locator replay and member payload reads happen only after this barrier.
            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadLedger(participant.Request.RootHandle, participant.ControlDirectory,
                    out var currentIdentity);
                RequireCompleteLocatorLedger(current, participant.Request.RootIdentity,
                    participant.Request.EnrollmentEpoch);
                RequireExpectedLedgerObservation(current, currentIdentity,
                    participant.Request.LedgerDigest, participant.Request.LedgerIdentity);
                RequireSameDigest(participant.LockedLedger!, current);
                if (participant.LockedLedgerIdentity != currentIdentity)
                    throw Refused("A membership ledger file identity changed after the complete root/member lock set was acquired.");

                participant.LockedLedger = current;
                participant.LockedLedgerIdentity = currentIdentity;
            }

            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = new MemberLocatorReplayScope(participant.LockedLedger!);
                participant.Scope = scope;
                participant.PreparedLocations = ResolveMemberLocatorMap(
                    participant.LockedLedger!, scope, LocatorReplayBindingPolicy.Acknowledged);
            }

            foreach (var participant in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var share = unionOwner.CreateShare();
                participant.PendingShare = share;
                var transaction = new Transaction(this, participant.Request.RootHandle,
                    participant.ControlDirectory, share, participant.LockedLedger!,
                    participant.LockedLedgerIdentity!, ownsControlDirectory: false);
                participant.Transaction = transaction;
                participant.PendingShare = null;

                var context = CreateLockedMemberLocations(transaction, participant.Request.RootHandle,
                    participant.Scope!, participant.PreparedLocations!, LocatorReplayBindingPolicy.Acknowledged,
                    participant.Request.RootIdentity, participant.Request.EnrollmentEpoch);
                participant.Context = context;
                participant.PreparedLocations = null; // The context now owns resolved parent handles.

                owners.Add(new CompleteMemberLocationsOwner(participant.Scope!, context,
                    () => transaction.LedgerIdentity, () => transaction.DisposeAsync()));
                participant.Scope = null;
                participant.Context = null;
                participant.Transaction = null;
            }

            await unionOwner.DisposeAsync().ConfigureAwait(false); // Drop the coordinator reference; root shares retain the group.
            unionOwner = null;
            ownershipTransferred = true;
            return owners;
        }
        catch (Exception exception)
        {
            admissionError = exception;
            throw;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                var cleanupErrors = new List<Exception>();
                for (var index = owners.Count - 1; index >= 0; index--)
                {
                    try { await owners[index].DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }

                for (var index = participants.Count - 1; index >= 0; index--)
                {
                    var participant = participants[index];
                    try { participant.Context?.Dispose(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                    try
                    {
                        if (participant.PreparedLocations is not null)
                            DisposeLocations(participant.PreparedLocations.Values);
                    }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                    try { participant.Scope?.Expire(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                    if (participant.Transaction is not null)
                    {
                        try { await participant.Transaction.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                    else if (participant.PendingShare is not null)
                    {
                        try { await participant.PendingShare.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                }

                if (unionOwner is not null)
                {
                    try { await unionOwner.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }
                else if (participants.Count > 0)
                {
                    // Before transfer into a union, each root scope still owns its control and root lock.
                    for (var index = participants.Count - 1; index >= 0; index--)
                    {
                        try { await participants[index].RootScope.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                }

                if (cleanupErrors.Count > 0)
                {
                    var failures = admissionError is null
                        ? cleanupErrors
                        : new[] { admissionError }.Concat(cleanupErrors).ToList();
                    throw new AggregateException(
                        "Complete member-location union failed and one or more retained resources did not release cleanly.",
                        failures);
                }
            }
        }
    }

    private sealed class UnionRootParticipant(
        CompleteMemberLocationsRequest request,
        PhysicalStoreLock.RootLockScope rootScope,
        PhysicalStoreDirectoryHandle controlDirectory,
        RootMembershipRecord initialLedger,
        PhysicalFileIdentity initialLedgerIdentity)
    {
        internal CompleteMemberLocationsRequest Request { get; } = request;
        internal PhysicalStoreLock.RootLockScope RootScope { get; } = rootScope;
        internal PhysicalStoreDirectoryHandle ControlDirectory { get; } = controlDirectory;
        internal RootMembershipRecord InitialLedger { get; } = initialLedger;
        internal PhysicalFileIdentity InitialLedgerIdentity { get; } = initialLedgerIdentity;
        internal RootMembershipRecord? LockedLedger { get; set; }
        internal PhysicalFileIdentity? LockedLedgerIdentity { get; set; }
        internal MemberLocatorReplayScope? Scope { get; set; }
        internal IReadOnlyDictionary<string, ResolvedMemberStateLocation>? PreparedLocations { get; set; }
        internal Transaction? Transaction { get; set; }
        internal LockedMemberLocations? Context { get; set; }
        internal IAsyncDisposable? PendingShare { get; set; }
    }

    private sealed class CompleteMemberLocationsRequestComparer : IComparer<CompleteMemberLocationsRequest>
    {
        internal static CompleteMemberLocationsRequestComparer Instance { get; } = new();

        public int Compare(CompleteMemberLocationsRequest? left, CompleteMemberLocationsRequest? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return PhysicalRootIdentityComparer.Instance.Compare(left.RootIdentity, right.RootIdentity);
        }
    }
}
