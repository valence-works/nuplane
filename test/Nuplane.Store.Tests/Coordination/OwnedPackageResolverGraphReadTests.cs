using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class OwnedPackageResolverGraphReadTests
{
    [SupportedPhysicalStoreFact]
    public async Task CompleteRoot_GraphExpansionAndAssetProjectionUseOwnedNativeReaderUnderEveryMemberLock()
    {
        using var context = await CreateCompleteContextWithAssetsAsync();
        var files = new NuspecReadObservingFileSystem(context.Files);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        await using var admission = await new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admission.Owner);
        files.OnNuspecRead = () => AssertEveryLockBusy(files, context);

        var graph = context.Graphs["first"];
        var rootNode = graph.Nodes.Single(static node => node.PackageId == "Root.First");
        var dependencyNode = graph.Nodes.Single(static node => node.PackageId == "Shared.Dependency");
        var packages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            [rootNode.PackageId] = Package(rootNode.PackageId, rootNode.Version, rootNode.InstallPath!),
            [dependencyNode.PackageId] = Package(dependencyNode.PackageId, dependencyNode.Version, dependencyNode.InstallPath!)
        };
        var scopedResolver = new ExistingInstallResolver(packages);
        var ownedResolver = CoordinatedReconciliationAdapters.BindPackageResolver(scopedResolver, owner);
        Assert.IsAssignableFrom<IPackageGraphFileReader>(ownedResolver);

        var graphResolver = new PackageDependencyGraphResolver(ownedResolver,
            Substitute.For<IReconciliationRetryPolicy>(), hostProvidedPackagesOptions: null,
            hostPackageVersions: new Dictionary<string, string>());
        var request = Assert.Single(context.Requests["first"]);
        var resolution = await graphResolver.ResolveAsync([request],
            (packageRequest, token) => ownedResolver.ResolveAsync(packageRequest, token),
            (packageRequest, token) => ownedResolver.ResolveAsync(packageRequest, token),
            CancellationToken.None);

        var resolvedGraph = Assert.Single(resolution.ResolvedGraphs);
        Assert.Equal("Root.First", Assert.Single(resolvedGraph.Roots).PackageId);
        var edge = Assert.Single(resolvedGraph.Edges);
        Assert.Equal("Root.First", edge.FromPackageId);
        Assert.Equal("Shared.Dependency", edge.ToPackageId);
        var projectedRoot = resolvedGraph.Nodes.Single(static node => node.PackageId == "Root.First");
        var projectedDependency = resolvedGraph.Nodes.Single(static node => node.PackageId == "Shared.Dependency");
        Assert.Equal(Path.Combine("lib", "net10.0", "Root.First.dll"), Assert.Single(projectedRoot.DiscoverableAssets));
        Assert.Equal(Path.Combine("lib", "net10.0", "Shared.Dependency.dll"), Assert.Single(projectedDependency.SupportAssets));
        Assert.Equal(2, scopedResolver.ResolveCount);
        Assert.Equal(2, files.NuspecReadCount);
        Assert.Equal(2, files.LockCheckCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task OwnedGraphReader_WrongRootAndExpiredOwnerRefuseBeforeNuspecRead()
    {
        using var context = await CreateCompleteContextWithAssetsAsync();
        var files = new NuspecReadObservingFileSystem(context.Files);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = await new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admission.Owner);
        var packageNode = context.Graphs["first"].Nodes.Single(static node => node.PackageId == "Root.First");
        var ownedResolver = CoordinatedReconciliationAdapters.BindPackageResolver(
            new ExistingInstallResolver(new Dictionary<string, ResolvedPackage>
            {
                [packageNode.PackageId] = Package(packageNode.PackageId, packageNode.Version, packageNode.InstallPath!)
            }), owner);
        var graphReader = Assert.IsAssignableFrom<IPackageGraphFileReader>(ownedResolver);

        using var foreignFixture = new PackageStoreFixture();
        var foreignInstall = foreignFixture.CreateDirectory("packages/feed/Foreign/1.0.0");
        File.WriteAllText(Path.Combine(foreignInstall, "Foreign.nuspec"),
            "<package><metadata><id>Foreign</id><version>1.0.0</version></metadata></package>");
        var wrongRoot = Assert.Throws<PackageStoreAdmissionException>(() =>
            graphReader.ReadInstallFiles(Package("Foreign", "1.0.0", foreignInstall)));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrongRoot.Reason);
        Assert.Equal(0, files.NuspecReadCount);

        await admission.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => graphReader.ReadInstallFiles(
            Package(packageNode.PackageId, packageNode.Version, packageNode.InstallPath!)));
        Assert.Equal(0, files.NuspecReadCount);
        await admission.DisposeAsync();
    }

    private static async Task<RootMembershipProtectionVerificationTests.Context> CreateCompleteContextWithAssetsAsync()
    {
        var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync(context =>
        {
            var rootInstallPath = context.Fixture.CreateDirectory("packages/feed/Root.First/1.0.0");
            WriteRuntimeAsset(rootInstallPath, "Root.First.dll");
            WriteRuntimeAsset(context.SharedInstallPath, "Shared.Dependency.dll");
        });
        try
        {
            await context.PublishStatesAsync();
            await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1,
                true, CancellationToken.None);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static void WriteRuntimeAsset(string installPath, string fileName)
    {
        var runtimeDirectory = Directory.CreateDirectory(Path.Combine(installPath, "lib", "net10.0")).FullName;
        File.WriteAllText(Path.Combine(runtimeDirectory, fileName), "fixture asset");
    }

    private static void AssertEveryLockBusy(
        IPhysicalStoreFileSystem files,
        RootMembershipProtectionVerificationTests.Context context)
    {
        var ledger = context.Registry.ReadCandidate(context.Root);
        using var control = files.OpenDirectoryChildNoFollow(context.Root, RootMembershipRegistry.ControlDirectoryName);
        var lockNames = new[] { "root.lock" }.Concat(ledger.Members.Select(member =>
            PhysicalStoreLock.GetMemberLockName(Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding).StateSlot)));
        foreach (var lockName in lockNames)
        {
            using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var acquired = files.TryAcquireExclusiveLock(lockFile).AsTask().GetAwaiter().GetResult();
            if (acquired is not null)
                acquired.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Null(acquired);
        }
    }

    private static ResolvedPackage Package(string id, string version, string path)
        => new(id, version, "test-feed", path, DateTimeOffset.UnixEpoch, "complete-root")
        {
            PackageContentHash = "sha512:" + Convert.ToBase64String(new byte[64])
        };

    private sealed class ExistingInstallResolver(IReadOnlyDictionary<string, ResolvedPackage> packages) : IScopedPackageResolver
    {
        private int _resolveCount;

        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException(
                "An enrolled graph resolution must use the scoped resolver overload."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packages.TryGetValue(request.Id, out var package))
                throw new InvalidOperationException($"No completed fixture install exists for '{request.Id}'.");
            borrow.ValidateForInstallPath(package.InstallPath!);
            Interlocked.Increment(ref _resolveCount);
            return Task.FromResult(package);
        }
    }

    private sealed class NuspecReadObservingFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryEnumerationFileSystem,
            IPhysicalStorePublicationFileSystem
    {
        private readonly HashSet<PhysicalFileIdentity> _nuspecIdentities = [];

        internal Action? OnNuspecRead { get; set; }
        internal int NuspecReadCount { get; private set; }
        internal int LockCheckCount { get; private set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string name)
            => inner.InspectChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string name)
            => inner.OpenDirectoryChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string name, FileAccess access)
        {
            var file = inner.OpenFileChildNoFollow(parent, name, access);
            if (name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                _nuspecIdentities.Add(inner.InspectHandle(file).Identity);
            return file;
        }

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string name,
            PhysicalFileIdentity expectedIdentity) => inner.ReadLinkTargetNoFollow(parent, name, expectedIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string name)
            => inner.CreateDirectoryExclusiveAt(parent, name);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string name)
            => inner.CreateFileExclusiveAt(parent, name);

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (_nuspecIdentities.Contains(inner.InspectHandle(file).Identity))
            {
                OnNuspecRead?.Invoke();
                LockCheckCount++;
                NuspecReadCount++;
            }
            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent,
            string name, PhysicalFileIdentity identity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, name, identity);
        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => ((IPhysicalStoreDirectoryEnumerationFileSystem)inner).EnumerateChildNamesNoFollow(parent, maximumEntries);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity stagedIdentity, string destinationName, PhysicalFileIdentity? destinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(parent, stagedName, stagedIdentity,
                destinationName, destinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, name, identity);
    }
}
