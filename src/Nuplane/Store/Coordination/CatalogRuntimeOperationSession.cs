using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Shares one retained catalog borrow across projected schema-1 runtime operation owners.</summary>
internal sealed class CatalogRuntimeOperationSession : IAsyncDisposable
{
    private readonly RootMembershipRegistry _registry;
    private readonly RootMembershipRegistry.NativeCatalogOwner _owner;
    private readonly RootMembershipRegistry.NativeCatalogOwnerBorrow _borrow;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _gate = new();
    private Task? _closeTask;
    private Exception? _poisonReason;
    private int _projectionShares;
    private bool _closing;

    internal CatalogRuntimeOperationSession(
        RootMembershipRegistry registry,
        RootMembershipRegistry.NativeCatalogOwner owner,
        RootMembershipRegistry.NativeCatalogOwnerBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(borrow);
        borrow.RequireRegistry(registry);
        if (!ReferenceEquals(borrow.Owner, owner))
            throw new InvalidOperationException("The catalog borrow does not belong to the retained owner.");
        _registry = registry;
        _owner = owner;
        _borrow = borrow;
    }

    internal RootMembershipRegistry.NativeCatalogOwner Owner => _owner;

    internal RootMembershipRegistry.NativeCatalogOwnerBorrow Borrow => _borrow;

    internal RootMembershipRegistry Registry => _registry;

    internal async ValueTask<OperationLease> EnterOperationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Poison(exception);
            throw;
        }

        try
        {
            lock (_gate)
            {
                if (_closing || _poisonReason is not null)
                    throw Refused("The retained catalog runtime session is closing or poisoned.", _poisonReason);
            }
            _owner.RevalidateRetainedEvidence();
            return new OperationLease(this);
        }
        catch (Exception exception)
        {
            Poison(exception);
            _operationGate.Release();
            throw;
        }
    }

    internal IAsyncDisposable CreateProjectionShare()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing || _closeTask is not null || _poisonReason is not null, this);
            checked { _projectionShares++; }
            return new ProjectionShare(this);
        }
    }

    internal NativeCatalogLockedRoot RequireCurrentRoot(PhysicalRootIdentity root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var retained = _owner.SnapshotRetainedRoots().SingleOrDefault(candidate => candidate.RootIdentity == root);
        return retained ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
            "The requested membership authority is absent from the retained trusted catalog.", root);
    }

    internal void RevalidateRequestObservations(IEnumerable<ResolvedPackageStorePath> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        _owner.RevalidateRetainedEvidence();
        foreach (var observation in observations)
        {
            if (observation.RootIdentity is not { } rootIdentity)
            {
                observation.Revalidate();
                continue;
            }

            var current = RequireCurrentRoot(rootIdentity);
            observation.RevalidateOwnedLedgerOutcome(current.Ledger, current.LedgerIdentity);
        }
        _owner.RevalidateRetainedEvidence();
    }

    internal Task RefreshAfterOwnedPublicationAsync(
        PhysicalRootIdentity rootIdentity,
        RootMembershipRecord ledger,
        PhysicalFileIdentity ledgerIdentity)
        => _owner.RefreshRuntimeProjectionAfterOwnedOutcomeAsync(_borrow, rootIdentity, ledger, ledgerIdentity);

    internal void Poison(Exception? reason = null)
    {
        lock (_gate)
        {
            _poisonReason ??= reason ?? new InvalidOperationException(
                "A projected catalog runtime operation did not complete successfully.");
        }
    }

    internal async ValueTask DisposeUnprojectedAsync()
    {
        Task closeTask;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            if (_projectionShares != 0)
                throw new InvalidOperationException("Catalog runtime projections must drain before admission cleanup.");
            // A prior closer owns observing its close failure; do not replay it from outer cleanup.
            if (_closeTask is not null)
                return;
            _closing = true;
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = start.Task;
            closeTask = _closeTask;
        }

        if (start is not null)
            _ = CompleteCloseAsync(start, verifyAdmittedSession: false);
        await closeTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => DisposeUnprojectedAsync();

    private async ValueTask ReleaseProjectionShareAsync()
    {
        Task closeTask;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            if (_projectionShares <= 0)
                throw new InvalidOperationException("A catalog runtime projection share was released more than once.");
            _projectionShares--;
            if (_projectionShares != 0)
                return;

            if (_closeTask is null)
            {
                _closing = true;
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeTask = start.Task;
            }
            closeTask = _closeTask;
        }

        if (start is not null)
            _ = CompleteCloseAsync(start, verifyAdmittedSession: true);
        await closeTask.ConfigureAwait(false);
    }

    private async Task CompleteCloseAsync(TaskCompletionSource completion, bool verifyAdmittedSession)
    {
        try
        {
            await CloseCoreAsync(verifyAdmittedSession).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task CloseCoreAsync(bool verifyAdmittedSession)
    {
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var errors = new List<Exception>();
        try
        {
            if (verifyAdmittedSession)
            {
                try { _owner.RevalidateRetainedEvidence(); }
                catch (Exception exception) { errors.Add(exception); }
                try
                {
                    await _registry.VerifyCatalogMemberProtectionAsync(_borrow, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) { errors.Add(exception); }
            }

            Exception? poisonReason;
            lock (_gate) poisonReason = _poisonReason;
            if (poisonReason is not null)
                errors.Add(Refused("The catalog runtime session was poisoned by an incomplete or uncertain operation.", poisonReason));

            try { await _borrow.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { errors.Add(exception); }
            try { await _owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { errors.Add(exception); }
        }
        finally
        {
            _operationGate.Release();
        }

        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Catalog runtime session verification or cleanup failed.", errors);
    }

    private static PackageStoreAdmissionException Refused(string message, Exception? inner = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, innerException: inner);

    internal sealed class OperationLease : IAsyncDisposable
    {
        private CatalogRuntimeOperationSession? _session;
        private bool _completed;

        internal OperationLease(CatalogRuntimeOperationSession session) => _session = session;

        internal void Complete() => _completed = true;

        public ValueTask DisposeAsync()
        {
            var session = Interlocked.Exchange(ref _session, null);
            if (session is null)
                return ValueTask.CompletedTask;

            Exception? replayError = null;
            try { session._owner.RevalidateRetainedEvidence(); }
            catch (Exception exception)
            {
                replayError = exception;
                session.Poison(exception);
            }
            if (!_completed)
                session.Poison();

            session._operationGate.Release();
            if (_completed && replayError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(replayError).Throw();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProjectionShare(CatalogRuntimeOperationSession session) : IAsyncDisposable
    {
        private CatalogRuntimeOperationSession? _session = session;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _session, null)?.ReleaseProjectionShareAsync() ?? ValueTask.CompletedTask;
    }
}
