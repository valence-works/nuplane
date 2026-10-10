using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;
using Nuplane.Operational;

namespace Nuplane.Integration.Tests;

[Trait("Platform", "Native")]
public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task RetainedGraphLoadSurvivesAnotherCompositionPublishingANewerGeneration()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var providerA = fixture.CreateProvider("member-a");
        await using var providerB = fixture.CreateProvider("member-b");

        PackageGraphUseLeaseOwner? ownerA = null;
        PackageGraphUseLeaseOwner? ownerB = null;
        Task<PackageLoadResult>? loadA = null;
        MetadataReadBarrier? firstMetadataRead = null;
        try
        {
            var acquiredOwnerA = await AcquireGraphUseAsync(providerA, fixture.Graphs["member-a"]);
            ownerA = acquiredOwnerA;
            var metadataRead = fixture.Files.BlockNextMetadataRead();
            firstMetadataRead = metadataRead;
            loadA = Task.Run(() => providerA.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [fixture.Graphs["member-a"].CreateEnvelope(acquiredOwnerA)], [], CancellationToken.None));
            await metadataRead.WaitUntilBlockedAsync();

            var admissionB = providerB.GetRequiredService<IPackageStoreAdmission>();
            await using (var operationB = await admissionB.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading))
            {
                var borrowB = operationB.Owner!.Borrow();
                try
                {
                    var locked = PackageStoreOperationAccess.GetLockedMemberLocations(borrowB);
                    var priorLedgerDigest = locked.Ledger.LedgerDigest;
                    var priorState = await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(
                            providerB.GetRequiredService<IStoreRegistry>())
                        .ReadCoordinatedStateAsync(borrowB, CancellationToken.None);
                    var priorGeneration = Assert.Single(priorState.ActiveGraphsByIdNormalized.Values).GenerationId;
                    Assert.Equal(fixture.Graphs["member-b"].Graph.GenerationId, priorGeneration);

                    var nextGraph = fixture.Graphs["member-b-v2"];
                    var nextState = fixture.BuildProtectedState(
                        "member-b", nextGraph, priorState,
                        revision: checked(priorState.ProtectionRecord!.Revision + 1),
                        isUpdate: true);
                    await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(
                            providerB.GetRequiredService<IStoreRegistry>())
                        .PersistCoordinatedActiveStateAsync(borrowB, nextState, CancellationToken.None);

                    var committedState = await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(
                            providerB.GetRequiredService<IStoreRegistry>())
                        .ReadCoordinatedStateAsync(borrowB, CancellationToken.None);
                    Assert.Equal("2.0.0", committedState.ActiveVersionById["Root.Second"]);
                    var committedGeneration = Assert.Single(committedState.ActiveGraphsByIdNormalized.Values).GenerationId;
                    Assert.NotEqual(priorGeneration, committedGeneration);
                    Assert.Equal(nextGraph.Graph.GenerationId, committedGeneration);
                    Assert.NotEqual(priorLedgerDigest, locked.Ledger.LedgerDigest);

                    ownerB = await providerB.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>()
                        .AcquireForRootAsync(borrowB, nextGraph.Graph, nextGraph.Requests,
                            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
                }
                finally
                {
                    borrowB.Dispose();
                }
            }

            var loadB = await providerB.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [fixture.Graphs["member-b-v2"].CreateEnvelope(ownerB!)], [], CancellationToken.None);
            Assert.Empty(loadB.FailedByPackageId);
            Assert.Contains(loadB.Loaded, session => session.PackageId == "Root.Second" && session.Version == "2.0.0");

            firstMetadataRead!.Release();
            var loadedA = await loadA;
            Assert.Empty(loadedA.FailedByPackageId);
            Assert.Contains(loadedA.Loaded, session => session.PackageId == "Root.First" && session.Version == "1.0.0");
            AssertGraphLoaded(providerA, fixture.Graphs["member-a"], "Root.First", "Shared.Dependency");
            AssertGraphLoaded(providerB, fixture.Graphs["member-b-v2"], "Root.Second", "Shared.Dependency");

            Assert.Equal(fixture.Graphs["member-b-v2"].Graph.GenerationId, ownerB!.Lease.Snapshot.GenerationId);
            var liveRecords = await InspectGraphUseRecordsAsync(
                providerA.GetRequiredService<IPackageStoreAdmission>(), fixture);
            Assert.Equal(2, liveRecords.Entries.Count);
            Assert.All(liveRecords.Entries,
                entry => Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState));
        }
        finally
        {
            firstMetadataRead?.Release();
            if (loadA is not null)
            {
                try { await loadA; }
                catch { }
            }

            if (ownerA is not null)
                await ownerA.DisposeAsync();
            if (ownerB is not null)
                await ownerB.DisposeAsync();

            providerA.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            providerB.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await fixture.WaitForGraphUseReleaseAsync(providerA.GetRequiredService<IPackageStoreAdmission>());
        }
    }

    private static async Task<PackageGraphUseLeaseOwner> AcquireGraphUseAsync(
        IServiceProvider services,
        ResolvedGraphFixture graph)
    {
        var admission = services.GetRequiredService<IPackageStoreAdmission>();
        var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var borrow = operation.Owner!.Borrow();
        try
        {
            return await services.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>()
                .AcquireForRootAsync(borrow, graph.Graph, graph.Requests,
                    PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        }
        finally
        {
            borrow.Dispose();
            await operation.DisposeAsync();
        }
    }

    private static async Task<GraphUseRecordInspectionResult> InspectGraphUseRecordsAsync(
        IPackageStoreAdmission admission,
        GraphUseFixture fixture)
    {
        var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        await using (operation)
        {
            var borrow = operation.Owner!.Borrow();
            try
            {
                return await PackageStoreOperationAccess.WithValidatedRootAsync(borrow,
                    async (files, root, token) =>
                    {
                        var members = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                        // WithValidatedRootAsync verifies all members before and after this callback.
                        // Reentering member-state reads here would wait on its own serialized gate.
                        return await new PackageGraphUseRecordStore(files)
                            .InspectAsync(root, borrow.Root, members.Ledger, token);
                    }, CancellationToken.None);
            }
            finally
            {
                borrow.Dispose();
            }
        }
    }

    private static void AssertGraphLoaded(IServiceProvider services, ResolvedGraphFixture graph, params string[] expectedAssemblyNames)
    {
        var graphKey = PackageLoader.BuildGraphKey(graph.Packages);
        var context = Assert.Single(AssemblyLoadContext.All, context => context.IsCollectible &&
            string.Equals(context.Name, graphKey, StringComparison.Ordinal));
        Assert.Equal(expectedAssemblyNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            context.Assemblies.Select(static assembly => assembly.GetName().Name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class GraphUseFixture : IDisposable
    {
        private readonly PackageStoreFixture _fixture = new();
        private readonly Dictionary<string, PhysicalStoreDirectoryHandle> _stateParents = new(StringComparer.Ordinal);
        private bool _disposed;

        private GraphUseFixture()
        {
            IPhysicalStoreFileSystem inner = OperatingSystem.IsWindows()
                ? new WindowsPhysicalStoreFileSystem()
                : new UnixPhysicalStoreFileSystem();
            Files = new MetadataReadBlockingFileSystem(inner);
        }

        internal MetadataReadBlockingFileSystem Files { get; }
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal RootMembershipRegistry Registry { get; private set; } = null!;
        internal string RootPath => _fixture.PackageInstallRoot;
        internal Dictionary<string, ResolvedGraphFixture> Graphs { get; } = new(StringComparer.Ordinal);

        internal static async Task<GraphUseFixture> CreateAsync()
        {
            var fixture = new GraphUseFixture();
            try
            {
                await fixture.InitializeAsync();
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal ServiceProvider CreateProvider(string memberId, IPackageActivationGate? activationGate = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.WithStateFile(StatePaths[memberId]);
                nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.Collectible));
            });
            services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = RootPath);
            if (activationGate is not null)
                services.AddSingleton(activationGate);
            services.RemoveAll<IPhysicalStoreFileSystem>();
            services.AddSingleton<IPhysicalStoreFileSystem>(Files);
            return services.BuildServiceProvider();
        }

        internal IReadOnlyDictionary<string, string> StatePaths { get; private set; } = null!;

        internal StoreStateRecord BuildProtectedState(
            string memberId,
            ResolvedGraphFixture graph,
            StoreStateRecord currentState,
            long revision,
            bool isUpdate)
        {
            var installs = new List<PackageInstallIdentity>();
            foreach (var node in graph.Graph.Nodes)
            {
                using var observation = new PackageInstallIdentityReader(Files).Observe(Root, RootIdentity,
                    Path.GetRelativePath(RootPath, node.InstallPath!).Replace(Path.DirectorySeparatorChar, '/'),
                    node.PackageId, node.Version);
                installs.Add(observation.InstallIdentity);
            }

            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                graph.Graph, graph.Requests, installs, revision);
            var versions = graph.Packages.ToDictionary(static package => package.Id,
                static package => package.Version, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var change = new PackageChangeSet(
                isUpdate ? [] : graph.Packages,
                isUpdate ? graph.Packages.Where(package => currentState.ActiveVersionById.TryGetValue(package.Id, out var oldVersion) &&
                    !string.Equals(oldVersion, package.Version, StringComparison.OrdinalIgnoreCase)).ToArray() : [],
                [], memberId, now);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(currentState, versions,
                graph.Packages, change, memberId, now, [graph.Graph]);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(currentState, [graph.Graph],
                versions, memberId, now);
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            var state = currentState with
            {
                ActiveVersionById = versions,
                LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors,
                    StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords,
                    StringComparer.OrdinalIgnoreCase),
                UpdatedAt = now,
                ProtectionRecord = null
            };
            var candidate = new PackageProtectionRecord(1, RootIdentity,
                RootMembershipBindingEpoch, memberId, revision, ProtectionDigest.StateBody(state),
                new string('0', 64), closure, closure, [], false);
            return state with
            {
                ProtectionRecord = new PackageProtectionRecord(1, RootIdentity,
                    RootMembershipBindingEpoch, memberId, revision, candidate.StateBodyDigest,
                    ProtectionDigest.Protection(candidate), closure, closure, [], false)
            };
        }

        internal async Task WaitForGraphUseReleaseAsync(IPackageStoreAdmission admission)
        {
            IOException? lastTransientFailure = null;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                try
                {
                    var inspection = await InspectGraphUseRecordsAsync(admission, this);
                    // Releasing native ownership leaves the published record for stale-record reaping.
                    // The lock probe, rather than disappearance of evidence, proves lifetime release.
                    if (inspection.Entries.All(static entry =>
                            entry.OwnershipState == GraphUseRecordOwnershipState.Stale))
                        return;
                }
                catch (IOException exception)
                {
                    lastTransientFailure = exception;
                }
                await Task.Delay(100);
            }

            var detail = lastTransientFailure is null ? string.Empty : $" Last transient filesystem error: {lastTransientFailure.Message}";
            throw new Xunit.Sdk.XunitException(
                $"Collectible graph-use records did not become stale after their load contexts were unloaded.{detail}");
        }

        private async Task InitializeAsync()
        {
            Root = OwnedProcessDirectory.Open(Files, RootPath);
            RootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
            Registry = new RootMembershipRegistry(Files, new StoreStateSerializer());
            var paths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["member-a"] = _fixture.StateFilePath,
                ["member-b"] = _fixture.CreateStateSlot("state-b/store-state.json")
            };
            StatePaths = paths;

            foreach (var pair in paths)
            {
                await new StoreStateSerializer().SaveAsync(pair.Value,
                    StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch }, CancellationToken.None);
                _stateParents.Add(pair.Key, OwnedProcessDirectory.Open(Files, Path.GetDirectoryName(pair.Value)!));
            }

            var declarations = paths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
                new RootMemberRecord.DeclaredBinding())).ToArray();
            Registry.InitializeIncomplete(Root, RootIdentity, RootMembershipBindingEpoch, declarations,
                quiescentCutoverConfirmed: true, CancellationToken.None);
            await Registry.BindDeclaredMembersAsync(Root, RootIdentity, RootMembershipBindingEpoch, declarations,
                _stateParents.ToDictionary(static pair => pair.Key, pair =>
                    (pair.Value, Path.GetFileName(paths[pair.Key])), StringComparer.Ordinal),
                quiescentCutoverConfirmed: true, CancellationToken.None);

            var shared = CreateInstall("Shared.Dependency", "2.1.0", dependencyId: null, sourceName: "feed");
            var firstRoot = CreateInstall("Root.First", "1.0.0", "Shared.Dependency", "member-a");
            var secondV1Root = CreateInstall("Root.Second", "1.0.0", "Shared.Dependency", "member-b");
            var secondV2Root = CreateInstall("Root.Second", "2.0.0", "Shared.Dependency", "member-b");
            Graphs.Add("member-a", await ResolveGraphAsync("member-a", firstRoot, shared));
            Graphs.Add("member-b", await ResolveGraphAsync("member-b", secondV1Root, shared));
            Graphs.Add("member-b-v2", await ResolveGraphAsync("member-b", secondV2Root, shared));

            var stateA = BuildProtectedState("member-a", Graphs["member-a"], StoreStateRecord.Empty(), 1, false);
            var stateB = BuildProtectedState("member-b", Graphs["member-b"], StoreStateRecord.Empty(), 1, false);
            await Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(Root, RootIdentity,
                RootMembershipBindingEpoch, quiescentCutoverConfirmed: true,
                async (locked, token) =>
                {
                    foreach (var memberId in paths.Keys)
                        await locked.PublishStateAsync(memberId, memberId == "member-a" ? stateA : stateB, token);
                    return true;
                }, CancellationToken.None);
            await Registry.CompleteEnrollmentAsync(Root, RootIdentity, RootMembershipBindingEpoch,
                quiescentCutoverConfirmed: true, CancellationToken.None);
        }

        private ResolvedPackage CreateInstall(string packageId, string version, string? dependencyId, string sourceName)
        {
            var path = _fixture.CreateDirectory($"packages/feed/{packageId}/{version}");
            var dependency = dependencyId is null
                ? string.Empty
                : $"<dependencies><dependency id=\"{dependencyId}\" version=\"[2.1.0]\" /></dependencies>";
            File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
                $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependency}</metadata></package>");
            EmitPackageAssembly(packageId, Path.Combine(path, packageId + ".dll"));
            File.WriteAllText(Path.Combine(path, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName),
                "{\"schemaVersion\":2,\"loading\":{\"loadMode\":\"Collectible\",\"scope\":\"DependencyClosure\"}}");
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
            return new ResolvedPackage(packageId, version, "feed", path, DateTimeOffset.UnixEpoch, sourceName);
        }

        private static void EmitPackageAssembly(string assemblyName, string assemblyPath)
        {
            var assembly = new PersistedAssemblyBuilder(
                new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
            var module = assembly.DefineDynamicModule(assemblyName);
            module.DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            assembly.Save(assemblyPath);
        }

        private async Task<ResolvedGraphFixture> ResolveGraphAsync(
            string memberId,
            ResolvedPackage root,
            ResolvedPackage dependency)
        {
            var request = new PackageRequest(root.Id, $"[{root.Version}]", "feed",
                PackageUpdatePolicy.Exact, memberId);
            var resolver = new PackageDependencyGraphResolver(
                Substitute.For<IPackageResolver>(), Substitute.For<IReconciliationRetryPolicy>());
            var resolution = await resolver.ResolveAsync([request],
                (_, _) => Task.FromResult(root),
                (_, _) => Task.FromResult(dependency), CancellationToken.None);
            var graph = Assert.Single(resolution.ResolvedGraphs);
            return new ResolvedGraphFixture(graph, resolution.ResolvedPackages, [request]);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var parent in _stateParents.Values.Reverse())
                parent.Dispose();
            _stateParents.Clear();
            Root?.Dispose();
            _fixture.Dispose();
        }

        private const long RootMembershipBindingEpoch = 37;
    }

    private sealed record ResolvedGraphFixture(
        ResolvedPackageGraph Graph,
        IReadOnlyList<ResolvedPackage> Packages,
        IReadOnlyList<PackageRequest> Requests)
    {
        internal ScopedResolvedPackageGraph CreateEnvelope(
            PackageGraphUseLeaseOwner owner,
            IReadOnlyList<ResolvedPackage>? packages = null)
            => new(Graph, packages ?? Packages, Requests, owner);
    }

    private sealed class MetadataReadBlockingFileSystem(IPhysicalStoreFileSystem inner) :
        IPhysicalStoreFileSystem,
        IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem,
        IPhysicalStoreDirectoryPublicationFileSystem,
        IPhysicalStoreDirectoryEnumerationFileSystem
    {
        private MetadataReadBarrier? _barrier;

        internal MetadataReadBarrier BlockNextMetadataRead()
        {
            var barrier = new MetadataReadBarrier();
            if (Interlocked.CompareExchange(ref _barrier, barrier, null) is not null)
                throw new InvalidOperationException("A metadata read is already armed.");
            return barrier;
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            FileAccess access)
        {
            var file = inner.OpenFileChildNoFollow(parent, singleName, access);
            if (string.Equals(singleName, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName,
                    StringComparison.Ordinal))
                Volatile.Read(ref _barrier)?.Observe(file);
            return file;
        }

        public string ReadLinkTargetNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedLinkIdentity)
            => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateFileExclusiveAt(parent, singleName);

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            Volatile.Read(ref _barrier)?.PauseIfObserved(file);
            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName,
            PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, singleName, expectedIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)inner).ObserveCanonicalDirectoryNameNoFollow(
                parent, singleName, expectedDirectoryIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)inner).PublishDirectoryNoReplaceAt(
                parent, stagedName, expectedStagedIdentity, destinationName);
        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => ((IPhysicalStoreDirectoryEnumerationFileSystem)inner).EnumerateChildNamesNoFollow(parent, maximumEntries);
    }

    private sealed class MetadataReadBarrier
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PhysicalStoreFileHandle? _file;
        private int _blocked;

        internal void Observe(PhysicalStoreFileHandle file)
            => Interlocked.CompareExchange(ref _file, file, null);

        internal async Task WaitUntilBlockedAsync()
            => await _started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        internal void Release() => _release.TrySetResult(true);

        internal bool HasStarted => _started.Task.IsCompleted;

        internal void PauseIfObserved(PhysicalStoreFileHandle file)
        {
            if (ReferenceEquals(Volatile.Read(ref _file), file) && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _started.TrySetResult(true);
                _release.Task.GetAwaiter().GetResult();
            }
        }
    }

}
