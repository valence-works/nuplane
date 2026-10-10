using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Registration;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Runs one native, replayed direct-directory desired-source read.</summary>
internal static class DesiredSourceDirectoryAccess
{
    internal static Task<TResult> WithDirectoryAsync<TResult>(
        string exactLocator,
        PackageStoreOperationBorrow? borrow,
        Func<DesiredSourceDirectorySession, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactLocator);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        var exactBaseLocator = Path.IsPathFullyQualified(exactLocator) ? null : Directory.GetCurrentDirectory();
        if (borrow is null)
        {
            var files = PackageStoreRuntimeAdmission.CreatePhysicalFileSystem();
            return RunAsync(files, exactLocator, exactBaseLocator, scope: null, callback, cancellationToken);
        }

        return PackageStoreOperationAccess.WithValidatedRootAsync(
            borrow,
            async (files, _, token) =>
            {
                var locked = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                var ledger = locked.Ledger;
                if (ledger.RootIdentity != borrow.Root || ledger.Status != RootMembershipStatus.Complete ||
                    ledger.PendingStateCommit is not null)
                {
                    throw Refuse(PackageStoreAdmissionReason.IncompleteEnrollment,
                        "The directory source requires the exact borrowed Complete root with no pending state commit.",
                        borrow.Root);
                }

                var scope = new RootMembershipRegistry.MemberLocatorReplayScope(ledger);
                try
                {
                    return await RunAsync(files, exactLocator, exactBaseLocator, scope, callback, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    scope.Expire();
                }
            },
            cancellationToken);
    }

    private static async Task<TResult> RunAsync<TResult>(
        IPhysicalStoreFileSystem files,
        string exactLocator,
        string? exactBaseLocator,
        RootMembershipRegistry.MemberLocatorReplayScope? scope,
        Func<DesiredSourceDirectorySession, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        if (files is not (UnixPhysicalStoreFileSystem or WindowsPhysicalStoreFileSystem) ||
            files is not IPhysicalStoreDirectoryEnumerationFileSystem enumeration ||
            files is not IPhysicalStoreDirectoryCandidateProbeFileSystem candidateProbe ||
            files is not IPhysicalStoreNameFileSystem names)
        {
            throw Refuse(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem provider cannot perform a native desired-source directory read.");
        }

        var resolver = new PackageStoreAuthorityResolver(files,
            new RootMembershipRegistry(files, new StoreStateSerializer()));
        using var resolved = resolver.ResolveDesiredSourceDirectory(exactLocator, scope, exactBaseLocator);
        if (scope is null && (resolved.RootIdentity is not null || resolved.AuthorityRoot is not null ||
                resolved.MembershipCandidate is not null || resolved.MembershipLedgerIdentity is not null))
        {
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "An unscoped directory source must resolve to positive Unenrolled authority.", resolved.RootIdentity);
        }

        if (scope is not null && resolved.RootIdentity is { } observedRoot && observedRoot != scope.RootIdentity)
        {
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "The directory source does not belong to the exact borrowed root.", scope.RootIdentity);
        }

        if (resolved.RootIdentity is not null &&
            (resolved.MembershipCandidate is not { Status: RootMembershipStatus.Complete, PendingStateCommit: null } candidate ||
             candidate.RootIdentity != resolved.RootIdentity || resolved.MembershipLedgerIdentity is null))
        {
            throw Refuse(PackageStoreAdmissionReason.IncompleteEnrollment,
                "The directory source membership candidate is not a complete acknowledged snapshot.", resolved.RootIdentity);
        }

        resolved.Revalidate();
        var missing = resolved.IsProspectiveMissingSuffix;
        var directory = missing
            ? null
            : resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The desired-source locator is not a held directory.", resolved.RootIdentity);
        var profile = directory is null ? null : names.ObserveDirectoryNameSemantics(directory);
        resolved.Revalidate();

        using var session = new DesiredSourceDirectorySession(
            enumeration, candidateProbe, resolved, directory, profile, missing);
        try
        {
            var result = await callback(session).ConfigureAwait(false);
            session.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            session.Revalidate();
            throw;
        }
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null)
        => new(reason, message, root);
}

/// <summary>Retains one resolved directory and confines enumeration and child sampling to that exact native evidence.</summary>
internal sealed class DesiredSourceDirectorySession : IDisposable
{
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
    private readonly IPhysicalStoreDirectoryCandidateProbeFileSystem _candidateProbe;
    private readonly ResolvedPackageStorePath _resolved;
    private readonly PhysicalStoreDirectoryHandle? _directory;
    private readonly Dictionary<string, (PhysicalFileIdentity Identity, IDisposable? Pin)> _sampledCandidates = new(StringComparer.Ordinal);
    private bool _disposed;

