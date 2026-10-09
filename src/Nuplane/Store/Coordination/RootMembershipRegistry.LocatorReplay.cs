using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Runs a metadata replay callback while a structurally Complete root and all member locks are held.</summary>
    /// <remarks>
    /// Persisted slot bindings supply only existing lock-order hints. Every configured locator is replayed and
    /// compared with its bound slot after root and all member locks are held. This is not state verification or
    /// operation admission; callers must perform their own locked payload checks inside the callback.
    /// </remarks>
    internal async Task<TResult> WithCompleteMemberLocationsAsync<TResult>(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        Func<LockedMemberLocations, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        RequireEpoch(expectedEnrollmentEpoch);
        RequireRoot(root, expectedRoot);

        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        Transaction? transaction = null;
        MemberLocatorReplayScope? scope = null;
        LockedMemberLocations? context = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? preparedLocations = null;
        try
        {
            // The unlocked copy is used only to name existing member locks. Its digest and full shape are
            // rechecked after acquiring root plus every hinted member lock.
            var hints = ReadLedger(root, control);
            RequireCompleteLocatorLedger(hints, expectedRoot, expectedEnrollmentEpoch);
            var lockHints = hints.Members.Select(GetSlot).ToArray();
            owner = await _locks.AcquireAsync(control, lockHints, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var lockedLedger = ReadLedger(root, control, out var lockedLedgerIdentity);
            RequireSameDigest(hints, lockedLedger);
            RequireCompleteLocatorLedger(lockedLedger, expectedRoot, expectedEnrollmentEpoch);
            scope = new MemberLocatorReplayScope(lockedLedger);
            preparedLocations = ResolveMemberLocatorMap(lockedLedger, scope, requireAcknowledgedBindings: true);
            cancellationToken.ThrowIfCancellationRequested();

            var ownedTransaction = new Transaction(this, root, control, owner, lockedLedger, lockedLedgerIdentity);
            transaction = ownedTransaction;
            owner = null;
            context = new LockedMemberLocations(ownedTransaction.ReadCurrent, scope, preparedLocations);
            preparedLocations = null; // Context now owns the resolved parent handles.
            return await callback(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    context?.Dispose();
                    if (preparedLocations is not null)
                        DisposeLocations(preparedLocations.Values);
                }
                finally
                {
                    scope?.Expire();
                }
            }
            finally
            {
                try
                {
                    if (transaction is not null)
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    else if (owner is not null)
                        await owner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (transaction is null)
                        control.Dispose();
                }
            }
        }
    }

    /// <summary>Runs an explicitly quiescent all-Declared mapping callback under root and all member locks.</summary>
    /// <remarks>
    /// This is the only locator-replay path that provisions missing member lock files. It requires explicit
    /// operator confirmation, resolves every declared locator in the root-lock bootstrap callback, and reads no
    /// member payload before the full lock set and exact ledger digest have been revalidated.
    /// </remarks>
    internal async Task<TResult> WithQuiescentIncompleteMemberLocationsAsync<TResult>(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        bool quiescentCutoverConfirmed,
        Func<LockedMemberLocations, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        RequireEpoch(expectedEnrollmentEpoch);
        if (!quiescentCutoverConfirmed)
            throw Refused("Mapping an all-Declared Incomplete membership requires explicit quiescent cutover confirmation.");
        RequireRoot(root, expectedRoot);

        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        Transaction? transaction = null;
        MemberLocatorReplayScope? scope = null;
        LockedMemberLocations? context = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? preparedLocations = null;
        RootMembershipRecord? preparedLedger = null;
        try
        {
            owner = await _locks.AcquireBootstrapAsync(control, token =>
            {
                token.ThrowIfCancellationRequested();
                var ledger = ReadLedger(root, control);
                RequireAllDeclaredLocatorLedger(ledger, expectedRoot, expectedEnrollmentEpoch);
                scope = new MemberLocatorReplayScope(ledger);
                var locations = ResolveMemberLocatorMap(ledger, scope, requireAcknowledgedBindings: false);
                preparedLedger = ledger;
                preparedLocations = locations;
                return Task.FromResult<IReadOnlyList<StateSlotIdentity>>(
                    ledger.Members.Select(member => locations[member.MemberId].Slot).ToArray());
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var initialLedger = preparedLedger ?? throw Refused("Bootstrap did not capture the declared membership ledger.");
            var locationsUnderRoot = preparedLocations ?? throw Refused("Bootstrap did not resolve every declared member locator.");
            var lockedLedger = ReadLedger(root, control, out var lockedLedgerIdentity);
            RequireSameDigest(initialLedger, lockedLedger);
            RequireAllDeclaredLocatorLedger(lockedLedger, expectedRoot, expectedEnrollmentEpoch);
            scope!.RequireCandidate(expectedRoot, lockedLedger);
            RevalidateMemberLocationMap(lockedLedger, locationsUnderRoot, requireAcknowledgedBindings: false);
            cancellationToken.ThrowIfCancellationRequested();

            var ownedTransaction = new Transaction(this, root, control, owner, lockedLedger, lockedLedgerIdentity);
            transaction = ownedTransaction;
            owner = null;
            context = new LockedMemberLocations(ownedTransaction.ReadCurrent, scope, locationsUnderRoot);
            preparedLocations = null; // Context now owns the resolved parent handles.
            return await callback(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    context?.Dispose();
                    if (preparedLocations is not null)
                        DisposeLocations(preparedLocations.Values);
                }
                finally
                {
                    scope?.Expire();
                }
            }
            finally
            {
                try
                {
                    if (transaction is not null)
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    else if (owner is not null)
                        await owner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (transaction is null)
                        control.Dispose();
                }
            }
        }
    }

    private IReadOnlyDictionary<string, ResolvedMemberStateLocation> ResolveMemberLocatorMap(
        RootMembershipRecord ledger,
        MemberLocatorReplayScope scope,
        bool requireAcknowledgedBindings)
    {
        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        var targetIds = ledger.TargetMemberIds.ToHashSet(StringComparer.Ordinal);
        if (ledger.Members.Count == 0 || memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(targetIds))
            throw Refused("Every locator replay requires the exact non-empty member and target union.");

        var locations = new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal);
        var slots = new HashSet<StateSlotIdentity>();
        try
        {
            var resolver = new PackageStoreAuthorityResolver(_files, this);
            foreach (var member in ledger.Members)
            {
                scope.EnsureActive();
                var location = resolver.ResolveMemberStateLocation(member.ConfiguredLocator, scope);
                if (!slots.Add(location.Slot))
                {
                    location.Dispose();
                    throw Refused("Distinct member locators resolved to the same native state slot.");
                }
                if (!locations.TryAdd(member.MemberId, location))
                {
                    location.Dispose();
                    throw Refused("A member locator was resolved more than once.");
                }

                if (requireAcknowledgedBindings)
                {
                    var acknowledged = member.Binding as RootMemberRecord.AcknowledgedBinding
                        ?? throw Refused("Complete locator replay requires every member's acknowledged binding.");
                    if (location.Slot != acknowledged.StateSlot ||
                        location.ExistingFileIdentity != acknowledged.ObservedStateFileIdentity)
                    {
                        throw Refused("A configured member locator no longer resolves to its acknowledged native state slot.");
                    }
                }
                else if (member.Binding is not RootMemberRecord.DeclaredBinding)
                {
                    throw Refused("Quiescent locator replay is limited to an all-Declared Incomplete membership.");
                }
            }

            if (!locations.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(memberIds))
                throw Refused("Locator replay did not resolve the exact member set.");
            RevalidateMemberLocationMap(ledger, locations, requireAcknowledgedBindings);
            return new ReadOnlyDictionary<string, ResolvedMemberStateLocation>(locations);
        }
        catch
        {
            DisposeLocations(locations.Values);
            throw;
        }
    }

    private static void RevalidateMemberLocationMap(
        RootMembershipRecord ledger,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        bool requireAcknowledgedBindings)
    {
        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (!memberIds.SetEquals(locations.Keys))
            throw Refused("The retained locator map no longer contains the exact membership union.");

        var slots = new HashSet<StateSlotIdentity>();
        foreach (var member in ledger.Members)
        {
            if (!locations.TryGetValue(member.MemberId, out var location) || !slots.Add(location.Slot))
                throw Refused("The retained locator map contains a missing or duplicate native state slot.");
            location.Revalidate();
            if (requireAcknowledgedBindings)
            {
                var acknowledged = member.Binding as RootMemberRecord.AcknowledgedBinding
                    ?? throw Refused("Complete locator replay requires every member's acknowledged binding.");
                if (location.Slot != acknowledged.StateSlot ||
                    location.ExistingFileIdentity != acknowledged.ObservedStateFileIdentity)
                {
                    throw Refused("A member locator changed its acknowledged slot during map revalidation.");
                }
            }
            else if (member.Binding is not RootMemberRecord.DeclaredBinding)
            {
                throw Refused("Quiescent locator replay is limited to an all-Declared Incomplete membership.");
            }
        }
    }

    private static void DisposeLocations(IEnumerable<ResolvedMemberStateLocation> locations)
    {
        foreach (var location in locations.Reverse())
            location.Dispose();
    }

    private static void RequireEpoch(long expectedEnrollmentEpoch)
    {
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));
    }

    private static void RequireSameDigest(RootMembershipRecord before, RootMembershipRecord after)
    {
        if (!string.Equals(before.LedgerDigest, after.LedgerDigest, StringComparison.Ordinal))
            throw Refused("The membership ledger changed while acquiring the complete lock set.");
    }

    private static void RequireCompleteLocatorLedger(
        RootMembershipRecord ledger,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch)
    {
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEnrollmentEpoch ||
            ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
            ledger.Members.Count == 0 || ledger.RetiredMembers.Any(evidence =>
                ledger.Members.Any(member => string.Equals(member.MemberId, evidence.MemberId, StringComparison.Ordinal))))
        {
            throw Refused("Complete locator replay requires the expected root and epoch with no pending commit.");
        }

        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(ledger.TargetMemberIds) ||
            ledger.Members.Any(member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
        {
            throw Refused("Complete locator replay requires every retained member to be one acknowledged target.");
        }
    }

    private static void RequireAllDeclaredLocatorLedger(
        RootMembershipRecord ledger,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch)
    {
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEnrollmentEpoch ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.RetiredMembers.Count != 0 || ledger.Members.Count == 0 ||
            ledger.Members.Any(member => member.Binding is not RootMemberRecord.DeclaredBinding))
        {
            throw Refused("Quiescent locator replay requires the expected all-Declared, non-pending Incomplete ledger.");
        }

        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(ledger.TargetMemberIds))
            throw Refused("Quiescent locator replay requires the exact target and member union.");
    }

    /// <summary>Short-lived metadata scope for one exact ledger during locked member-locator replay.</summary>
    internal sealed class MemberLocatorReplayScope
    {
        private int _active = 1;

        internal MemberLocatorReplayScope(RootMembershipRecord ledger)
        {
            RootIdentity = ledger.RootIdentity;
            EnrollmentEpoch = ledger.EnrollmentEpoch;
            Status = ledger.Status;
            LedgerDigest = ledger.LedgerDigest;
        }

        internal PhysicalRootIdentity RootIdentity { get; }
        internal long EnrollmentEpoch { get; }
        internal RootMembershipStatus Status { get; }
        internal string LedgerDigest { get; }

        internal void EnsureActive()
        {
            if (Volatile.Read(ref _active) == 0)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                    "The root-locked member-locator replay scope has expired.", RootIdentity);
            }
        }

        internal void RequireCandidate(PhysicalRootIdentity observedRoot, RootMembershipRecord candidate)
        {
            EnsureActive();
            if (observedRoot != RootIdentity || candidate.RootIdentity != RootIdentity ||
                candidate.EnrollmentEpoch != EnrollmentEpoch || candidate.Status != Status ||
                candidate.PendingStateCommit is not null ||
                !string.Equals(candidate.LedgerDigest, LedgerDigest, StringComparison.Ordinal))
            {
                throw Refused("A configured member locator encountered a different or changing membership authority.");
            }
        }

        internal void Expire() => Interlocked.Exchange(ref _active, 0);
    }

    /// <summary>Exposes held metadata and transaction lifetime only during one root/member-lock callback.</summary>
    internal sealed class LockedMemberLocations : IDisposable
    {
        private readonly Func<RootMembershipRecord> _getLedger;
        private readonly MemberLocatorReplayScope _scope;
        private readonly IReadOnlyDictionary<string, ResolvedMemberStateLocation> _locations;
        private bool _disposed;

        internal LockedMemberLocations(
            Func<RootMembershipRecord> getLedger,
            MemberLocatorReplayScope scope,
            IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations)
        {
            ArgumentNullException.ThrowIfNull(getLedger);
            _getLedger = getLedger;
            _scope = scope;
            _locations = locations;
        }

        internal RootMembershipRecord Ledger
        {
            get
            {
                EnsureActive();
                return _getLedger();
            }
        }

        internal IReadOnlyDictionary<string, ResolvedMemberStateLocation> Locations
        {
            get
            {
                EnsureActive();
                return _locations;
            }
        }

        /// <summary>Rechecks the exact scoped ledger and all retained native locations.</summary>
        internal void Revalidate()
        {
            EnsureActive();
            var ledger = _getLedger();
            if (_scope.Status == RootMembershipStatus.Complete)
                RequireCompleteLocatorLedger(ledger, _scope.RootIdentity, _scope.EnrollmentEpoch);
            else
                RequireAllDeclaredLocatorLedger(ledger, _scope.RootIdentity, _scope.EnrollmentEpoch);
            RevalidateMemberLocationMap(ledger, _locations, requireAcknowledgedBindings: _scope.Status == RootMembershipStatus.Complete);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeLocations(_locations.Values);
        }

        private void EnsureActive()
        {
            _scope.EnsureActive();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ledger = _getLedger();
            _scope.RequireCandidate(ledger.RootIdentity, ledger);
        }
    }
}
