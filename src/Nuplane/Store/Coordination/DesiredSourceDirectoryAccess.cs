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

    /// <summary>Runs a directory callback under a root callback that already owns the exact borrow.</summary>
    /// <remarks>
    /// This narrow entry point is for native operations, such as archive acquisition, that already run
    /// under <see cref="PackageStoreOperationAccess.WithValidatedRootAsync{TResult}"/>. Re-entering that
    /// serialized root callback would wait on its own gate.
    /// </remarks>
    internal static async Task<TResult> WithDirectoryInValidatedRootAsync<TResult>(
        string exactLocator,
        PackageStoreOperationBorrow borrow,
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle heldRoot,
        Func<DesiredSourceDirectorySession, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactLocator);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        var locked = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
        if (locked.Ledger.RootIdentity != borrow.Root ||
            locked.Ledger.Status != RootMembershipStatus.Complete ||
            locked.Ledger.PendingStateCommit is not null)
        {
            throw Refuse(PackageStoreAdmissionReason.IncompleteEnrollment,
                "The directory source requires the exact borrowed Complete root with no pending state commit.",
                borrow.Root);
        }

        var heldRootInfo = files.InspectHandle(heldRoot);
        if (heldRootInfo.Kind != PhysicalStoreEntryKind.Directory ||
            heldRootInfo.Identity != borrow.Root.HandleIdentity)
        {
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "The validated native root does not match the exact borrowed root.", borrow.Root);
        }

        var exactBaseLocator = Path.IsPathFullyQualified(exactLocator) ? null : Directory.GetCurrentDirectory();
        var scope = new RootMembershipRegistry.MemberLocatorReplayScope(locked.Ledger);
        try
        {
            return await RunAsync(files, exactLocator, exactBaseLocator, scope, callback, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            scope.Expire();
        }
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
            files is not IPhysicalStoreNameFileSystem names ||
            files is not IPhysicalStoreDesiredSourceTextReadFileSystem textReads)
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
        var directoryInfo = directory is null ? null : files.InspectHandle(directory);
        if (directoryInfo is not null && directoryInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The desired-source parent is not a positively verified directory.", resolved.RootIdentity);
        resolved.Revalidate();

        using var session = new DesiredSourceDirectorySession(
            files, enumeration, candidateProbe, textReads, resolved, directory, directoryInfo, profile, missing);
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
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
    private readonly IPhysicalStoreDirectoryCandidateProbeFileSystem _candidateProbe;
    private readonly IPhysicalStoreDesiredSourceTextReadFileSystem _textReads;
    private readonly ResolvedPackageStorePath _resolved;
    private readonly PhysicalStoreDirectoryHandle? _directory;
    private readonly PhysicalStoreEntryInfo? _directoryInfo;
    private readonly Dictionary<string, (PhysicalFileIdentity Identity, IDisposable? Pin)> _sampledCandidates = new(StringComparer.Ordinal);
    private bool _disposed;

    internal DesiredSourceDirectorySession(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreDirectoryEnumerationFileSystem enumeration,
        IPhysicalStoreDirectoryCandidateProbeFileSystem candidateProbe,
        IPhysicalStoreDesiredSourceTextReadFileSystem textReads,
        ResolvedPackageStorePath resolved,
        PhysicalStoreDirectoryHandle? directory,
        PhysicalStoreEntryInfo? directoryInfo,
        PhysicalStoreNameSemantics? profile,
        bool isMissing)
    {
        _files = files;
        _enumeration = enumeration;
        _candidateProbe = candidateProbe;
        _textReads = textReads;
        _resolved = resolved;
        _directory = directory;
        _directoryInfo = directoryInfo;
        Profile = profile;
        IsMissing = isMissing;
    }

    internal bool IsMissing { get; }

    internal PhysicalStoreNameSemantics? Profile { get; }

    internal PhysicalFileIdentity DirectoryIdentity
        => _directoryInfo?.Identity ?? throw new InvalidOperationException("A missing source directory has no native identity.");

    internal async ValueTask<PhysicalStoreDesiredSourceTextReadResult> ReadDesiredSourceTextAsync(
        string singleName,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterNativeReadAsync = null)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMissing)
        {
            _resolved.Revalidate();
            return PhysicalStoreDesiredSourceTextReadResult.Missing;
        }

        PhysicalStoreNames.ValidateSingleComponent(singleName);
        var directory = _directory ?? throw new InvalidOperationException("A present source directory has no retained native directory.");
        var directoryInfo = _directoryInfo ?? throw new InvalidOperationException("A present source directory has no retained native identity.");
        var profile = Profile ?? throw new InvalidOperationException("A present source directory has no retained native name profile.");
        try
        {
            _resolved.Revalidate();
            var result = await _textReads.ReadDesiredSourceTextAsync(
                directory,
                singleName,
                directoryInfo,
                profile,
                cancellationToken,
                afterNativeReadAsync).ConfigureAwait(false);
            _resolved.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            _resolved.Revalidate();
            throw;
        }
    }

    internal IReadOnlyList<string> EnumeratePackageCandidates()
        => EnumeratePackageCandidates(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal,
            CancellationToken.None);

    internal IReadOnlyList<string> EnumeratePackageCandidates(
        StringComparison extensionComparison,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMissing)
            return [];

        var directory = _directory ?? throw new InvalidOperationException("A present source directory has no retained native directory.");
        try
        {
            _resolved.Revalidate();
            var names = _enumeration.EnumerateChildNamesNoFollow(directory, int.MaxValue);
            var candidates = new List<string>();
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name.EndsWith(".nupkg", extensionComparison))
                    candidates.Add(name);
            }

            _resolved.Revalidate();
            cancellationToken.ThrowIfCancellationRequested();
            return candidates;
        }
        catch
        {
            _resolved.Revalidate();
            throw;
        }
    }

    internal void RevalidatePackageCandidate(
        string singleName,
        PhysicalFileIdentity expectedParentIdentity,
        PhysicalStoreNameSemantics expectedProfile,
        PhysicalFileIdentity expectedFileIdentity,
        long expectedLength)
    {
        EnsureExpectedParent(expectedParentIdentity);
        EnsureExpectedProfile(expectedProfile);
        EnsureArchiveLength(expectedLength, NativePackageArchiveStreamLimits.MaximumPackageArchiveBytes);
        // A fresh session must acquire its own provider-owned native pin before comparing it with
        // the selection snapshot; Windows cannot safely replay an identity without that pin.
        var sample = SamplePackageCandidate(singleName);
        RequireSelectedRegularFile(sample, expectedFileIdentity, expectedLength);
        EnsureExpectedProfile(expectedProfile);
        _resolved.Revalidate();
    }

    internal async Task CopyPackageArchiveToAsync(
        string singleName,
        PhysicalFileIdentity expectedParentIdentity,
        PhysicalStoreNameSemantics expectedProfile,
        PhysicalFileIdentity expectedFileIdentity,
        long expectedLength,
        Stream destination,
        long maximumArchiveBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExpectedParent(expectedParentIdentity);
        EnsureExpectedProfile(expectedProfile);
        EnsureArchiveLength(expectedLength, maximumArchiveBytes);

        var directory = _directory ?? throw new InvalidOperationException("A present source directory has no retained native directory.");
        var directoryInfo = _directoryInfo ?? throw new InvalidOperationException("A present source directory has no retained native identity.");
        var streams = _files as IPhysicalStorePackageStreamFileSystem
            ?? throw Refuse(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot open a bounded native package archive stream.", _resolved.RootIdentity);

        try
        {
            _resolved.Revalidate();
            var sample = SamplePackageCandidate(singleName);
            RequireSelectedRegularFile(sample, expectedFileIdentity, expectedLength);

            PhysicalStoreEntryInfo beforeOpen;
            try
            {
                beforeOpen = RequireSelectedEntry(
                    _files.InspectChildNoFollow(directory, singleName), expectedFileIdentity, expectedLength);
            }
            catch (PackageStoreAdmissionException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The selected local package archive could not be reopened by its exact native identity.",
                    _resolved.RootIdentity, exception);
            }

            PhysicalStoreFileHandle file;
            try
            {
                file = _files.OpenFileChildNoFollow(directory, singleName, FileAccess.Read);
            }
            catch (PackageStoreAdmissionException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The selected local package archive could not be reopened by its exact native identity.",
                    _resolved.RootIdentity, exception);
            }

            using (file)
            {
                PhysicalStoreEntryInfo opened;
                Stream archiveStream;
                try
                {
                    opened = RequireSelectedEntry(_files.InspectHandle(file), expectedFileIdentity, expectedLength);
                    RequireSameSelectedEntry(beforeOpen, opened);
                    archiveStream = streams.OpenPackageArchiveReadStream(
                        directory, singleName, file, directoryInfo, opened, maximumArchiveBytes);
                }
                catch (PackageStoreAdmissionException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The selected local package archive could not be opened through its exact native identity.",
                        _resolved.RootIdentity, exception);
                }

                await using var archive = archiveStream;
                var buffer = new byte[64 * 1024];
                long copiedBytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try
                    {
                        read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (PackageStoreAdmissionException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The selected local package archive could not be read through its exact native identity.",
                            _resolved.RootIdentity, exception);
                    }

                    if (read == 0)
                        break;
                    copiedBytes = checked(copiedBytes + read);
                    if (copiedBytes > expectedLength || copiedBytes > maximumArchiveBytes)
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The selected local package archive changed length while it was being copied.",
                            _resolved.RootIdentity);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (copiedBytes != expectedLength)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The selected local package archive changed length while it was being copied.",
                        _resolved.RootIdentity);

                EnsureExpectedProfile(expectedProfile);
                _candidateProbe.RevalidatePackageCandidate(directory, singleName, expectedFileIdentity,
                    _sampledCandidates[singleName].Pin);
                _resolved.Revalidate();
                cancellationToken.ThrowIfCancellationRequested();
            }
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

    private void EnsureExpectedParent(PhysicalFileIdentity expectedParentIdentity)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(expectedParentIdentity);
        if (IsMissing || _directoryInfo?.Identity != expectedParentIdentity)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The local feed directory identity changed after version selection.", _resolved.RootIdentity);
    }

    private void EnsureExpectedProfile(PhysicalStoreNameSemantics expectedProfile)
    {
        ArgumentNullException.ThrowIfNull(expectedProfile);
        if (Profile != expectedProfile)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The local feed directory name profile changed after version selection.", _resolved.RootIdentity);
    }

    private void EnsureArchiveLength(long expectedLength, long maximumArchiveBytes)
    {
        if (expectedLength <= 0 || expectedLength > maximumArchiveBytes)
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The selected local package archive is empty or exceeds the native archive byte limit.",
                _resolved.RootIdentity);
    }

    private void RequireSelectedRegularFile(
        PhysicalStoreDirectoryCandidateSample sample,
        PhysicalFileIdentity expectedIdentity,
        long expectedLength)
    {
        if (sample.Kind != PhysicalStoreDirectoryCandidateKind.RegularFile ||
            sample.Identity != expectedIdentity ||
            sample.Length != expectedLength)
        {
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The selected local package candidate is missing, linked, non-regular, or changed identity.",
                _resolved.RootIdentity);
        }
    }

    private PhysicalStoreEntryInfo RequireSelectedEntry(
        PhysicalStoreEntryInfo? entry,
        PhysicalFileIdentity expectedIdentity,
        long expectedLength)
    {
        if (entry is not { Kind: PhysicalStoreEntryKind.RegularFile, LinkCount: 1 } selected ||
            selected.Identity != expectedIdentity || selected.Length != expectedLength)
        {
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The selected local package archive is not the same regular single-link file.",
                _resolved.RootIdentity);
        }

        return selected;
    }

    private void RequireSameSelectedEntry(PhysicalStoreEntryInfo first, PhysicalStoreEntryInfo second)
    {
        if (first.Kind != second.Kind || first.Identity != second.Identity ||
            first.LinkCount != second.LinkCount || first.Length != second.Length)
        {
            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                "The selected local package archive changed while it was being reopened.",
                _resolved.RootIdentity);
        }
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
        PhysicalRootIdentity? root,
        Exception? innerException = null)
        => new(reason, message, root, innerException);
}
