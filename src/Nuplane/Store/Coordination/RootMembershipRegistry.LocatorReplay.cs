using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    internal enum LocatorReplayBindingPolicy
    {
        Acknowledged,
        Declared,
        BoundIncomplete
    }

    /// <summary>Runs a scoped member-state callback while a structurally Complete root and all member locks are held.</summary>
    /// <remarks>
    /// Persisted slot bindings supply only existing lock-order hints. Every configured locator is replayed and
    /// compared with its bound slot after root and all member locks are held. The scoped reader validates exact
    /// bound payloads; the scoped writer uses the same owner. Neither operation establishes semantic completeness
    /// or package-operation admission.
    /// </remarks>
    internal async Task<TResult> WithCompleteMemberLocationsAsync<TResult>(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        Func<LockedMemberLocations, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await using var locked = await AcquireCompleteMemberLocationsAsync(root, expectedRoot,
            expectedEnrollmentEpoch, expectedLedgerDigest: null, expectedLedgerIdentity: null, cancellationToken)
            .ConfigureAwait(false);
        return await callback(locked.Context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires and retains the exact Complete root/member lock set for one admitted operation.</summary>
    /// <remarks>Expected digest and file identity bind the earlier path observation to the locked reread.</remarks>
    internal async Task<CompleteMemberLocationsOwner> AcquireCompleteMemberLocationsAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        string? expectedLedgerDigest,
        PhysicalFileIdentity? expectedLedgerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        RequireEpoch(expectedEnrollmentEpoch);
        RequireRoot(root, expectedRoot);

        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        Transaction? transaction = null;
        MemberLocatorReplayScope? scope = null;
        LockedMemberLocations? context = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? preparedLocations = null;
        var ownershipTransferred = false;
        try
        {
            // This copy names only existing lock files. Its digest and native ledger identity are checked again
            // after acquiring the root and every member lock, before any member payload can be read.
            var hints = ReadLedger(root, control, out var hintsIdentity);
            RequireCompleteLocatorLedger(hints, expectedRoot, expectedEnrollmentEpoch);
            RequireExpectedLedgerObservation(hints, hintsIdentity, expectedLedgerDigest, expectedLedgerIdentity);
            var lockHints = hints.Members.Select(GetSlot).ToArray();
            owner = await _locks.AcquireAsync(control, lockHints, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var lockedLedger = ReadLedger(root, control, out var lockedLedgerIdentity);
            RequireSameDigest(hints, lockedLedger);
            RequireExpectedLedgerObservation(lockedLedger, lockedLedgerIdentity, expectedLedgerDigest, expectedLedgerIdentity);
            if (hintsIdentity != lockedLedgerIdentity)
                throw Refused("The membership ledger file identity changed while acquiring all locks.");
            RequireCompleteLocatorLedger(lockedLedger, expectedRoot, expectedEnrollmentEpoch);
            scope = new MemberLocatorReplayScope(lockedLedger);
            preparedLocations = ResolveMemberLocatorMap(lockedLedger, scope, LocatorReplayBindingPolicy.Acknowledged);
            cancellationToken.ThrowIfCancellationRequested();

            transaction = new Transaction(this, root, control, owner, lockedLedger, lockedLedgerIdentity);
            owner = null;
            context = CreateLockedMemberLocations(transaction, root, scope, preparedLocations,
                LocatorReplayBindingPolicy.Acknowledged, expectedRoot, expectedEnrollmentEpoch);
            preparedLocations = null; // Context now owns the resolved parent handles.
            var result = new CompleteMemberLocationsOwner(
                scope ?? throw new InvalidOperationException("The locator scope was not created."),
                context ?? throw new InvalidOperationException("The locked member context was not created."),
                () => (transaction ?? throw new InvalidOperationException("The locked transaction was not created.")).LedgerIdentity,
                () => (transaction ?? throw new InvalidOperationException("The locked transaction was not created.")).DisposeAsync());
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
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
    }

    private static void RequireExpectedLedgerObservation(
        RootMembershipRecord ledger,
        PhysicalFileIdentity ledgerIdentity,
        string? expectedDigest,
        PhysicalFileIdentity? expectedIdentity)
    {
        if ((expectedDigest is not null && !string.Equals(ledger.LedgerDigest, expectedDigest, StringComparison.Ordinal)) ||
            (expectedIdentity is not null && ledgerIdentity != expectedIdentity))
            throw Refused("The Complete membership ledger no longer matches the retained path observation.");
    }

    /// <summary>Retains the registry transaction and locator scope until the operation owner drains.</summary>
    internal sealed class CompleteMemberLocationsOwner : IAsyncDisposable
    {
        private readonly MemberLocatorReplayScope _scope;
        private readonly Func<PhysicalFileIdentity> _getLedgerIdentity;
        private readonly Func<ValueTask> _disposeTransaction;
        private readonly object _disposeGate = new();
        private Task? _disposeTask;

        internal CompleteMemberLocationsOwner(
            MemberLocatorReplayScope scope,
            LockedMemberLocations context,
            Func<PhysicalFileIdentity> getLedgerIdentity,
            Func<ValueTask> disposeTransaction)
        {
            _scope = scope;
            Context = context;
            _getLedgerIdentity = getLedgerIdentity;
            _disposeTransaction = disposeTransaction;
        }

        internal LockedMemberLocations Context { get; }
        internal RootMembershipRecord Ledger => Context.Ledger;
        internal PhysicalFileIdentity LedgerIdentity => _getLedgerIdentity();

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            var errors = new List<Exception>(2);
            try { Context.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }

            _scope.Expire();
            try { await _disposeTransaction().ConfigureAwait(false); }
            catch (Exception exception) { errors.Add(exception); }

            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Complete member-location ownership cleanup encountered multiple failures.", errors);
        }
    }

    /// <summary>Runs state operations for a bound Incomplete membership under root and every existing member lock.</summary>
    /// <remarks>
    /// This explicitly quiescent path never provisions locks. It requires a fully bound, non-pending Incomplete
    /// member/target union and replays every persisted locator before exposing the scoped state reader/writer.
    /// </remarks>
    internal async Task<TResult> WithQuiescentBoundIncompleteMemberLocationsAsync<TResult>(
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
            throw Refused("Bound Incomplete member operations require explicit quiescent cutover confirmation.");
        RequireRoot(root, expectedRoot);

        var control = OpenControl(root);
        IAsyncDisposable? owner = null;
        Transaction? transaction = null;
        MemberLocatorReplayScope? scope = null;
        LockedMemberLocations? context = null;
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? preparedLocations = null;
        try
        {
            // Bound slots name lock files only. The digest and configured paths are replayed after all locks are held.
            var hints = ReadLedger(root, control);
            RequireBoundIncompleteLocatorLedger(hints, expectedRoot, expectedEnrollmentEpoch);
            var lockHints = hints.Members.Select(GetSlot).ToArray();
            owner = await _locks.AcquireAsync(control, lockHints, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var lockedLedger = ReadLedger(root, control, out var lockedLedgerIdentity);
            RequireSameDigest(hints, lockedLedger);
            RequireBoundIncompleteLocatorLedger(lockedLedger, expectedRoot, expectedEnrollmentEpoch);
            scope = new MemberLocatorReplayScope(lockedLedger);
            preparedLocations = ResolveMemberLocatorMap(lockedLedger, scope, LocatorReplayBindingPolicy.BoundIncomplete);
            cancellationToken.ThrowIfCancellationRequested();

            var ownedTransaction = new Transaction(this, root, control, owner, lockedLedger, lockedLedgerIdentity);
            transaction = ownedTransaction;
            owner = null;
            context = CreateLockedMemberLocations(ownedTransaction, root, scope, preparedLocations,
                LocatorReplayBindingPolicy.BoundIncomplete, expectedRoot, expectedEnrollmentEpoch);
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
                var locations = ResolveMemberLocatorMap(ledger, scope, LocatorReplayBindingPolicy.Declared);
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
            RevalidateMemberLocationMap(lockedLedger, locationsUnderRoot, LocatorReplayBindingPolicy.Declared);
            cancellationToken.ThrowIfCancellationRequested();

            var ownedTransaction = new Transaction(this, root, control, owner, lockedLedger, lockedLedgerIdentity);
            transaction = ownedTransaction;
            owner = null;
            context = CreateLockedMemberLocations(ownedTransaction, root, scope, locationsUnderRoot,
                LocatorReplayBindingPolicy.Declared, expectedRoot, expectedEnrollmentEpoch);
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
        LocatorReplayBindingPolicy policy)
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

                RequireLocationMatchesBinding(member, location, policy);
            }

            if (!locations.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(memberIds))
                throw Refused("Locator replay did not resolve the exact member set.");
            RevalidateMemberLocationMap(ledger, locations, policy);
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
        LocatorReplayBindingPolicy policy)
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
            RequireLocationMatchesBinding(member, location, policy);
        }
    }

    private static void RequireLocationMatchesBinding(RootMemberRecord member,
        ResolvedMemberStateLocation location, LocatorReplayBindingPolicy policy)
    {
        switch (policy)
        {
            case LocatorReplayBindingPolicy.Acknowledged:
            {
                var acknowledged = member.Binding as RootMemberRecord.AcknowledgedBinding
                    ?? throw Refused("Complete locator replay requires every member's acknowledged binding.");
                if (location.Slot != acknowledged.StateSlot ||
                    location.ExistingFileIdentity != acknowledged.ObservedStateFileIdentity)
                    throw Refused("A configured member locator no longer resolves to its acknowledged native state slot.");
                return;
            }
            case LocatorReplayBindingPolicy.Declared:
                if (member.Binding is not RootMemberRecord.DeclaredBinding)
                    throw Refused("Quiescent bootstrap locator replay is limited to an all-Declared Incomplete membership.");
                return;
            case LocatorReplayBindingPolicy.BoundIncomplete:
            {
                if (member.Binding is RootMemberRecord.DeclaredBinding)
                    throw Refused("Bound Incomplete locator replay cannot contain an unbound declaration.");
                var expectedSlot = GetSlot(member.Binding);
                var expectedIdentity = member.Binding switch
                {
                    RootMemberRecord.ProspectiveBinding => null,
                    RootMemberRecord.ExistingUnprotectedBinding existing => existing.ObservedStateFileIdentity,
                    RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
                    _ => throw Refused("Bound Incomplete locator replay encountered an unsupported member binding.")
                };
                if (location.Slot != expectedSlot || location.ExistingFileIdentity != expectedIdentity)
                    throw Refused("A configured member locator no longer resolves to its bound Incomplete native state slot.");
                return;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
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

    private static void RequireBoundIncompleteLocatorLedger(
        RootMembershipRecord ledger,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch)
    {
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEnrollmentEpoch ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.Members.Count == 0 || ledger.Members.Any(member => member.Binding is RootMemberRecord.DeclaredBinding))
        {
            throw Refused("Quiescent bound locator replay requires the expected non-pending, fully bound Incomplete ledger.");
        }

        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(ledger.TargetMemberIds))
            throw Refused("Quiescent bound locator replay requires the exact target and member union.");
        if (ledger.Members.Any(member => member.Binding is not (RootMemberRecord.ProspectiveBinding or
                RootMemberRecord.ExistingUnprotectedBinding or RootMemberRecord.AcknowledgedBinding)))
        {
            throw Refused("Quiescent bound locator replay encountered an unsupported member binding.");
        }
    }

    private LockedMemberLocations CreateLockedMemberLocations(
        Transaction transaction,
        PhysicalStoreDirectoryHandle root,
        MemberLocatorReplayScope scope,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        LocatorReplayBindingPolicy policy,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch)
    {
        return new LockedMemberLocations(
            _files,
            root,
            () => transaction.ReadCurrent(),
            scope,
            locations,
            policy,
            _stateSerializer,
            (memberId, parent, token) => ReadMemberStateAsync(transaction, memberId, parent, token),
            (memberId, nextState, parents, token, checkpoint) =>
                PublishStateAsync(transaction, parents, memberId, nextState, token, checkpoint),
            ledger =>
            {
                var current = transaction.ReadCurrent();
                RequireSameDigest(ledger, current);
                RequireLocatorPolicyLedger(current, expectedRoot, expectedEnrollmentEpoch, policy);
                var nextScope = new MemberLocatorReplayScope(current);
                try
                {
                    var nextLocations = ResolveMemberLocatorMap(current, nextScope, policy);
                    return (nextScope, nextLocations);
                }
                catch
                {
                    nextScope.Expire();
                    throw;
                }
            },
            (currentScope, currentLocations, path) => BindConfiguredStateFile(currentScope, currentLocations, path),
            (currentScope, currentLocations, token) => VerifyLockedMemberStatesAsync(
                transaction, root, currentScope, currentLocations, expectedRoot, expectedEnrollmentEpoch, policy, token),
            (currentScope, currentLocations, memberId, priorState, nextState, token) => VerifyCoordinatedCandidateAsync(
                transaction, root, currentScope, currentLocations, memberId, priorState, nextState,
                expectedRoot, expectedEnrollmentEpoch, policy, token));
    }

    private LockedMemberLocations.ConfiguredMemberBinding BindConfiguredStateFile(
        MemberLocatorReplayScope scope,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        string configuredPath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(locations);
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw Refused("Coordinated state access requires one configured state-file locator.");
        scope.EnsureActive();
        var candidate = new PackageStoreAuthorityResolver(_files, this)
            .ResolveMemberStateLocation(configuredPath, scope);
        var transferred = false;
        try
        {
            candidate.Revalidate();
            if (candidate.ExistingFileIdentity is not { } expectedFileIdentity)
                throw Refused("A coordinated state-file locator must resolve to an existing acknowledged state file.");

            var matches = locations.Where(pair => pair.Value.Slot == candidate.Slot &&
                    pair.Value.ExistingFileIdentity == expectedFileIdentity)
                .Select(static pair => pair.Key)
                .ToArray();
            if (matches.Length != 1)
                throw Refused("The configured state-file locator does not identify exactly one acknowledged member slot.");

            locations[matches[0]].Revalidate();
            candidate.Revalidate();
            scope.EnsureActive();
            var result = new LockedMemberLocations.ConfiguredMemberBinding(matches[0], candidate);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
                candidate.Dispose();
        }
    }

    private async Task<IReadOnlyDictionary<string, StoreStateRecord>> VerifyLockedMemberStatesAsync(
        Transaction transaction,
        PhysicalStoreDirectoryHandle root,
        MemberLocatorReplayScope scope,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        LocatorReplayBindingPolicy policy,
        CancellationToken cancellationToken)
    {
        scope.EnsureActive();
        if (policy != LocatorReplayBindingPolicy.Acknowledged)
            throw Refused("Coordinated state access requires an acknowledged member union.");
        var ledger = transaction.ReadCurrent();
        scope.RequireCandidate(ledger.RootIdentity, ledger);
        RequireLocatorPolicyLedger(ledger, expectedRoot, expectedEnrollmentEpoch, policy);
        RevalidateMemberLocationMap(ledger, locations, policy);

        void RevalidateCurrentMap()
        {
            var current = transaction.ReadCurrent();
            scope.RequireCandidate(current.RootIdentity, current);
            RequireSameDigest(ledger, current);
            RevalidateMemberLocationMap(current, locations, policy);
        }

        async Task<StoreStateRecord?> ReadCurrentState(string memberId, CancellationToken token)
        {
            RevalidateCurrentMap();
            var location = locations.TryGetValue(memberId, out var found)
                ? found
                : throw Refused("A requested member state is outside the current locked location map.");
            var state = await ReadMemberStateAsync(transaction, memberId, location.Parent, token).ConfigureAwait(false);
            RevalidateCurrentMap();
            return state;
        }

        return await VerifyAllMemberProtectionCoreAsync(ledger, root, expectedRoot, expectedEnrollmentEpoch,
            RootMembershipStatus.Complete, ReadCurrentState, RevalidateCurrentMap, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifyCoordinatedCandidateAsync(
        Transaction transaction,
        PhysicalStoreDirectoryHandle root,
        MemberLocatorReplayScope scope,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        string memberId,
        StoreStateRecord priorState,
        StoreStateRecord nextState,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        LocatorReplayBindingPolicy policy,
        CancellationToken cancellationToken)
    {
        var ledger = transaction.ReadCurrent();
        scope.RequireCandidate(ledger.RootIdentity, ledger);
        var priorStates = await VerifyLockedMemberStatesAsync(transaction, root, scope, locations,
            expectedRoot, expectedEnrollmentEpoch, policy, cancellationToken).ConfigureAwait(false);
        if (!priorStates.TryGetValue(memberId, out var currentPrior) ||
            !currentPrior.ProtectionRecord!.HasSamePayloadAs(priorState.ProtectionRecord!))
        {
            throw Refused("The state used to form the coordinated candidate is no longer the exact verified prior member state.");
        }

        var member = FindMember(ledger, memberId);
        if (member.Binding is not RootMemberRecord.AcknowledgedBinding acknowledged)
            throw Refused("Coordinated publication requires an acknowledged member binding.");
        long expectedRevision;
        try { expectedRevision = checked(acknowledged.ProtectionRecord.Revision + 1); }
        catch (OverflowException exception)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The coordinated state protection revision cannot advance.", expectedRoot, exception);
        }
        var protection = nextState.ProtectionRecord
            ?? throw Refused("A coordinated publication candidate must carry explicit protection.");
        if (protection.RootIdentity != expectedRoot || protection.EnrollmentEpoch != expectedEnrollmentEpoch ||
            !string.Equals(protection.MemberId, memberId, StringComparison.Ordinal) ||
            protection.Revision != expectedRevision)
        {
            throw Refused("The coordinated candidate must name the exact root, epoch, member and next protection revision.");
        }

        var graphs = PersistedStoreStateGraphVerifier.Verify(nextState);
        var candidateScope = new MemberLocatorReplayScope(ledger);
        var observations = new List<IDisposable>();
        var revalidations = new List<Action>();
        try
        {
            ObserveProtectedInstalls(root, expectedRoot, nextState,
                graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs), candidateScope,
                observations, revalidations);
            RevalidateCandidateLedger(transaction, ledger, scope, locations, policy);
            foreach (var revalidate in revalidations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                revalidate();
            }
            RevalidateCandidateLedger(transaction, ledger, scope, locations, policy);
        }
        finally
        {
            try
            {
                for (var index = observations.Count - 1; index >= 0; index--)
                    observations[index].Dispose();
            }
            finally { candidateScope.Expire(); }
        }
    }

    private static void RevalidateCandidateLedger(
        Transaction transaction,
        RootMembershipRecord expectedLedger,
        MemberLocatorReplayScope scope,
        IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
        LocatorReplayBindingPolicy policy)
    {
        var current = transaction.ReadCurrent();
        scope.RequireCandidate(current.RootIdentity, current);
        RequireSameDigest(expectedLedger, current);
        RevalidateMemberLocationMap(current, locations, policy);
    }

    private static void RequireLocatorPolicyLedger(RootMembershipRecord ledger,
        PhysicalRootIdentity expectedRoot, long expectedEnrollmentEpoch, LocatorReplayBindingPolicy policy)
    {
        switch (policy)
        {
            case LocatorReplayBindingPolicy.Acknowledged:
                RequireCompleteLocatorLedger(ledger, expectedRoot, expectedEnrollmentEpoch);
                break;
            case LocatorReplayBindingPolicy.Declared:
                RequireAllDeclaredLocatorLedger(ledger, expectedRoot, expectedEnrollmentEpoch);
                break;
            case LocatorReplayBindingPolicy.BoundIncomplete:
                RequireBoundIncompleteLocatorLedger(ledger, expectedRoot, expectedEnrollmentEpoch);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
        }
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
        private readonly IPhysicalStoreFileSystem _files;
        private readonly PhysicalStoreDirectoryHandle _root;
        private readonly Func<RootMembershipRecord> _getLedger;
        private readonly LocatorReplayBindingPolicy _policy;
        private readonly IPackageProtectionStatePayloadSerializer _stateSerializer;
        private readonly Func<string, PhysicalStoreDirectoryHandle, CancellationToken, Task<StoreStateRecord?>> _readState;
        private readonly Func<string, StoreStateRecord, IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle>,
            CancellationToken, Action<RootMembershipPublicationPoint>?, Task<RootMembershipRecord>> _publishState;
        private readonly Func<RootMembershipRecord,
            (MemberLocatorReplayScope Scope, IReadOnlyDictionary<string, ResolvedMemberStateLocation> Locations)> _refresh;
        private readonly Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>, string,
            ConfiguredMemberBinding> _bindConfiguredStateFile;
        private readonly Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>,
            CancellationToken, Task<IReadOnlyDictionary<string, StoreStateRecord>>> _verifyCurrentStates;
        private readonly Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>, string,
            StoreStateRecord, StoreStateRecord, CancellationToken, Task> _verifyCandidate;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private MemberLocatorReplayScope _scope;
        private IReadOnlyDictionary<string, ResolvedMemberStateLocation> _locations;
        private bool _disposed;

        internal LockedMemberLocations(
            IPhysicalStoreFileSystem files,
            PhysicalStoreDirectoryHandle root,
            Func<RootMembershipRecord> getLedger,
            MemberLocatorReplayScope scope,
            IReadOnlyDictionary<string, ResolvedMemberStateLocation> locations,
            LocatorReplayBindingPolicy policy,
            IPackageProtectionStatePayloadSerializer stateSerializer,
            Func<string, PhysicalStoreDirectoryHandle, CancellationToken, Task<StoreStateRecord?>> readState,
            Func<string, StoreStateRecord, IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle>,
                CancellationToken, Action<RootMembershipPublicationPoint>?, Task<RootMembershipRecord>> publishState,
            Func<RootMembershipRecord,
                (MemberLocatorReplayScope Scope, IReadOnlyDictionary<string, ResolvedMemberStateLocation> Locations)> refresh,
            Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>, string,
                ConfiguredMemberBinding> bindConfiguredStateFile,
            Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>,
                CancellationToken, Task<IReadOnlyDictionary<string, StoreStateRecord>>> verifyCurrentStates,
            Func<MemberLocatorReplayScope, IReadOnlyDictionary<string, ResolvedMemberStateLocation>, string,
                StoreStateRecord, StoreStateRecord, CancellationToken, Task> verifyCandidate)
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(getLedger);
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(locations);
            ArgumentNullException.ThrowIfNull(stateSerializer);
            ArgumentNullException.ThrowIfNull(readState);
            ArgumentNullException.ThrowIfNull(publishState);
            ArgumentNullException.ThrowIfNull(refresh);
            ArgumentNullException.ThrowIfNull(bindConfiguredStateFile);
            ArgumentNullException.ThrowIfNull(verifyCurrentStates);
            ArgumentNullException.ThrowIfNull(verifyCandidate);
            _files = files;
            _root = root;
            _getLedger = getLedger;
            _scope = scope;
            _locations = locations;
            _policy = policy;
            _stateSerializer = stateSerializer;
            _readState = readState;
            _publishState = publishState;
            _refresh = refresh;
            _bindConfiguredStateFile = bindConfiguredStateFile;
            _verifyCurrentStates = verifyCurrentStates;
            _verifyCandidate = verifyCandidate;
        }

        internal RootMembershipRecord Ledger
        {
            get
            {
                EnsureActive();
                return _getLedger();
            }
        }

        /// <summary>Requires coordinated registry access to use this context's exact payload serializer instance.</summary>
        internal void RequirePayloadSerializer(IStoreStateSerializer serializer)
        {
            EnsureActive();
            if (!ReferenceEquals(serializer, _stateSerializer))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "The coordinated registry must use the exact payload serializer held by the locked membership context.");
        }

        /// <summary>Retains and revalidates the configured native state path through one coordinated operation.</summary>
        internal sealed class ConfiguredMemberBinding : IDisposable
        {
            private readonly ResolvedMemberStateLocation _location;
            private bool _disposed;

            internal ConfiguredMemberBinding(string memberId, ResolvedMemberStateLocation location)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
                ArgumentNullException.ThrowIfNull(location);
                MemberId = memberId;
                _location = location;
            }

            internal string MemberId { get; }

            internal void Revalidate()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _location.Revalidate();
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _location.Dispose();
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
            EnsureValidMap();
        }

        /// <summary>Uses the held root under serialized, fully verified Complete membership.</summary>
        /// <remarks>
        /// The operation state retains ownership while this callback and its replay run. Callers must
        /// not retain the supplied handle, reenter this member context, or return ownership that they
        /// cannot clean up if final verification refuses. This method acquires no native lock or path.
        /// </remarks>
        internal async Task<TResult> WithValidatedRootAsync<TResult>(
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, CancellationToken, Task<TResult>> callback,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(callback);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureValidMap();
                RequireAcknowledgedPolicy();
                await _verifyCurrentStates(_scope, _locations, cancellationToken).ConfigureAwait(false);
                EnsureValidMap();

                TResult result;
                try
                {
                    result = await callback(_files, _root, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Cancellation or a failed callback cannot skip the native/semantic replay.
                    EnsureValidMap();
                    await _verifyCurrentStates(_scope, _locations, CancellationToken.None).ConfigureAwait(false);
                    EnsureValidMap();
                    throw;
                }

                EnsureValidMap();
                await _verifyCurrentStates(_scope, _locations, CancellationToken.None).ConfigureAwait(false);
                EnsureValidMap();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>Reads and verifies one member state while the complete scoped location map remains locked.</summary>
        internal async Task<StoreStateRecord?> ReadMemberStateAsync(string memberId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureValidMap();
                if (!_locations.TryGetValue(memberId, out var location))
                    throw Refused("The requested state member is not in the exact locked membership union.");
                var state = await _readState(memberId, location.Parent, cancellationToken).ConfigureAwait(false);
                EnsureValidMap();
                return state;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>Reads one exactly bound configured state slot after full current-state and native verification.</summary>
        internal async Task<StoreStateRecord> ReadConfiguredStateAsync(string configuredPath, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureValidMap();
                RequireAcknowledgedPolicy();
                using var binding = _bindConfiguredStateFile(_scope, _locations, configuredPath);
                var states = await _verifyCurrentStates(_scope, _locations, cancellationToken).ConfigureAwait(false);
                EnsureValidMap();
                binding.Revalidate();
                return states.TryGetValue(binding.MemberId, out var state)
                    ? state
                    : throw Refused("The configured state-file binding is absent from the fully verified member union.");
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>Builds and publishes one complete next state after verifying the full old and candidate graph unions.</summary>
        internal async Task<StoreStateRecord> MutateConfiguredStateAsync(
            string configuredPath,
            Func<StoreStateRecord, StoreStateRecord> createNextState,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(createNextState);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureValidMap();
                RequireAcknowledgedPolicy();
                using var binding = _bindConfiguredStateFile(_scope, _locations, configuredPath);
                var states = await _verifyCurrentStates(_scope, _locations, cancellationToken).ConfigureAwait(false);
                EnsureValidMap();
                binding.Revalidate();
                if (!states.TryGetValue(binding.MemberId, out var priorState))
                    throw Refused("The configured state-file binding is absent from the fully verified member union.");

                var nextState = createNextState(priorState)
                    ?? throw Refused("A coordinated state mutation did not provide a complete next state.");
                await _verifyCandidate(_scope, _locations, binding.MemberId, priorState, nextState, cancellationToken)
                    .ConfigureAwait(false);
                EnsureValidMap();
                binding.Revalidate();
                await PublishStateUnderGateAsync(binding.MemberId, nextState, cancellationToken, checkpoint: null)
                    .ConfigureAwait(false);
                return nextState;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        /// <summary>Publishes one member state through this existing owner and refreshes every locator before reuse.</summary>
        internal async Task<RootMembershipRecord> PublishStateAsync(
            string memberId,
            StoreStateRecord nextState,
            CancellationToken cancellationToken,
            Action<RootMembershipPublicationPoint>? checkpoint = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
            ArgumentNullException.ThrowIfNull(nextState);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await PublishStateUnderGateAsync(memberId, nextState, cancellationToken, checkpoint)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private async Task<RootMembershipRecord> PublishStateUnderGateAsync(
            string memberId,
            StoreStateRecord nextState,
            CancellationToken cancellationToken,
            Action<RootMembershipPublicationPoint>? checkpoint)
        {
            EnsureValidMap();
            if (_policy == LocatorReplayBindingPolicy.Declared)
                throw Refused("An all-Declared locator context has no verified state binding to publish.");
            var priorScope = _scope;
            var priorLocations = _locations;
            var parents = priorLocations.ToDictionary(pair => pair.Key, pair => pair.Value.Parent, StringComparer.Ordinal);

            // Once publication begins, no caller can use any location resolved against the old ledger digest.
            priorScope.Expire();
            MemberLocatorReplayScope? refreshedScope = null;
            IReadOnlyDictionary<string, ResolvedMemberStateLocation>? refreshedLocations = null;
            try
            {
                var published = await _publishState(memberId, nextState, parents, cancellationToken, checkpoint)
                    .ConfigureAwait(false);
                var refreshed = _refresh(published);
                refreshedScope = refreshed.Scope;
                refreshedLocations = refreshed.Locations;
                DisposeLocations(priorLocations.Values);
                _locations = refreshed.Locations;
                _scope = refreshed.Scope;
                return published;
            }
            catch
            {
                priorScope.Expire();
                refreshedScope?.Expire();
                if (refreshedLocations is not null)
                    DisposeLocations(refreshedLocations.Values);
                DisposeLocations(priorLocations.Values);
                _locations = new ReadOnlyDictionary<string, ResolvedMemberStateLocation>(
                    new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal));
                throw;
            }
        }

        private void RequireAcknowledgedPolicy()
        {
            EnsureActive();
            if (_policy != LocatorReplayBindingPolicy.Acknowledged)
                throw Refused("Coordinated store state access requires a Complete acknowledged member union.");
        }

        public void Dispose()
        {
            _operationGate.Wait();
            try
            {
                if (_disposed)
                    return;
                _disposed = true;
                _scope.Expire();
                var locations = _locations;
                _locations = new ReadOnlyDictionary<string, ResolvedMemberStateLocation>(
                    new Dictionary<string, ResolvedMemberStateLocation>(StringComparer.Ordinal));
                DisposeLocations(locations.Values);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private void EnsureActive()
        {
            _scope.EnsureActive();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ledger = _getLedger();
            _scope.RequireCandidate(ledger.RootIdentity, ledger);
        }

        private void EnsureValidMap()
        {
            EnsureActive();
            var ledger = _getLedger();
            RequireLocatorPolicyLedger(ledger, _scope.RootIdentity, _scope.EnrollmentEpoch, _policy);
            RevalidateMemberLocationMap(ledger, _locations, _policy);
        }
    }
}
