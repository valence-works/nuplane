using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class PackageStoreRuntimeAdmission
{
    internal static IPackageStoreAdmission Create(
        IPhysicalStoreFileSystem files,
        IStoreRegistry selectedRegistry,
        IStoreStateSerializer serializer,
        string? configuredInstallRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(selectedRegistry);
        ArgumentNullException.ThrowIfNull(serializer);
        if (selectedRegistry is not StoreRegistry builtIn ||
            !ReferenceEquals(builtIn.PayloadSerializer, serializer) ||
            serializer is not IPackageProtectionStatePayloadSerializer)
            return CreateUnenrolledOnly(files, configuredInstallRoot);

        var registry = new RootMembershipRegistry(files, builtIn.PayloadSerializer);
        var rootLocator = string.IsNullOrWhiteSpace(configuredInstallRoot)
            ? Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages")
            : configuredInstallRoot;
        var baseLocator = Path.IsPathFullyQualified(rootLocator) ? null : Directory.GetCurrentDirectory();
        return new PackageStoreAdmission(files, registry, rootLocator, baseLocator);
    }

    internal static IPackageStoreAdmission CreateManual(IStoreRegistry storeRegistry, FeedResolutionOptions feedOptions)
    {
        ArgumentNullException.ThrowIfNull(storeRegistry);
        ArgumentNullException.ThrowIfNull(feedOptions);
        if (storeRegistry is not StoreRegistry builtIn)
            return CreateUnenrolledOnly(CreatePhysicalFileSystem(), feedOptions.PackageInstallRoot);
        return Create(CreatePhysicalFileSystem(), storeRegistry, builtIn.PayloadSerializer,
            feedOptions.PackageInstallRoot);
    }

    /// <summary>
    /// Creates a metadata-only classifier for exact persisted install paths when a manual
    /// composition has no configured package root. It does not guess a root or read member state.
    /// </summary>
    internal static IPackageStoreAdmission CreateManualInstallPathClassifier()
        => CreateUnenrolledOnlyClassifier(CreatePhysicalFileSystem(), rootLocator: null, baseLocator: null);

    internal static IPhysicalStoreFileSystem CreatePhysicalFileSystem()
        => OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();

    private static IPackageStoreAdmission CreateUnenrolledOnly(
        IPhysicalStoreFileSystem files,
        string? configuredInstallRoot)
    {
        var rootLocator = string.IsNullOrWhiteSpace(configuredInstallRoot)
            ? Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages")
            : configuredInstallRoot;
        var baseLocator = Path.IsPathFullyQualified(rootLocator) ? null : Directory.GetCurrentDirectory();

        return CreateUnenrolledOnlyClassifier(files, rootLocator, baseLocator);
    }

    private static IPackageStoreAdmission CreateUnenrolledOnlyClassifier(
        IPhysicalStoreFileSystem files,
        string? rootLocator,
        string? baseLocator)
    {
        // The resolver needs the registry only to inspect the reserved ledger namespace. This
        // admission never calls a member-state reader and has no operation-owner path; a present
        // authority is refused before the custom state service or any callback can run.
        var ledgerOnlyRegistry = new RootMembershipRegistry(files, new StoreStateSerializer());
        return new UnenrolledOnlyPackageStoreAdmission(files,
            new PackageStoreAuthorityResolver(files, ledgerOnlyRegistry), rootLocator, baseLocator);
    }

    private sealed class UnenrolledOnlyPackageStoreAdmission(
        IPhysicalStoreFileSystem files,
        PackageStoreAuthorityResolver resolver,
        string? configuredRootLocator,
        string? configuredRootBaseLocator) : IPackageStoreAdmission
    {
        public ValueTask<PackageStoreRootOperationAdmission> AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind kind,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            cancellationToken.ThrowIfCancellationRequested();

            if (configuredRootLocator is null)
                throw Refuse("This manual admission classifies exact install paths only and has no configured-root authority.");

            using var resolved = resolver.Resolve(configuredRootLocator,
                PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix, exactBaseLocator: configuredRootBaseLocator);
            if (resolved.IsProspectiveConfiguredRoot)
            {
                if (resolved.RootIdentity is not null || resolved.MembershipCandidate is not null)
                    throw Refuse("A prospective configured root cannot carry an observed membership authority.");
                resolved.Revalidate();
                return ValueTask.FromResult(new PackageStoreRootOperationAdmission(
                    PackageStoreAdmissionStatus.Unenrolled, root: null, owner: null));
            }

            var target = resolved.Target as PhysicalStoreDirectoryHandle
                ?? throw Refuse("The configured package-store root is not a held directory.");
            var info = files.InspectHandle(target);
            if (info.Kind != PhysicalStoreEntryKind.Directory)
                throw Refuse("The configured package-store root changed kind during admission.");
            if (resolved.RootIdentity is not null)
                throw Refuse("The configured root has membership authority but its state service cannot provide exact coordinated ownership.",
                    resolved.RootIdentity);

            resolved.Revalidate();
            return ValueTask.FromResult(new PackageStoreRootOperationAdmission(
                PackageStoreAdmissionStatus.Unenrolled, new PhysicalRootIdentity(info.Identity), owner: null));
        }

        public ValueTask<PackageStorePathAdmission> AcquireForInstallPathsAsync(
            IReadOnlyCollection<string> installPaths,
            PackageStoreAdmissionKind kind,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(installPaths);
            if (!Enum.IsDefined(kind))
                throw new ArgumentOutOfRangeException(nameof(kind));
            cancellationToken.ThrowIfCancellationRequested();

            var observations = new List<(string Path, ResolvedPackageStorePath Resolved, PhysicalStoreEntryKind Kind)>();
            try
            {
                foreach (var path in installPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentException.ThrowIfNullOrWhiteSpace(path);
                    ResolvedPackageStorePath resolved;
                    try
                    {
                        resolved = resolver.Resolve(path, PhysicalStorePathTarget.PackageDirectory);
                    }
                    catch (PackageStoreAdmissionException)
                    {
                        resolved = resolver.Resolve(path, PhysicalStorePathTarget.ArchiveFile);
                    }

                    try
                    {
                        var info = files.InspectHandle(resolved.Target);
                        if (resolved.RootIdentity is { } root)
                            throw Refuse("An install path has membership authority but its state service cannot provide exact coordinated ownership.", root);
                        if (info.Kind is not PhysicalStoreEntryKind.Directory and not PhysicalStoreEntryKind.RegularFile ||
                            (info.Kind == PhysicalStoreEntryKind.RegularFile && info.LinkCount != 1))
                            throw Refuse("An install path is not a supported package directory or single-link archive.");
                        observations.Add((path, resolved, info.Kind));
                    }
                    catch
                    {
                        resolved.Dispose();
                        throw;
                    }
                }

                foreach (var observation in observations)
                {
                    observation.Resolved.Revalidate();
                    var current = files.InspectHandle(observation.Resolved.Target);
                    if (current.Kind != observation.Kind ||
                        (current.Kind == PhysicalStoreEntryKind.RegularFile && current.LinkCount != 1))
                        throw Refuse("An install path changed during metadata-only admission.");
                    if (observation.Resolved.RootIdentity is not null)
                        throw Refuse("An install path acquired membership authority during metadata-only admission.",
                            observation.Resolved.RootIdentity);
                }

                var entries = observations.Select(item => new PackageStorePathAdmissionEntry(
                    item.Path, PackageStoreAdmissionStatus.Unenrolled, root: null)).ToArray();
                var control = new UnenrolledPathControl();
                var admission = new PackageStorePathAdmission(entries, control);
                control.Bind(admission);
                return ValueTask.FromResult(admission);
            }
            finally
            {
                // The admission contains only immutable Unenrolled classifications; retained
                // handles are not an authorization capability and close before returning.
                foreach (var observation in observations.AsEnumerable().Reverse())
                    observation.Resolved.Dispose();
            }
        }
    }

    private sealed class UnenrolledPathControl : IPackageStorePathAdmissionControl
    {
        private PackageStorePathAdmission? _admission;

        internal void Bind(PackageStorePathAdmission admission) => _admission = admission;

        public PackageStoreOperationBorrow BorrowFor(PackageStorePathAdmission admission, string installPath)
        {
            if (!ReferenceEquals(_admission, admission))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                    "The admission handle does not own this metadata-only path classification.");
            throw Refuse("An unenrolled path does not receive an enrolled operation borrow.");
        }

        public ValueTask DisposeAsync(PackageStorePathAdmission admission)
        {
            if (!ReferenceEquals(_admission, admission))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                    "The admission handle does not own this metadata-only path classification.");
            _admission = null;
            return ValueTask.CompletedTask;
        }
    }

    private static PackageStoreAdmissionException Refuse(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnsupportedParticipant, message, root);

}
