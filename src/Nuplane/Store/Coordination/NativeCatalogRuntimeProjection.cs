using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Owns one independent Complete-v1 context projected beneath a retained catalog session.</summary>
internal sealed class NativeCatalogRuntimeProjection : IAsyncDisposable, IStoreOperationLockedMemberContext
{
    private readonly Func<PhysicalFileIdentity> _getLedgerIdentity;
    private readonly Func<ValueTask> _disposeTransaction;
    private readonly CatalogRuntimeOperationSession _session;
    private readonly IAsyncDisposable _sessionShare;
    private readonly object _gate = new();
    private Task? _disposeTask;

    internal NativeCatalogRuntimeProjection(
        RootMembershipRegistry.LockedMemberLocations context,
        Func<PhysicalFileIdentity> getLedgerIdentity,
        Func<ValueTask> disposeTransaction,
        CatalogRuntimeOperationSession session,
        IAsyncDisposable sessionShare)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(getLedgerIdentity);
        ArgumentNullException.ThrowIfNull(disposeTransaction);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sessionShare);
        LockedMemberLocations = context;
        _getLedgerIdentity = getLedgerIdentity;
        _disposeTransaction = disposeTransaction;
        _session = session;
        _sessionShare = sessionShare;
    }

    public RootMembershipRegistry.LockedMemberLocations LockedMemberLocations { get; }

    internal RootMembershipRecord Ledger => LockedMemberLocations.Ledger;

    internal PhysicalFileIdentity LedgerIdentity => _getLedgerIdentity();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>(3);
        try { LockedMemberLocations.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        try { await _disposeTransaction().ConfigureAwait(false); }
        catch (Exception exception) { errors.Add(exception); }
        if (errors.Count > 0)
        {
            var failure = errors.Count == 1
                ? errors[0]
                : new AggregateException("Catalog runtime projection resources did not all release cleanly.", errors);
            _session.Poison(failure);
        }
        try { await _sessionShare.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { errors.Add(exception); }

        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Native catalog runtime projection cleanup failed.", errors);
    }
}
