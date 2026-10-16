using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

internal sealed partial class PhysicalStoreLock
{
    /// <summary>Acquires one root lock without acquiring any member lock.</summary>
    internal async Task<RootLockScope> AcquireRootLockAsync(
        PhysicalStoreDirectoryHandle controlDirectory,
        PhysicalRootIdentity rootIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlDirectory);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        var controlInfo = _fileSystem.InspectHandle(controlDirectory);
        if (controlInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Refusal(PackageStoreAdmissionReason.UnknownAuthority, "The held control location is not a directory.");

        HeldFileLock? rootLock = null;
        try
        {
            var acquired = await AcquireNamedLockAsync(controlDirectory, controlInfo.Identity, RootLockName, cancellationToken)
                .ConfigureAwait(false);
            rootLock = acquired.Lock;
            VerifyStillCanonical(controlDirectory, controlInfo.Identity, RootLockName, rootLock, acquired.NameSemantics);
            cancellationToken.ThrowIfCancellationRequested();
            var scope = new RootLockScope(this, rootIdentity, controlDirectory, controlInfo.Identity,
                rootLock, acquired.NameSemantics);
            rootLock = null;
            return scope;
        }
        catch (Exception acquisitionError)
        {
            if (rootLock is null)
                throw;

            try
            {
                await rootLock.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Root-lock acquisition failed and its native lock did not release cleanly.",
                    acquisitionError, cleanupError);
            }

            throw;
        }
    }

    /// <summary>
    /// Acquires all root-local member locks after every supplied root lock is already held.
    /// After argument validation, the operation consumes the root scopes on success or acquisition failure.
    /// The caller remains responsible for scopes when invalid arguments are rejected.
    /// </summary>
    internal async Task<PhysicalStoreLockUnionOwner> AcquireMemberLockUnionAsync(
        IReadOnlyList<RootLockScope> rootScopes,
        IReadOnlyList<RootMemberLockRequest> memberRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootScopes);
        ArgumentNullException.ThrowIfNull(memberRequests);
        cancellationToken.ThrowIfCancellationRequested();

        var roots = ValidateAndOrderRootScopes(rootScopes);
        var requests = ValidateMemberRequests(roots, memberRequests);
        var orderedObligations = requests
            .SelectMany(static request => request.StateSlots.Select(slot => new MemberLockObligation(request.Root, slot)))
            .OrderBy(static obligation => obligation.StateSlot, StateSlotComparer.Instance)
            .ThenBy(static obligation => obligation.Root.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ToArray();

        var acquiredMembers = new List<HeldFileLock>(orderedObligations.Length);
        var transferredLocks = new List<HeldFileLock>(roots.Length + orderedObligations.Length);
        var transferredControls = new List<PhysicalStoreDirectoryHandle>(roots.Length);
        PhysicalStoreLockUnionOwner? unionOwner = null;
        try
        {
            // Recheck the whole root set immediately before taking the first member lock.
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.VerifyCanonical();
            }

            foreach (var obligation in orderedObligations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lockName = GetMemberLockName(obligation.StateSlot);
                var acquired = await AcquireNamedLockAsync(
                    obligation.Root.ControlDirectory,
                    obligation.Root.ControlIdentity,
                    lockName,
                    cancellationToken).ConfigureAwait(false);
                acquiredMembers.Add(acquired.Lock);
                VerifyStillCanonical(obligation.Root.ControlDirectory, obligation.Root.ControlIdentity,
                    lockName, acquired.Lock, obligation.Root.NameSemantics);
                if (acquired.NameSemantics != obligation.Root.NameSemantics)
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "The control-directory name profile changed during member-lock acquisition.");
            }

            // Replay every root/control profile and every root-local member-lock binding after the complete
            // acquisition, so no earlier root is trusted only because it was checked before a later lock.
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.VerifyCanonical();
            }

            for (var index = 0; index < orderedObligations.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var obligation = orderedObligations[index];
                VerifyStillCanonical(obligation.Root.ControlDirectory, obligation.Root.ControlIdentity,
                    GetMemberLockName(obligation.StateSlot), acquiredMembers[index], obligation.Root.NameSemantics);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var root in roots)
                transferredLocks.Add(root.TransferRootLock());
            transferredLocks.AddRange(acquiredMembers);
            acquiredMembers.Clear();

            foreach (var root in roots)
                transferredControls.Add(root.TransferControlDirectory());
            unionOwner = new PhysicalStoreLockUnionOwner(transferredLocks.ToArray(), transferredControls.ToArray());
            transferredLocks.Clear();
            transferredControls.Clear();
            return unionOwner;
        }
        catch (Exception acquisitionError)
        {
            var cleanupErrors = new List<Exception>();
            if (unionOwner is not null)
            {
                try { await unionOwner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
            }
            else
            {
                cleanupErrors.AddRange(await DisposeLocksInReverseCoreAsync(acquiredMembers).ConfigureAwait(false));
                cleanupErrors.AddRange(await DisposeLocksInReverseCoreAsync(transferredLocks).ConfigureAwait(false));
                for (var index = transferredControls.Count - 1; index >= 0; index--)
                {
                    try { transferredControls[index].Dispose(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }
            }
            for (var index = roots.Length - 1; index >= 0; index--)
            {
                try { await roots[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
            }

            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "Member-lock union acquisition failed and one or more retained root locks did not release cleanly.",
                    new[] { acquisitionError }.Concat(cleanupErrors));
            }

            throw;
        }
    }

    private RootLockScope[] ValidateAndOrderRootScopes(IReadOnlyList<RootLockScope> rootScopes)
    {
        if (rootScopes.Count == 0)
            throw new ArgumentException("A member-lock union requires at least one held root lock.", nameof(rootScopes));

        var roots = new RootLockScope[rootScopes.Count];
        var seen = new HashSet<PhysicalRootIdentity>();
        for (var index = 0; index < roots.Length; index++)
        {
            var root = rootScopes[index] ?? throw new ArgumentException("Root-lock scopes cannot contain null entries.", nameof(rootScopes));
            if (!ReferenceEquals(root.Owner, this))
                throw new ArgumentException("Every root-lock scope must come from this physical lock provider.", nameof(rootScopes));
            if (!seen.Add(root.RootIdentity))
                throw new ArgumentException("A physical root can occur only once in a lock union.", nameof(rootScopes));
            root.EnsureAvailable();
            if (index > 0 && PhysicalRootIdentityComparer.Instance.Compare(roots[index - 1].RootIdentity, root.RootIdentity) >= 0)
                throw new ArgumentException("Root locks must be acquired in canonical physical-root order.", nameof(rootScopes));
            roots[index] = root;
        }

        return roots;
    }

    private static RootMemberLockRequest[] ValidateMemberRequests(
        IReadOnlyList<RootLockScope> roots,
        IReadOnlyList<RootMemberLockRequest> memberRequests)
    {
        if (memberRequests.Count != roots.Count)
            throw new ArgumentException("Every held root requires exactly one member-lock request.", nameof(memberRequests));

        var requestsByRoot = new Dictionary<RootLockScope, RootMemberLockRequest>(ReferenceEqualityComparer.Instance);
        foreach (var request in memberRequests)
        {
            if (request is null)
                throw new ArgumentException("Member-lock requests cannot contain null entries.", nameof(memberRequests));
            if (!roots.Contains(request.Root, ReferenceEqualityComparer.Instance))
                throw new ArgumentException("A member-lock request references a root outside this union.", nameof(memberRequests));
            if (!requestsByRoot.TryAdd(request.Root, request))
                throw new ArgumentException("A held root can have only one member-lock request.", nameof(memberRequests));
        }

        var requests = new RootMemberLockRequest[roots.Count];
        for (var index = 0; index < roots.Count; index++)
        {
            if (!requestsByRoot.TryGetValue(roots[index], out var request))
                throw new ArgumentException("A held root is missing its member-lock request.", nameof(memberRequests));
            var orderedSlots = ValidateAndOrderSlots(request.StateSlots);
            var lockNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in orderedSlots)
            {
                if (!lockNames.Add(GetMemberLockName(slot)))
                    throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                        "Distinct state-slot identities map to one root-local member-lock name.");
            }
            requests[index] = new RootMemberLockRequest(roots[index], orderedSlots);
        }

        return requests;
    }

    internal sealed class RootLockScope : IAsyncDisposable
    {
        private readonly object _gate = new();
        private HeldFileLock? _rootLock;
        private PhysicalStoreDirectoryHandle? _controlDirectory;
        private Task? _disposeTask;
        private bool _transferred;

        internal RootLockScope(
            PhysicalStoreLock owner,
            PhysicalRootIdentity rootIdentity,
            PhysicalStoreDirectoryHandle controlDirectory,
            PhysicalFileIdentity controlIdentity,
            HeldFileLock rootLock,
            PhysicalStoreNameSemantics nameSemantics)
        {
            Owner = owner;
            RootIdentity = rootIdentity;
            _controlDirectory = controlDirectory;
            ControlIdentity = controlIdentity;
            _rootLock = rootLock;
            NameSemantics = nameSemantics;
        }

        internal PhysicalStoreLock Owner { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal PhysicalStoreDirectoryHandle ControlDirectory
            => _controlDirectory ?? throw new ObjectDisposedException(nameof(RootLockScope));
        internal PhysicalFileIdentity ControlIdentity { get; }
        internal PhysicalStoreNameSemantics NameSemantics { get; }

        internal void VerifyCanonical()
        {
            lock (_gate)
            {
                EnsureAvailableCore();
                Owner.VerifyRootAndDirectoryProfile(ControlDirectory, ControlIdentity,
                    _rootLock!, NameSemantics);
            }
        }

        internal void EnsureAvailable()
        {
            lock (_gate)
                EnsureAvailableCore();
        }

        private void EnsureAvailableCore()
        {
            if (_transferred || _rootLock is null || _controlDirectory is null || _disposeTask is not null)
                throw new ObjectDisposedException(nameof(RootLockScope));
        }

        internal HeldFileLock TransferRootLock()
        {
            lock (_gate)
            {
                EnsureAvailableCore();
                var rootLock = _rootLock!;
                _rootLock = null;
                _transferred = true;
                return rootLock;
            }
        }

        internal PhysicalStoreDirectoryHandle TransferControlDirectory()
        {
            lock (_gate)
            {
                if (!_transferred || _controlDirectory is null)
                    throw new InvalidOperationException("The root lock scope is not part of a transferred union.");
                var control = _controlDirectory;
                _controlDirectory = null;
                return control;
            }
        }

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
            List<Exception>? errors = null;
            if (_rootLock is not null)
            {
                try { await _rootLock.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
                _rootLock = null;
            }

            if (_controlDirectory is not null)
            {
                try { _controlDirectory.Dispose(); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
                _controlDirectory = null;
            }

            ThrowCleanupErrors(errors, "A root-lock scope could not be released cleanly.");
        }
    }

    internal sealed record RootMemberLockRequest(RootLockScope Root, IReadOnlyList<StateSlotIdentity> StateSlots);

    private sealed record MemberLockObligation(RootLockScope Root, StateSlotIdentity StateSlot);

    internal sealed class PhysicalStoreLockUnionOwner : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly HeldFileLock[] _heldLocks;
        private readonly PhysicalStoreDirectoryHandle[] _controlDirectories;
        private int _references = 1;
        private bool _primaryReleased;
        private Task? _primaryDisposeTask;
        private Task? _finalReleaseTask;

        internal PhysicalStoreLockUnionOwner(
            HeldFileLock[] heldLocks,
            PhysicalStoreDirectoryHandle[] controlDirectories)
        {
            _heldLocks = heldLocks;
            _controlDirectories = controlDirectories;
        }

        internal IAsyncDisposable CreateShare()
        {
            lock (_gate)
            {
                if (_primaryReleased || _finalReleaseTask is not null)
                    throw new ObjectDisposedException(nameof(PhysicalStoreLockUnionOwner));
                // Allocate before incrementing so a failed share construction cannot strand a reference.
                var share = new Share(this);
                _references++;
                return share;
            }
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? startRelease = null;
            Task result;
            lock (_gate)
            {
                if (_primaryReleased)
                    return new ValueTask(_primaryDisposeTask ?? Task.CompletedTask);
                _primaryReleased = true;
                result = ReleaseReferenceLocked(out startRelease);
                _primaryDisposeTask = result;
            }

            if (startRelease is not null)
                _ = ReleasePhysicalResourcesAsync(startRelease);
            return new ValueTask(result);
        }

        private ValueTask ReleaseShareAsync()
        {
            TaskCompletionSource? startRelease = null;
            Task result;
            lock (_gate)
            {
                if (_references <= 0)
                    throw new InvalidOperationException("The physical lock union reference count is already drained.");
                result = ReleaseReferenceLocked(out startRelease);
            }

            if (startRelease is not null)
                _ = ReleasePhysicalResourcesAsync(startRelease);
            return new ValueTask(result);
        }

        private Task ReleaseReferenceLocked(out TaskCompletionSource? startRelease)
        {
            _references--;
            if (_references > 0)
            {
                startRelease = null;
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _finalReleaseTask = completion.Task;
            startRelease = completion;
            return completion.Task;
        }

        private async Task ReleasePhysicalResourcesAsync(TaskCompletionSource completion)
        {
            var errors = await DisposeLocksInReverseCoreAsync(_heldLocks).ConfigureAwait(false);
            for (var index = _controlDirectories.Length - 1; index >= 0; index--)
            {
                try { _controlDirectories[index].Dispose(); }
                catch (Exception exception) { errors.Add(exception); }
            }

            if (errors.Count == 0)
                completion.TrySetResult();
            else if (errors.Count == 1)
                completion.TrySetException(errors[0]);
            else
                completion.TrySetException(new AggregateException(
                    "Physical root/member lock union cleanup encountered multiple failures.", errors));
        }

        private sealed class Share(PhysicalStoreLockUnionOwner owner) : IAsyncDisposable
        {
            private readonly object _gate = new();
            private Task? _disposeTask;

            public ValueTask DisposeAsync()
            {
                lock (_gate)
                {
                    _disposeTask ??= owner.ReleaseShareAsync().AsTask();
                    return new ValueTask(_disposeTask);
                }
            }
        }
    }
}
