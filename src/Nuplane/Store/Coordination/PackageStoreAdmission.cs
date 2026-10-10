using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Acquires retained native and semantic admission for configured roots and complete install-path sets.</summary>
/// <remarks>This implementation classifies authority but does not itself wire any package-store driver.</remarks>
internal sealed class PackageStoreAdmission : IPackageStoreAdmission
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly RootMembershipRegistry _registry;
    private readonly PackageStoreAuthorityResolver _resolver;
    private readonly string _configuredRootLocator;
    private readonly string? _configuredRootBaseLocator;

    internal RootMembershipRegistry Registry => _registry;

    internal PackageStoreAdmission(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        string configuredRootLocator,
        string? configuredRootBaseLocator = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRootLocator);
        _files = files;
        _registry = registry;
        _resolver = new PackageStoreAuthorityResolver(files, registry);
        _configuredRootLocator = configuredRootLocator;
        _configuredRootBaseLocator = configuredRootBaseLocator;
    }

    public async ValueTask<PackageStoreRootOperationAdmission> AcquireConfiguredRootOperationAsync(
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = _resolver.Resolve(_configuredRootLocator,
            PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix, exactBaseLocator: _configuredRootBaseLocator);
        var transferred = false;
        Exception? admissionError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refusal("The configured package-store root is not a held directory.");
            var targetInfo = _files.InspectHandle(target);
            if (targetInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Refusal("The configured package-store root changed kind during admission.");

            if (resolved.IsProspectiveConfiguredRoot)
            {
                if (resolved.RootIdentity is not null || resolved.MembershipCandidate is not null)
                    throw Refusal("A prospective configured root cannot carry an observed membership authority.");
                resolved.Revalidate();
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Unenrolled,
                    root: null, owner: null);
            }

            if (resolved.RootIdentity is null)
            {
                // The resolver returns no candidate only after observing reserved authority absent at every path edge.
                resolved.Revalidate();
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Unenrolled,
                    new PhysicalRootIdentity(targetInfo.Identity), owner: null);
            }

            var candidate = resolved.MembershipCandidate
                ?? throw Refusal("The configured root has no retained membership candidate.");
            var ledgerIdentity = resolved.MembershipLedgerIdentity
                ?? throw Refusal("The configured root has no retained ledger-file identity.");
            var owner = await AcquireVerifiedOwnerAsync(resolved.AuthorityRoot!, resolved.RootIdentity,
                candidate.EnrollmentEpoch, candidate.LedgerDigest, ledgerIdentity, cancellationToken).ConfigureAwait(false);
            try
            {
                resolved.Revalidate();
                var validator = new AdmittedPathValidator(_files, _resolver, resolved.RootIdentity,
                    candidate.EnrollmentEpoch, () => (owner.Ledger.LedgerDigest, owner.LedgerIdentity),
                    pathIdentities: null);
                var state = new PackageStoreOperationState(resolved.RootIdentity, candidate.EnrollmentEpoch,
                    new EnrolledOperationOwnership(owner, resolved), validator);
                transferred = true;
                return new PackageStoreRootOperationAdmission(PackageStoreAdmissionStatus.Enrolled,
                    resolved.RootIdentity, state.Owner);
            }
            catch (Exception exception)
            {
                try { await owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Configured-root admission failed and its retained owner did not release cleanly.",
                        exception, cleanupError);
                }
                throw;
            }
        }
        catch (Exception exception)
        {
            admissionError = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                if (admissionError is null)
                {
                    resolved.Dispose();
                }
                else
                {
                    try { resolved.Dispose(); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException(
                            "Configured-root admission failed and its retained path observation did not close cleanly.",
                            admissionError, cleanupError);
                    }
                }
            }
        }
    }

    public async ValueTask<PackageStorePathAdmission> AcquireForInstallPathsAsync(
        IReadOnlyCollection<string> installPaths,
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installPaths);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        cancellationToken.ThrowIfCancellationRequested();

        var requestedPaths = installPaths.ToArray();
        if (requestedPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Install paths cannot be null, empty, or whitespace.", nameof(installPaths));

        var observations = new List<PathObservation>(requestedPaths.Length);
        var groups = new Dictionary<PhysicalRootIdentity, RootGroup>();
        var owners = new List<RootOwnerEntry>();
        var transferred = false;
        Exception? admissionError = null;
        try
        {
            foreach (var path in requestedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (resolved, targetKind) = ResolveInstallPath(path);
                PhysicalStoreEntryInfo targetInfo;
                try { targetInfo = _files.InspectHandle(resolved.Target); }
                catch (Exception exception)
                {
                    DisposeResolutionAfterFailure(resolved, exception);
                    throw;
                }
                if ((targetKind == PhysicalStorePathTarget.PackageDirectory && targetInfo.Kind != PhysicalStoreEntryKind.Directory) ||
                    (targetKind == PhysicalStorePathTarget.ArchiveFile &&
                     (targetInfo.Kind != PhysicalStoreEntryKind.RegularFile || targetInfo.LinkCount != 1)))
                {
                    var refusal = Refusal("An install path did not resolve to one supported package directory or archive file.");
                    DisposeResolutionAfterFailure(resolved, refusal);
                    throw refusal;
                }

                var observation = new PathObservation(path, resolved, targetInfo.Identity, targetKind);
                observations.Add(observation);
                if (resolved.RootIdentity is null)
                    continue;

                var candidate = resolved.MembershipCandidate
                    ?? throw Refusal("An enrolled install path has no retained membership candidate.");
                var ledgerIdentity = resolved.MembershipLedgerIdentity
                    ?? throw Refusal("An enrolled install path has no retained ledger-file identity.");
                if (!groups.TryGetValue(resolved.RootIdentity, out var group))
                {
                    group = new RootGroup(resolved.RootIdentity, candidate.EnrollmentEpoch,
                        candidate.LedgerDigest, ledgerIdentity, resolved.AuthorityRoot!);
                    groups.Add(resolved.RootIdentity, group);
                }
                else if (group.Epoch != candidate.EnrollmentEpoch ||
                         !string.Equals(group.LedgerDigest, candidate.LedgerDigest, StringComparison.Ordinal) ||
                         group.LedgerIdentity != ledgerIdentity)
                {
                    throw Refusal("Paths resolving to one physical root observed conflicting membership evidence.", resolved.RootIdentity);
                }

                group.Paths.Add(observation);
            }

            var orderedGroups = groups.Values.OrderBy(static group => group.Root, PhysicalRootIdentityComparer.Instance).ToArray();
            foreach (var group in orderedGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locked = await _registry.AcquireCompleteMemberLocationsAsync(group.RootHandle, group.Root,
                    group.Epoch, group.LedgerDigest, group.LedgerIdentity, cancellationToken).ConfigureAwait(false);
                owners.Add(new RootOwnerEntry(group, locked));
            }

            // Every distinct root and every declared member lock is now held before the first member payload read.
            foreach (var owner in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locked = owner.Locked ?? throw new InvalidOperationException("A root lock scope was not retained.");
                await _registry.VerifyAllMemberProtectionAsync(locked.Context, owner.Group.RootHandle,
                    owner.Group.Root, owner.Group.Epoch, RootMembershipStatus.Complete, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Retained observations for the complete path union are rechecked after all roots and members verify.
            foreach (var observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observation.Resolved.Revalidate();
            }

            foreach (var owner in owners)
            {
                var locked = owner.Locked ?? throw new InvalidOperationException("A root lock scope was not retained.");
                var expectedIdentities = owner.Group.Paths
                    .GroupBy(static observation => observation.Path, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key,
                        static group => new AdmittedPathIdentity(group.First().TargetIdentity, group.First().TargetKind),
                        StringComparer.Ordinal);
                var validator = new AdmittedPathValidator(_files, _resolver, owner.Group.Root, owner.Group.Epoch,
                    () => (locked.Ledger.LedgerDigest, locked.LedgerIdentity), expectedIdentities);
                var state = new PackageStoreOperationState(owner.Group.Root, owner.Group.Epoch,
                    new EnrolledOperationOwnership(locked), validator);
                owner.State = state;
                owner.Locked = null;
            }

            var ownerByRoot = owners.ToDictionary(static entry => entry.Group.Root, static entry => entry.State!);
            var entries = observations.Select(observation => new PackageStorePathAdmissionEntry(
                observation.Path,
                observation.Resolved.RootIdentity is null ? PackageStoreAdmissionStatus.Unenrolled : PackageStoreAdmissionStatus.Enrolled,
                observation.Resolved.RootIdentity)).ToArray();
            var retainedOwners = owners.Select(static owner => owner.State!).ToArray();
            var pathControl = new PathAdmissionControl(ownerByRoot, retainedOwners, observations);
            var admission = new PackageStorePathAdmission(entries, pathControl);
            pathControl.Bind(admission);
            transferred = true;
            return admission;
        }
        catch (Exception exception)
        {
            admissionError = exception;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                var cleanupErrors = new List<Exception>();
                for (var index = owners.Count - 1; index >= 0; index--)
                {
                    if (owners[index].State is { } state)
                    {
                        try { await state.Owner.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                    else if (owners[index].Locked is { } locked)
                    {
                        try { await locked.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                }

                for (var index = observations.Count - 1; index >= 0; index--)
                {
                    try { observations[index].Resolved.Dispose(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }

                if (cleanupErrors.Count > 0)
                {
                    var failures = admissionError is null
                        ? cleanupErrors
                        : new[] { admissionError }.Concat(cleanupErrors).ToList();
                    throw new AggregateException(
                        "Path admission failed and one or more retained resources did not release cleanly.", failures);
                }
            }
        }
    }

    private async Task<RootMembershipRegistry.CompleteMemberLocationsOwner> AcquireVerifiedOwnerAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity rootIdentity,
        long epoch,
        string ledgerDigest,
        PhysicalFileIdentity ledgerIdentity,
        CancellationToken cancellationToken)
    {
        var locked = await _registry.AcquireCompleteMemberLocationsAsync(root, rootIdentity, epoch,
            ledgerDigest, ledgerIdentity, cancellationToken).ConfigureAwait(false);
        try
        {
            await _registry.VerifyAllMemberProtectionAsync(locked.Context, root, rootIdentity, epoch,
                RootMembershipStatus.Complete, cancellationToken).ConfigureAwait(false);
            return locked;
        }
        catch (Exception exception)
        {
            try { await locked.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Complete-member verification failed and its retained lock owner did not release cleanly.",
                    exception, cleanupError);
            }
            throw;
        }
    }

    private static void DisposeResolutionAfterFailure(ResolvedPackageStorePath resolved, Exception admissionError)
    {
        try { resolved.Dispose(); }
        catch (Exception cleanupError)
        {
            throw new AggregateException(
                "Install-path admission failed and its retained path observation did not close cleanly.",
                admissionError, cleanupError);
        }
    }

    private static PackageStoreAdmissionException Refusal(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private (ResolvedPackageStorePath Resolved, PhysicalStorePathTarget TargetKind) ResolveInstallPath(string path)
    {
        try
        {
            return (_resolver.Resolve(path, PhysicalStorePathTarget.PackageDirectory), PhysicalStorePathTarget.PackageDirectory);
        }
        catch (PackageStoreAdmissionException)
        {
            // The second metadata-only walk distinguishes a single-link archive from a directory. Both paths
            // reject final aliases and both must complete before any package/archive payload is opened.
            return (_resolver.Resolve(path, PhysicalStorePathTarget.ArchiveFile), PhysicalStorePathTarget.ArchiveFile);
        }
    }

    private sealed class RootGroup(
        PhysicalRootIdentity root,
        long epoch,
        string ledgerDigest,
        PhysicalFileIdentity ledgerIdentity,
        PhysicalStoreDirectoryHandle rootHandle)
    {
        internal PhysicalRootIdentity Root { get; } = root;
        internal long Epoch { get; } = epoch;
        internal string LedgerDigest { get; } = ledgerDigest;
        internal PhysicalFileIdentity LedgerIdentity { get; } = ledgerIdentity;
        internal PhysicalStoreDirectoryHandle RootHandle { get; } = rootHandle;
        internal List<PathObservation> Paths { get; } = [];
    }

    private sealed record PathObservation(
        string Path,
        ResolvedPackageStorePath Resolved,
        PhysicalFileIdentity TargetIdentity,
        PhysicalStorePathTarget TargetKind);

    private sealed record AdmittedPathIdentity(PhysicalFileIdentity Identity, PhysicalStorePathTarget TargetKind);

    private sealed class RootOwnerEntry(RootGroup group, RootMembershipRegistry.CompleteMemberLocationsOwner locked)
    {
        internal RootGroup Group { get; } = group;
        internal RootMembershipRegistry.CompleteMemberLocationsOwner? Locked { get; set; } = locked;
        internal PackageStoreOperationState? State { get; set; }
    }

    private sealed class EnrolledOperationOwnership : IAsyncDisposable, IStoreOperationLockedMemberContext
    {
        private readonly RootMembershipRegistry.CompleteMemberLocationsOwner _locked;
        private readonly ResolvedPackageStorePath? _configuredRootObservation;

        internal EnrolledOperationOwnership(
            RootMembershipRegistry.CompleteMemberLocationsOwner locked,
            ResolvedPackageStorePath? configuredRootObservation = null)
        {
            _locked = locked;
            _configuredRootObservation = configuredRootObservation;
        }

        public RootMembershipRegistry.LockedMemberLocations LockedMemberLocations => _locked.Context;
        internal RootMembershipRegistry.CompleteMemberLocationsOwner Locked => _locked;

        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>(2);
            try { await _locked.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { errors.Add(exception); }
            try { _configuredRootObservation?.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }

            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Enrolled operation ownership cleanup encountered multiple failures.", errors);
        }
    }

    private sealed class AdmittedPathValidator : IPackageStoreOperationPathValidator,
        IPackageStoreOperationPackageDirectoryValidator
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly PackageStoreAuthorityResolver _resolver;
        private readonly PhysicalRootIdentity _root;
        private readonly long _epoch;
        private readonly Func<(string Digest, PhysicalFileIdentity Identity)> _ledgerObservation;
        private readonly IReadOnlyDictionary<string, AdmittedPathIdentity>? _pathIdentities;

        internal AdmittedPathValidator(
            IPhysicalStoreFileSystem files,
            PackageStoreAuthorityResolver resolver,
            PhysicalRootIdentity root,
            long epoch,
            Func<(string Digest, PhysicalFileIdentity Identity)> ledgerObservation,
            IReadOnlyDictionary<string, AdmittedPathIdentity>? pathIdentities)
        {
            _files = files;
            _resolver = resolver;
            _root = root;
            _epoch = epoch;
            _ledgerObservation = ledgerObservation;
            _pathIdentities = pathIdentities;
        }

        public void ValidateForInstallPath(string installPath)
        {
            if (_pathIdentities is not null && !_pathIdentities.ContainsKey(installPath))
                throw Refusal("The install path is absent from this operation's admitted path union.", _root);

            var currentLedger = _ledgerObservation();
            var expectedPath = _pathIdentities is not null ? _pathIdentities[installPath] : null;
            var targetKind = expectedPath?.TargetKind ?? PhysicalStorePathTarget.PackageDirectory;
            ResolvedPackageStorePath resolved;
            if (_pathIdentities is null)
            {
                try { resolved = _resolver.Resolve(installPath, targetKind, _root); }
                catch (PackageStoreAdmissionException)
                {
                    targetKind = PhysicalStorePathTarget.ArchiveFile;
                    resolved = _resolver.Resolve(installPath, targetKind, _root);
                }
            }
            else
            {
                resolved = _resolver.Resolve(installPath, targetKind, _root);
            }
            using (resolved)
            {
                ValidateResolvedInstallPath(resolved, targetKind, expectedPath, currentLedger);
                resolved.Revalidate();
            }
        }

        public TResult WithValidatedPackageDirectory<TResult>(
            string installPath,
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, TResult> callback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            ArgumentNullException.ThrowIfNull(callback);
            AdmittedPathIdentity? expectedPath = null;
            if (_pathIdentities is not null)
            {
                if (!_pathIdentities.TryGetValue(installPath, out var admittedPath))
                    throw Refusal("The install path is absent from this operation's admitted path union.", _root);
                expectedPath = admittedPath;
            }

            if (expectedPath is { TargetKind: not PhysicalStorePathTarget.PackageDirectory })
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Scoped package metadata requires an admitted extracted package directory.",
                    _root);
            }

            var currentLedger = _ledgerObservation();
            using var resolved = _resolver.Resolve(installPath, PhysicalStorePathTarget.PackageDirectory, _root);
            ValidateResolvedInstallPath(resolved, PhysicalStorePathTarget.PackageDirectory, expectedPath, currentLedger);
            resolved.Revalidate();

            var directory = resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refusal("The admitted package path is not a held directory.", _root);
            TResult result;
            try
            {
                result = callback(_files, directory);
            }
            catch
            {
                // Preserve a refused result if the retained authority/path evidence changed while
                // a callback was failing; otherwise propagate its original read/parse failure.
                resolved.Revalidate();
                throw;
            }
            resolved.Revalidate();
            return result;
        }

        public TResult WithValidatedPackageDirectoryOrMissing<TResult>(
            string installPath,
            Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle?, TResult> callback)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            ArgumentNullException.ThrowIfNull(callback);
            AdmittedPathIdentity? expectedPath = null;
            if (_pathIdentities is not null)
            {
                if (!_pathIdentities.TryGetValue(installPath, out var admittedPath))
                    throw Refusal("The install path is absent from this operation's admitted path union.", _root);
                expectedPath = admittedPath;
            }

            if (expectedPath is { TargetKind: not PhysicalStorePathTarget.PackageDirectory })
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Scoped package metadata requires an admitted extracted package directory.",
                    _root);
            }

            var currentLedger = _ledgerObservation();
            using var resolved = _resolver.Resolve(
                installPath,
                PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix,
                _root);
            ValidateCompleteOperationRoot(resolved, currentLedger);
            resolved.Revalidate();

            var missing = resolved.IsProspectiveMissingSuffix;
            var directory = missing
                ? null
                : resolved.Target as PhysicalStoreDirectoryHandle
                    ?? throw Refusal("The admitted package path is not a held directory.", _root);
            if (missing)
            {
                // A path-restricted admission captured a concrete directory identity. Its later absence
                // is a changed target, not a cache miss. Only a root-scoped operation can classify an
                // exact missing suffix that was not present when the operation began.
                if (expectedPath is not null)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "The package directory admitted for this operation is now absent.", _root);
                var parent = _files.InspectHandle(resolved.Target);
                if (parent.Kind != PhysicalStoreEntryKind.Directory ||
                    !SameVolume(parent.Identity, _root.HandleIdentity))
                    throw Refusal("The positively absent package path has no same-volume held parent.", _root);
            }
            else
            {
                var target = ValidateResolvedInstallPath(resolved, PhysicalStorePathTarget.PackageDirectory,
                    expectedPath, currentLedger);
                if (directory is null || target.Identity != _files.InspectHandle(directory).Identity)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "The admitted package directory changed during native resolution.", _root);
            }

            TResult result;
            try
            {
                result = callback(_files, directory);
            }
            catch
            {
                resolved.Revalidate();
                ValidateCompleteOperationRoot(resolved, _ledgerObservation());
                throw;
            }

            resolved.Revalidate();
            ValidateCompleteOperationRoot(resolved, _ledgerObservation());
            return result;
        }

        private void ValidateCompleteOperationRoot(
            ResolvedPackageStorePath resolved,
            (string Digest, PhysicalFileIdentity Identity) currentLedger)
        {
            if (resolved.RootIdentity != _root || resolved.MembershipCandidate is not { Status: RootMembershipStatus.Complete } candidate ||
                candidate.EnrollmentEpoch != _epoch ||
                !string.Equals(candidate.LedgerDigest, currentLedger.Digest, StringComparison.Ordinal) ||
                resolved.MembershipLedgerIdentity != currentLedger.Identity)
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The package path no longer matches its live Complete operation owner.", _root);
            }
        }

        private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
            => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
                string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal);

        private PhysicalStoreEntryInfo ValidateResolvedInstallPath(
            ResolvedPackageStorePath resolved,
            PhysicalStorePathTarget targetKind,
            AdmittedPathIdentity? expectedPath,
            (string Digest, PhysicalFileIdentity Identity) currentLedger)
        {
            ValidateCompleteOperationRoot(resolved, currentLedger);

            var target = _files.InspectHandle(resolved.Target);
            var expectedKind = targetKind == PhysicalStorePathTarget.ArchiveFile
                ? PhysicalStoreEntryKind.RegularFile : PhysicalStoreEntryKind.Directory;
            if (target.Kind != expectedKind ||
                (targetKind == PhysicalStorePathTarget.ArchiveFile && target.LinkCount != 1) ||
                (expectedPath is not null && target.Identity != expectedPath.Identity))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                    "The install path no longer identifies the package directory admitted for this operation.", _root);
            }

            return target;
        }
    }

    private sealed class PathAdmissionControl : IPackageStorePathAdmissionControl
    {
        private readonly object _gate = new();
        private readonly IReadOnlyDictionary<PhysicalRootIdentity, PackageStoreOperationState> _owners;
        private readonly IReadOnlyList<PackageStoreOperationState> _retainedOwners;
        private readonly IReadOnlyList<PathObservation> _observations;
        private PackageStorePathAdmission? _admission;
        private bool _closing;
        private Task? _disposeTask;

        internal PathAdmissionControl(
            IReadOnlyDictionary<PhysicalRootIdentity, PackageStoreOperationState> owners,
            IReadOnlyList<PackageStoreOperationState> retainedOwners,
            IReadOnlyList<PathObservation> observations)
        {
            _owners = owners;
            _retainedOwners = retainedOwners;
            _observations = observations;
        }

        internal void Bind(PackageStorePathAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(admission);
            lock (_gate)
            {
                if (_admission is not null)
                    throw new InvalidOperationException("A path admission control can bind only one admission handle.");
                _admission = admission;
            }
        }

        public PackageStoreOperationBorrow BorrowFor(PackageStorePathAdmission admission, string installPath)
        {
            ArgumentNullException.ThrowIfNull(admission);
            ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
            lock (_gate)
            {
                if (!ReferenceEquals(_admission, admission))
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The admission handle does not own this path-control scope.");
                if (_closing)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The path admission is closing.");
                var observation = _observations.FirstOrDefault(item => string.Equals(item.Path, installPath, StringComparison.Ordinal))
                    ?? throw Refusal("The requested path is not part of this admission.");
                var root = observation.Resolved.RootIdentity
                    ?? throw Refusal("An unenrolled path does not receive an enrolled operation borrow.");
                if (!_owners.TryGetValue(root, out var owner))
                    throw Refusal("The path's enrolled root has no retained owner.", root);
                return owner.BorrowForPath(owner.Owner, installPath);
            }
        }

        public ValueTask DisposeAsync(PackageStorePathAdmission admission)
        {
            ArgumentNullException.ThrowIfNull(admission);
            lock (_gate)
            {
                if (!ReferenceEquals(_admission, admission))
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The admission handle does not own this path-control scope.");
                if (_disposeTask is null)
                {
                    _closing = true;
                    _disposeTask = DisposeCoreAsync();
                }
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            var errors = new List<Exception>();
            for (var index = _retainedOwners.Count - 1; index >= 0; index--)
            {
                try { await _retainedOwners[index].Owner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { errors.Add(exception); }
            }
            for (var index = _observations.Count - 1; index >= 0; index--)
            {
                try { _observations[index].Resolved.Dispose(); }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Path admission cleanup encountered multiple failures.", errors);
        }
    }

    private sealed class PhysicalRootIdentityComparer : IComparer<PhysicalRootIdentity>
    {
        internal static PhysicalRootIdentityComparer Instance { get; } = new();

        public int Compare(PhysicalRootIdentity? left, PhysicalRootIdentity? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = StringComparer.Ordinal.Compare(left.HandleIdentity.Provider, right.HandleIdentity.Provider);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.HandleIdentity.VolumeOrDeviceId, right.HandleIdentity.VolumeOrDeviceId);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.HandleIdentity.FileId, right.HandleIdentity.FileId);
        }
    }
}
