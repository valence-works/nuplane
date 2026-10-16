using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>An immutable active-package projection paired with its retained path admission.</summary>
/// <remarks>Raw descriptors remain descriptive; use <see cref="BorrowFor"/> for admitted path work.</remarks>
public sealed class AdmittedActivePackageSnapshot : IAsyncDisposable
{
    private readonly object _disposeSync = new();
    private PackageStorePathAdmission? _admission;
    private Task? _disposeTask;

    internal AdmittedActivePackageSnapshot(
        ActivePackagesSnapshot snapshot,
        PackageStorePathAdmission admission,
        StoreStateRecord state)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Gets the immutable snapshot projection.</summary>
    public ActivePackagesSnapshot Snapshot { get; }

    internal StoreStateRecord State { get; }

    /// <summary>Creates a counted path-limited borrow for a path captured by the snapshot.</summary>
    public PackageStoreOperationBorrow BorrowFor(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var admission = Volatile.Read(ref _admission)
            ?? throw new ObjectDisposedException(nameof(AdmittedActivePackageSnapshot));
        return admission.BorrowFor(installPath);
    }

    internal PackageStoreOperationBorrow BorrowRootFor(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var admission = Volatile.Read(ref _admission)
            ?? throw new ObjectDisposedException(nameof(AdmittedActivePackageSnapshot));
        return admission.BorrowRootFor(installPath);
    }

    /// <summary>Closes the scope and waits for borrows already issued from it to drain.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            var admission = Interlocked.Exchange(ref _admission, null);
            try { _disposeTask = admission?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
            catch (Exception exception) { _disposeTask = Task.FromException(exception); }
            return new ValueTask(_disposeTask);
        }
    }
}
