using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Implements the counted read and release state for one root-bound graph-use lease.</summary>
/// <remarks>
/// The native root/use ownership is supplied by the validated core binding path. This type does not
/// reopen paths or establish native identity; its exact path map is immutable for the lease lifetime.
/// </remarks>
internal sealed class PackageGraphUseLeaseOwnerControl : IPackageGraphUseLeaseOwnerControl
{
    private readonly object _gate = new();
    private readonly PhysicalRootIdentity _root;
    private readonly PackageGraphUseSnapshot _snapshot;
    private readonly Dictionary<string, Guid> _nodeByExactPath;
    private readonly IAsyncDisposable _heldOwnership;
    private readonly IPackageGraphUseLifetimeObserver _lifetimeObserver;
    private readonly IPhysicalStoreFileSystem? _nativeFileSystem;
    private readonly PackageGraphUseLeaseOwner _owner;
    private readonly PackageGraphUseLease _lease;

    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource<bool>? _closeCompletion;
    private int _activeReads;
    private bool _closing;
    private bool _transferred;
    private bool _released;
    private Exception? _releaseFailure;

    private PackageGraphUseLeaseOwnerControl(
        PhysicalRootIdentity root,
        PackageGraphUseSnapshot snapshot,
        Dictionary<Guid, string> pathsByNode,
        IAsyncDisposable heldOwnership,
        IPackageGraphUseLifetimeObserver lifetimeObserver,
        IPhysicalStoreFileSystem? nativeFileSystem)
    {
        _root = root;
        _snapshot = snapshot;
        _nodeByExactPath = pathsByNode.ToDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);
        _heldOwnership = heldOwnership;
        _lifetimeObserver = lifetimeObserver;
        _nativeFileSystem = nativeFileSystem;
        _lease = new PackageGraphUseLease(snapshot, this);
        _owner = new PackageGraphUseLeaseOwner(_lease, this);
    }

    internal static PackageGraphUseLeaseOwner Create(
        PhysicalRootIdentity root,
        PackageGraphUseSnapshot snapshot,
        IReadOnlyDictionary<Guid, string> canonicalInstallPaths,
        IAsyncDisposable heldOwnership,
        IPackageGraphUseLifetimeObserver lifetimeObserver,
        IPhysicalStoreFileSystem? nativeFileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(canonicalInstallPaths);
        ArgumentNullException.ThrowIfNull(heldOwnership);
        ArgumentNullException.ThrowIfNull(lifetimeObserver);

        var paths = ValidateAndCopyBindings(root, snapshot, canonicalInstallPaths);
        return new PackageGraphUseLeaseOwnerControl(root, snapshot, paths, heldOwnership, lifetimeObserver,
            nativeFileSystem)._owner;
    }

    internal IPhysicalStoreFileSystem NativeFileSystem => _nativeFileSystem
        ?? throw Refusal(PackageStoreAdmissionReason.UnsupportedParticipant,
            "This graph-use lease has no retained native filesystem provider.");

    internal Exception? ReleaseFailure
    {
        get
        {
            lock (_gate)
                return _releaseFailure;
        }
    }

    public IDisposable AcquireRead(PackageGraphUseLease lease, string installPath)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        lock (_gate)
        {
            EnsureLeaseMatches(lease);
            if (_closing || _released)
                throw Refusal(PackageStoreAdmissionReason.ExpiredScope, "The package-graph use lease is closing or has ended.");
            if (!_nodeByExactPath.ContainsKey(installPath))
                throw Refusal(PackageStoreAdmissionReason.RootMismatch, "The exact install path is not part of this root-bound graph-use lease.");

            _activeReads++;
            return new ReadPin(this);
        }
    }

    public PackageInstallIdentity GetInstallIdentity(PackageGraphUseLease lease, string installPath)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        lock (_gate)
        {
            EnsureLeaseMatches(lease);
            if (!_nodeByExactPath.TryGetValue(installPath, out var nodeId))
                throw Refusal(PackageStoreAdmissionReason.RootMismatch, "The exact install path is not part of this root-bound graph-use lease.");
            return _snapshot.Nodes.Single(node => node.NodeId == nodeId).Install;
        }
    }

    public void TransferToLifetime(
        PackageGraphUseLeaseOwner owner,
        WeakReference<object> lifetime,
        bool isCollectible)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lifetime);

        lock (_gate)
        {
            EnsureOwnerMatches(owner);
            if (_closing || _released || _transferred)
                throw Refusal(PackageStoreAdmissionReason.ExpiredScope, "The package-graph use lease cannot transfer release authority in its current state.");

            if (lifetime.TryGetTarget(out var target) &&
                (ReferenceEquals(target, owner) || ReferenceEquals(target, owner.Lease) ||
                 ReferenceEquals(target, _snapshot) || ReferenceEquals(target, _heldOwnership) ||
                 ReferenceEquals(target, this) || ReferenceEquals(target, _lifetimeObserver)))
            {
                throw Refusal(PackageStoreAdmissionReason.UnsupportedParticipant, "The lifetime target is already retained by the graph-use owner and cannot control its release.");
            }

            var retained = isCollectible
                ? _lifetimeObserver.TryRegisterCollectible(owner, lifetime)
                : PackageGraphUseLifetimeRetention.RetainNonCollectible(owner);
            if (!retained)
                throw Refusal(PackageStoreAdmissionReason.UnsupportedParticipant, "The passive package-graph lifetime observer is not accepting collectible leases.");

            _transferred = true;
        }
    }

    public ValueTask DisposeOwnerAsync(PackageGraphUseLeaseOwner owner)
        => new(RequestClose(owner, transferredOnly: false));

    public ValueTask DisposeTransferredOwnerAsync(PackageGraphUseLeaseOwner owner)
        => new(RequestClose(owner, transferredOnly: true));

    private Task RequestClose(PackageGraphUseLeaseOwner owner, bool transferredOnly)
    {
        TaskCompletionSource<bool> completion;
        Task drain;
        lock (_gate)
        {
            EnsureOwnerMatches(owner);
            if (transferredOnly && !_transferred)
                throw Refusal(PackageStoreAdmissionReason.ExpiredScope, "Release authority was not transferred to a package lifetime.");
            if (!transferredOnly && _transferred)
                return Task.CompletedTask;
            if (_closeCompletion is not null)
                return _closeCompletion.Task;

            _closing = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeCompletion = completion;
            if (_activeReads == 0)
                drain = Task.CompletedTask;
            else
            {
                _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _drained.Task;
            }
        }

        _ = ReleaseAfterDrainAsync(drain, completion);
        return completion.Task;
    }

    private void ReleaseRead()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (_activeReads <= 0)
                throw new InvalidOperationException("The graph-use read count is inconsistent.");
            _activeReads--;
            if (_closing && _activeReads == 0)
                drained = _drained;
        }

        drained?.TrySetResult(true);
    }

    private async Task ReleaseAfterDrainAsync(Task drain, TaskCompletionSource<bool> completion)
    {
        try
        {
            await drain.ConfigureAwait(false);
            await _heldOwnership.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
                _released = true;

            if (_transferred)
                PackageGraphUseLifetimeRetention.ReleaseSucceeded(_owner);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            lock (_gate)
                _releaseFailure = exception;

            if (_transferred)
                PackageGraphUseLifetimeRetention.ReleaseFailed(_owner, exception);
            else
                PackageGraphUseLifetimeRetention.RetainFailedControl(this, exception);
            completion.TrySetException(exception);
        }
    }

    private void EnsureLeaseMatches(PackageGraphUseLease lease)
    {
        if (!ReferenceEquals(lease, _lease) || !ReferenceEquals(lease.Snapshot, _snapshot))
            throw Refusal(PackageStoreAdmissionReason.ExpiredScope, "The package-graph lease view is foreign or stale.");
    }

    private void EnsureOwnerMatches(PackageGraphUseLeaseOwner owner)
    {
        if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(owner.Lease, _lease))
            throw Refusal(PackageStoreAdmissionReason.ExpiredScope, "The package-graph lease owner is foreign or stale.");
    }

    private PackageStoreAdmissionException Refusal(PackageStoreAdmissionReason reason, string message)
        => new(reason, message, _root);

    private static Dictionary<Guid, string> ValidateAndCopyBindings(
        PhysicalRootIdentity root,
        PackageGraphUseSnapshot snapshot,
        IReadOnlyDictionary<Guid, string> suppliedPaths)
    {
        if (snapshot.Roots.Count(item => item == root) != 1)
            throw InvalidBinding(root, "The graph-use snapshot does not declare this physical root exactly once.");

        var nodeIds = new HashSet<Guid>();
        var declaredRoots = new HashSet<PhysicalRootIdentity>();
        foreach (var declaredRoot in snapshot.Roots)
        {
            if (!declaredRoots.Add(declaredRoot))
                throw InvalidBinding(root, "The graph-use snapshot contains a duplicate physical root.");
        }

        var rootNodes = new HashSet<Guid>();
        foreach (var node in snapshot.Nodes)
        {
            if (!nodeIds.Add(node.NodeId))
                throw InvalidBinding(root, "The graph-use snapshot contains a duplicate node identity.");
            if (!declaredRoots.Contains(node.Install.Root))
                throw InvalidBinding(root, "A graph node refers to a physical root omitted from the snapshot.");
            if (node.Install.Root == root)
                rootNodes.Add(node.NodeId);
        }

        if (rootNodes.Count == 0 || suppliedPaths.Count != rootNodes.Count)
            throw InvalidBinding(root, "The native install-path binding does not exactly cover this root's graph nodes.");

        var result = new Dictionary<Guid, string>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodeId in rootNodes)
        {
            if (!suppliedPaths.TryGetValue(nodeId, out var path) || string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
                throw InvalidBinding(root, "A graph node is missing its exact fully qualified native install path.");
            if (!paths.Add(path))
                throw InvalidBinding(root, "Two graph nodes resolve to the same exact install path.");
            result.Add(nodeId, path);
        }

        if (suppliedPaths.Keys.Any(nodeId => !rootNodes.Contains(nodeId)))
            throw InvalidBinding(root, "The native install-path binding contains a node from another root or graph.");

        return result;
    }

    private static PackageStoreAdmissionException InvalidBinding(PhysicalRootIdentity root, string message)
        => new(PackageStoreAdmissionReason.StateMismatch, message, root);

    private sealed class ReadPin(PackageGraphUseLeaseOwnerControl owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseRead();
        }
    }
}