    internal DesiredSourceDirectorySession(
        IPhysicalStoreDirectoryEnumerationFileSystem enumeration,
        IPhysicalStoreDirectoryCandidateProbeFileSystem candidateProbe,
        ResolvedPackageStorePath resolved,
        PhysicalStoreDirectoryHandle? directory,
        PhysicalStoreNameSemantics? profile,
        bool isMissing)
    {
        _enumeration = enumeration;
        _candidateProbe = candidateProbe;
        _resolved = resolved;
        _directory = directory;
        Profile = profile;
        IsMissing = isMissing;
    }

    internal bool IsMissing { get; }

    internal PhysicalStoreNameSemantics? Profile { get; }

    internal IReadOnlyList<string> EnumeratePackageCandidates()
    {
        EnsureActive();
        if (IsMissing)
            return [];

        var directory = _directory ?? throw new InvalidOperationException("A present source directory has no retained native directory.");
        try
        {
            _resolved.Revalidate();
            var names = _enumeration.EnumerateChildNamesNoFollow(directory, int.MaxValue);
            // Preserve Directory.EnumerateFiles("*.nupkg")'s PlatformDefault matching. This is
            // a managed enumeration compatibility rule, not the native directory lookup profile.
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var candidates = names.Where(name => name.EndsWith(".nupkg", comparison)).ToArray();
            _resolved.Revalidate();
            return candidates;
        }
        catch
        {
            _resolved.Revalidate();
            throw;
        }
    }

    internal PhysicalStoreDirectoryCandidateSample SamplePackageCandidate(
        string singleName,
        PhysicalFileIdentity? expectedIdentity = null)
    {
        EnsureActive();
        if (IsMissing)
            throw new InvalidOperationException("A missing source directory has no package candidates.");
        PhysicalStoreNames.ValidateSingleComponent(singleName);
        var directory = _directory ?? throw new InvalidOperationException("A present source directory has no retained native directory.");
        try
        {
            _resolved.Revalidate();
            var hasRetainedCandidate = _sampledCandidates.TryGetValue(singleName, out var retained);
            if (expectedIdentity is null && hasRetainedCandidate)
                expectedIdentity = retained.Identity;
            var identityPin = hasRetainedCandidate ? retained.Pin : null;
            var result = _candidateProbe.SamplePackageCandidateNoFollow(directory, singleName, expectedIdentity, identityPin);
            if (result.Kind == PhysicalStoreDirectoryCandidateKind.RegularFile)
            {
                var identity = result.Identity
                    ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A regular package candidate did not provide a native identity.", _resolved.RootIdentity);
                if (expectedIdentity is not null && identity != expectedIdentity)
                {
                    if (result.IdentityPin is not null &&
                        (!hasRetainedCandidate || !ReferenceEquals(result.IdentityPin, retained.Pin)))
                        result.IdentityPin.Dispose();
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A package candidate changed its identity during sampling.", _resolved.RootIdentity);
                }

                if (hasRetainedCandidate)
                {
                    if (result.IdentityPin is not null && !ReferenceEquals(result.IdentityPin, retained.Pin))
                    {
                        result.IdentityPin.Dispose();
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The candidate provider replaced a retained identity pin.", _resolved.RootIdentity);
                    }
                }
                else
                {
                    _sampledCandidates.Add(singleName, (identity, result.IdentityPin));
                }
            }
            else if (result.Kind == PhysicalStoreDirectoryCandidateKind.Busy)
            {
                if (!result.IsVerifiedRegularFile)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A busy package candidate could not be positively verified as a regular single-link file.",
                        _resolved.RootIdentity);
                if (hasRetainedCandidate && result.Identity is not null && result.Identity != retained.Identity)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A busy package candidate changed its retained native identity.", _resolved.RootIdentity);
                if (result.IdentityPin is not null &&
                    (!hasRetainedCandidate || !ReferenceEquals(result.IdentityPin, retained.Pin)))
                {
                    result.IdentityPin.Dispose();
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A busy candidate returned a new or foreign identity pin.", _resolved.RootIdentity);
                }
            }
            else if (result.IdentityPin is not null)
            {
                if (!hasRetainedCandidate || !ReferenceEquals(result.IdentityPin, retained.Pin))
                    result.IdentityPin.Dispose();
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "A non-regular candidate unexpectedly returned a native identity pin.", _resolved.RootIdentity);
            }

            _resolved.Revalidate();
            return result;
        }
        catch
        {
            _resolved.Revalidate();
            throw;
        }
    }

    internal void ReleasePackageCandidate(string singleName)
    {
        EnsureActive();
        if (_sampledCandidates.Remove(singleName, out var candidate))
            candidate.Pin?.Dispose();
    }

    internal void Revalidate()
    {
        EnsureActive();
        _resolved.Revalidate();
        if (_directory is not null)
        {
            foreach (var (name, candidate) in _sampledCandidates)
                _candidateProbe.RevalidatePackageCandidate(_directory, name, candidate.Identity, candidate.Pin);
        }
    }

    private void EnsureActive()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var candidate in _sampledCandidates.Values)
            candidate.Pin?.Dispose();
        _sampledCandidates.Clear();
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root)
        => new(reason, message, root);
}
