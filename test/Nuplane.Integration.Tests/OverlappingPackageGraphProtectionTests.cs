using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
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
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
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
            return await AcquireGraphUseAsync(services, graph, borrow);
        }
        finally
        {
            borrow.Dispose();
            await operation.DisposeAsync();
        }
    }

    private static ValueTask<PackageGraphUseLeaseOwner> AcquireGraphUseAsync(
        IServiceProvider services,
        ResolvedGraphFixture graph,
        PackageStoreOperationBorrow borrow)
        => services.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>()
            .AcquireForRootAsync(borrow, graph.Graph, graph.Requests,
                PackageGraphUseSnapshotState.Committed, CancellationToken.None);

    private static async Task<GraphUseRecordInspectionResult> InspectGraphUseRecordsAsync(
        IPackageStoreAdmission admission,
        GraphUseFixture fixture)
    {
        var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
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

    internal sealed class GraphUseFixture : IDisposable
    {
        private readonly PackageStoreFixture _fixture = new();
        private readonly Dictionary<string, PhysicalStoreDirectoryHandle> _stateParents = new(StringComparer.Ordinal);
        private readonly PackageLoadMode _packageLoadMode;
        private readonly long _membershipEpoch;
        private bool _disposed;

        private GraphUseFixture(PackageLoadMode packageLoadMode, long membershipEpoch)
        {
            _packageLoadMode = packageLoadMode;
            _membershipEpoch = membershipEpoch;
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
        internal ResolvedPackage UnrelatedInstall { get; private set; } = null!;
        internal ResolvedGraphFixture? IndependentGraph { get; private set; }
        internal ResolvedGraphFixture? FreshMemberAGraph { get; private set; }
        internal Dictionary<string, ResolvedGraphFixture> Graphs { get; } = new(StringComparer.Ordinal);
        internal string CreateStateSlot(string relativePath) => _fixture.CreateStateSlot(relativePath);

        internal ResolvedPackage CreateAdditionalEligibleInstall(string packageId, string version)
            => CreateInstall(packageId, version, dependencyId: null, sourceName: "execution-test");

        internal static async Task<GraphUseFixture> CreateAsync(
            PackageLoadMode packageLoadMode = PackageLoadMode.Collectible,
            bool includeIndependentGraph = false,
            bool includeFreshMemberAGraph = false,
            Action<string>? beforeSharedDependencyCompletionMarker = null,
            bool emitNativeProbeOnRootSecondV1 = false,
            long rootMembershipEpoch = RootMembershipBindingEpoch)
        {
            var fixture = new GraphUseFixture(packageLoadMode, rootMembershipEpoch);
            try
            {
                await fixture.InitializeAsync(includeIndependentGraph, includeFreshMemberAGraph,
                    beforeSharedDependencyCompletionMarker, emitNativeProbeOnRootSecondV1);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal ServiceProvider CreateProvider(string memberId, IPackageActivationGate? activationGate = null,
            IReadOnlyList<PackageRequest>? desiredRequests = null,
            FeedDefinition? remoteFeed = null,
            IReadOnlyList<FeedDefinition>? additionalFeeds = null,
            bool participant = false,
            INuplaneObserver? observer = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.WithStateFile(StatePaths[memberId]);
                if (participant)
                    nuplane.UseCoordinatedPackageStore(CompleteRoster, true);
                nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(_packageLoadMode));
            });
            if (observer is not null)
                services.AddSingleton<INuplaneObserver>(observer);
            services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = RootPath);
            if (desiredRequests is not null)
            {
                services.AddSingleton<IDesiredPackageSource>(new StaticDesiredSource(desiredRequests));
                services.Configure<FeedResolutionOptions>(options =>
                {
                    options.Feeds.Add(remoteFeed ?? new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json")));
                    if (additionalFeeds is not null)
                        options.Feeds.AddRange(additionalFeeds);
                });
                if (remoteFeed?.ServiceIndex.Scheme == Uri.UriSchemeHttp)
                {
                    services.Remove(services.Single(descriptor =>
                        descriptor.ServiceType == typeof(IValidateOptions<FeedResolutionOptions>) &&
                        descriptor.ImplementationType == typeof(FeedCredentialCompositeValidator)));
                }
                // Keep these reconciliation requests exact so feed version enumeration is not
                // confused with package acquisition. A supplied lock entry authenticates the
                // selected archive hash; legacy cached dependencies remain hashless.
                services.Configure<LockFileOptions>(options =>
                {
                    options.Mode = LockFileMode.Enforce;
                    options.Path = Path.Combine(Path.GetDirectoryName(StatePaths[memberId])!, "nuplane.lock.json");
                });
            }
            if (activationGate is not null)
                services.AddSingleton(activationGate);
            if (!participant)
            {
                services.RemoveAll<IPhysicalStoreFileSystem>();
                services.AddSingleton<IPhysicalStoreFileSystem>(Files);
                // This instrumented component fixture deliberately supplies native admission.
                // Production participant composition rejects wrapped filesystem providers.
                services.AddSingleton(ProtectedReconciliationServices.Create);
            }
            configureServices?.Invoke(services);
            return services.BuildServiceProvider();
        }

        internal IReadOnlyDictionary<string, string> StatePaths { get; private set; } = null!;

        internal Nuplane.Builder.PackageStoreMemberDeclaration[] CompleteRoster => StatePaths
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
                new Nuplane.Builder.PackageStoreMemberDeclaration("default", pair.Key, pair.Value)).ToArray();

        internal StoreStateRecord BuildProtectedState(
            string memberId,
            ResolvedGraphFixture graph,
            StoreStateRecord currentState,
            long revision,
            bool isUpdate) => BuildProtectedState(memberId, [graph], currentState, revision, isUpdate);

        internal StoreStateRecord BuildProtectedState(
            string memberId,
            IReadOnlyList<ResolvedGraphFixture> graphs,
            StoreStateRecord currentState,
            long revision,
            bool isUpdate)
        {
            ArgumentNullException.ThrowIfNull(graphs);
            if (graphs.Count == 0)
                throw new ArgumentException("At least one graph is required to build protected state.", nameof(graphs));

            var installsByPath = new Dictionary<string, PackageInstallIdentity>(StringComparer.Ordinal);
            foreach (var node in graphs.SelectMany(static graph => graph.Graph.Nodes))
            {
                var relativePath = Path.GetRelativePath(RootPath, node.InstallPath!).Replace(Path.DirectorySeparatorChar, '/');
                using var observation = new PackageInstallIdentityReader(Files).Observe(Root, RootIdentity,
                    relativePath,
                    node.PackageId, node.Version);
                if (installsByPath.TryGetValue(relativePath, out var priorIdentity))
                    Assert.Equal(priorIdentity, observation.InstallIdentity);
                else
                    installsByPath.Add(relativePath, observation.InstallIdentity);
            }

            var snapshots = graphs.Select(graph => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                graph.Graph, graph.Requests, graph.Graph.Nodes.Select(node =>
                    installsByPath[Path.GetRelativePath(RootPath, node.InstallPath!).Replace(Path.DirectorySeparatorChar, '/')]).ToArray(), revision))
                .ToArray();
            var packages = graphs.SelectMany(static graph => graph.Packages).ToArray();
            var versions = packages.ToDictionary(static package => package.Id,
                static package => package.Version, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var added = isUpdate
                ? packages.Where(package => !currentState.ActiveVersionById.ContainsKey(package.Id)).ToArray()
                : packages;
            var updated = isUpdate
                ? packages.Where(package => currentState.ActiveVersionById.TryGetValue(package.Id, out var oldVersion) &&
                    !string.Equals(oldVersion, package.Version, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            var change = new PackageChangeSet(added, updated, [], memberId, now);
            var activeGraphs = graphs.Select(static graph => graph.Graph).ToArray();
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(currentState, versions,
                packages, change, memberId, now, activeGraphs);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(currentState, activeGraphs,
                versions, memberId, now);
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, snapshots);
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
                _membershipEpoch, memberId, revision, ProtectionDigest.StateBody(state),
                new string('0', 64), closure, closure, [], false);
            return state with
            {
                ProtectionRecord = new PackageProtectionRecord(1, RootIdentity,
                    _membershipEpoch, memberId, revision, candidate.StateBodyDigest,
                    ProtectionDigest.Protection(candidate), closure, closure, [], false)
            };
        }

        internal Task<RootMembershipRecord> PublishProtectedStateAsync(
            string memberId,
            StoreStateRecord state,
            CancellationToken cancellationToken = default)
            => Registry.PublishStateAsync(Root, _stateParents, memberId, state, cancellationToken);

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

        private async Task InitializeAsync(
            bool includeIndependentGraph,
            bool includeFreshMemberAGraph,
            Action<string>? beforeSharedDependencyCompletionMarker,
            bool emitNativeProbeOnRootSecondV1)
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

            var declarations = paths.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RootMemberRecord(pair.Key, pair.Value,
                    new RootMemberRecord.DeclaredBinding())).ToArray();
            // Resolve migration inputs before enrollment; incomplete roots intentionally refuse package reads.
            var shared = CreateInstall("Shared.Dependency", "2.1.0", dependencyId: null, sourceName: "feed",
                beforeCompletionMarker: beforeSharedDependencyCompletionMarker);
            var firstRoot = CreateInstall("Root.First", "1.0.0", "Shared.Dependency", "member-a");
            var secondV1Root = CreateInstall("Root.Second", "1.0.0", "Shared.Dependency", "member-b",
                emitNativeProbe: emitNativeProbeOnRootSecondV1);
            var secondV2Root = CreateInstall("Root.Second", "2.0.0", "Shared.Dependency", "member-b");
            Graphs.Add("member-a", await ResolveGraphAsync("member-a", firstRoot, shared));
            Graphs.Add("member-b", await ResolveGraphAsync("member-b", secondV1Root, shared));
            Graphs.Add("member-b-v2", await ResolveGraphAsync("member-b", secondV2Root, shared));
            UnrelatedInstall = CreateInstall("Unrelated.Package", "0.1.0", dependencyId: null, sourceName: "unrelated");
            if (includeFreshMemberAGraph)
                FreshMemberAGraph = await ResolveGraphAsync("member-a", firstRoot, shared);
            if (includeIndependentGraph)
            {
                var independentRoot = CreateInstall("Root.Independent", "1.0.0", dependencyId: null, sourceName: "member-a");
                IndependentGraph = await ResolveGraphAsync("member-a", independentRoot, UnrelatedInstall);
            }

            Registry.InitializeIncomplete(Root, RootIdentity, _membershipEpoch, declarations,
                quiescentCutoverConfirmed: true, CancellationToken.None);
            await Registry.BindDeclaredMembersAsync(Root, RootIdentity, _membershipEpoch, declarations,
                _stateParents.ToDictionary(static pair => pair.Key, pair =>
                    (pair.Value, Path.GetFileName(paths[pair.Key])), StringComparer.Ordinal),
                quiescentCutoverConfirmed: true, CancellationToken.None);

            var stateA = BuildProtectedState("member-a", Graphs["member-a"], StoreStateRecord.Empty(), 1, false);
            var stateB = BuildProtectedState("member-b", Graphs["member-b"], StoreStateRecord.Empty(), 1, false);
            await Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(Root, RootIdentity,
                _membershipEpoch, quiescentCutoverConfirmed: true,
                async (locked, token) =>
                {
                    foreach (var memberId in paths.Keys.OrderBy(static memberId => memberId, StringComparer.Ordinal))
                        await locked.PublishStateAsync(memberId, memberId == "member-a" ? stateA : stateB, token);
                    return true;
                }, CancellationToken.None);
            await Registry.CompleteEnrollmentAsync(Root, RootIdentity, _membershipEpoch,
                quiescentCutoverConfirmed: true, CancellationToken.None);
        }

        private ResolvedPackage CreateInstall(
            string packageId,
            string version,
            string? dependencyId,
            string sourceName,
            Action<string>? beforeCompletionMarker = null,
            bool emitNativeProbe = false)
        {
            var path = _fixture.CreateDirectory($"packages/feed/{packageId}/{version}");
            using (var packageDirectory = OwnedProcessDirectory.Open(Files, path))
                Files.RegisterPackageDirectory(packageDirectory);
            var dependency = dependencyId is null
                ? string.Empty
                : $"<dependencies><dependency id=\"{dependencyId}\" version=\"[2.1.0]\" /></dependencies>";
            File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
                $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependency}</metadata></package>");
            EmitPackageAssembly(packageId, Path.Combine(path, packageId + ".dll"), emitNativeProbe);
            File.WriteAllText(Path.Combine(path, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName),
                $"{{\"schemaVersion\":2,\"loading\":{{\"loadMode\":\"{_packageLoadMode}\",\"scope\":\"DependencyClosure\"}}}}");
            beforeCompletionMarker?.Invoke(path);
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
            return new ResolvedPackage(packageId, version, "feed", path, DateTimeOffset.UnixEpoch, sourceName);
        }

        private static void EmitPackageAssembly(string assemblyName, string assemblyPath, bool emitNativeProbe = false)
        {
            var assembly = new PersistedAssemblyBuilder(
                new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
            var module = assembly.DefineDynamicModule(assemblyName);
            var marker = module.DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class);
            if (emitNativeProbe)
            {
                var method = marker.DefinePInvokeMethod(
                    NativeLibraryFixtureCompiler.MethodName,
                    NativeLibraryFixtureCompiler.LibraryName,
                    NativeLibraryFixtureCompiler.ExportName,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
                    CallingConventions.Standard,
                    typeof(int),
                    [typeof(int)],
                    CallingConvention.Cdecl,
                    CharSet.Ansi);
                method.SetImplementationFlags(MethodImplAttributes.PreserveSig);
            }

            marker.CreateType();
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

    internal sealed record ResolvedGraphFixture(
        ResolvedPackageGraph Graph,
        IReadOnlyList<ResolvedPackage> Packages,
        IReadOnlyList<PackageRequest> Requests)
    {
        internal ScopedResolvedPackageGraph CreateEnvelope(
            PackageGraphUseLeaseOwner owner,
            IReadOnlyList<ResolvedPackage>? packages = null)
            => new(Graph, packages ?? Packages, Requests, owner);
    }

    internal sealed class MetadataReadBlockingFileSystem(IPhysicalStoreFileSystem inner) :
        IPhysicalStoreFileSystem,
        IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem,
        IPhysicalStoreDirectoryPublicationFileSystem,
        IPhysicalStoreDirectoryEnumerationFileSystem,
        IPhysicalStorePackageStreamFileSystem,
        IPhysicalStoreControlRecoveryFileSystem
    {
        private MetadataReadBarrier? _barrier;
        private PackageFileOpenBarrier? _packageFileOpenBarrier;
        private readonly ConcurrentDictionary<PhysicalFileIdentity, byte> _packageDirectories = new();
        private readonly ConcurrentDictionary<PhysicalFileIdentity, byte> _rootLockFiles = new();
        private readonly ConcurrentDictionary<PhysicalFileIdentity, byte> _memberLockFiles = new();
        private int _activeRootLocks;
        private int _activeMemberLocks;
        private int _successfulRootLockAcquisitions;
        private int _successfulMemberLockAcquisitions;
        private int _packageFileAccessAttempts;

        internal int SuccessfulRootLockAcquisitions => Volatile.Read(ref _successfulRootLockAcquisitions);
        internal int SuccessfulMemberLockAcquisitions => Volatile.Read(ref _successfulMemberLockAcquisitions);
        internal int PackageFileAccessAttempts => Volatile.Read(ref _packageFileAccessAttempts);

        internal void ResetPackageFileAccessAttempts()
            => Volatile.Write(ref _packageFileAccessAttempts, 0);

        internal void RegisterPackageDirectory(PhysicalStoreDirectoryHandle directory)
            => _packageDirectories.TryAdd(inner.InspectHandle(directory).Identity, 0);

        internal PackageFileOpenBarrier BlockNextPackageFileOpen()
        {
            var barrier = new PackageFileOpenBarrier();
            if (Interlocked.CompareExchange(ref _packageFileOpenBarrier, barrier, null) is not null)
                throw new InvalidOperationException("A package-file open barrier is already armed.");
            return barrier;
        }

        internal MetadataReadBarrier BlockNextMetadataRead()
        {
            var barrier = new MetadataReadBarrier(onlyAfterRootUnlock: false);
            if (Interlocked.CompareExchange(ref _barrier, barrier, null) is not null)
                throw new InvalidOperationException("A metadata read is already armed.");
            return barrier;
        }

        internal MetadataReadBarrier BlockNextMetadataReadAfterRootUnlock()
        {
            var barrier = new MetadataReadBarrier(onlyAfterRootUnlock: true);
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
            PauseBeforePackageFileAccess(parent, singleName);
            var file = inner.OpenFileChildNoFollow(parent, singleName, access);
            RememberOperationLockFile(singleName, file);
            if (string.Equals(singleName, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName,
                    StringComparison.Ordinal))
                Volatile.Read(ref _barrier)?.Observe(file,
                    Volatile.Read(ref _activeRootLocks), Volatile.Read(ref _activeMemberLocks));
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
        {
            var file = inner.CreateFileExclusiveAt(parent, singleName);
            RememberOperationLockFile(singleName, file);
            return file;
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            Volatile.Read(ref _barrier)?.PauseIfObserved(file,
                Volatile.Read(ref _activeRootLocks), Volatile.Read(ref _activeMemberLocks));
            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);
        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var fileIdentity = inner.InspectHandle(file).Identity;
            var isRootLock = _rootLockFiles.ContainsKey(fileIdentity);
            var isMemberLock = _memberLockFiles.ContainsKey(fileIdentity);
            var acquired = await inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (acquired is null || (!isRootLock && !isMemberLock))
                return acquired;

            if (isRootLock)
            {
                Interlocked.Increment(ref _activeRootLocks);
                Interlocked.Increment(ref _successfulRootLockAcquisitions);
            }
            if (isMemberLock)
            {
                Interlocked.Increment(ref _activeMemberLocks);
                Interlocked.Increment(ref _successfulMemberLockAcquisitions);
            }
            return new OperationLockTrackingLease(acquired, this, isRootLock, isMemberLock);
        }
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
        {
            PauseBeforePackageFileAccess(parent, singleName);
            return ((IPhysicalStoreNameFileSystem)inner)
                .ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        }
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
        public ValueTask<PhysicalStoreLockedControlFile?> TryOpenAndLockControlFileForRemovalAt(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStoreControlRecoveryFileSystem)inner)
                .TryOpenAndLockControlFileForRemovalAt(parent, singleName, expectedIdentity);
        public ValueTask RemoveLockedControlFileAsync(PhysicalStoreLockedControlFile file)
            => ((IPhysicalStoreControlRecoveryFileSystem)inner).RemoveLockedControlFileAsync(file);
        public PhysicalStoreEntryInfo InspectLockedControlFile(PhysicalStoreLockedControlFile file)
            => ((IPhysicalStoreControlRecoveryFileSystem)inner).InspectLockedControlFile(file);
        public byte[] ReadLockedControlFile(PhysicalStoreLockedControlFile file, int maximumBytes)
            => ((IPhysicalStoreControlRecoveryFileSystem)inner).ReadLockedControlFile(file, maximumBytes);
        public PhysicalStoreEntryInfo MoveControlFileNoReplaceAt(
            PhysicalStoreDirectoryHandle parent, string sourceName,
            PhysicalFileIdentity expectedSourceIdentity, string destinationName)
            => ((IPhysicalStoreControlRecoveryFileSystem)inner).MoveControlFileNoReplaceAt(
                parent, sourceName, expectedSourceIdentity, destinationName);
        public Stream OpenPackageArchiveReadStream(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent,
            PhysicalStoreEntryInfo expectedFile,
            long maximumBytes)
        {
            PauseBeforePackageFileAccess(parent, singleName);
            return ((IPhysicalStorePackageStreamFileSystem)inner).OpenPackageArchiveReadStream(
                parent, singleName, file, expectedParent, expectedFile, maximumBytes);
        }
        public Stream CreatePackageFileWriteStream(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent,
            long maximumBytes)
        {
            PauseBeforePackageFileAccess(parent, singleName);
            return ((IPhysicalStorePackageStreamFileSystem)inner).CreatePackageFileWriteStream(
                parent, singleName, file, expectedParent, maximumBytes);
        }

        private void PauseBeforePackageFileAccess(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var parentIdentity = inner.InspectHandle(parent).Identity;
            if (_packageDirectories.ContainsKey(parentIdentity))
            {
                Interlocked.Increment(ref _packageFileAccessAttempts);
                Volatile.Read(ref _packageFileOpenBarrier)?.PauseBeforeOpen(
                    parentIdentity, singleName, Volatile.Read(ref _activeRootLocks), Volatile.Read(ref _activeMemberLocks));
            }
        }

        private void RememberOperationLockFile(string singleName, PhysicalStoreFileHandle file)
        {
            var fileIdentity = inner.InspectHandle(file).Identity;
            if (string.Equals(singleName, "root.lock", StringComparison.Ordinal))
                _rootLockFiles.TryAdd(fileIdentity, 0);
            else if (singleName.StartsWith("member-", StringComparison.Ordinal) &&
                     singleName.EndsWith(".lock", StringComparison.Ordinal))
                _memberLockFiles.TryAdd(fileIdentity, 0);
        }

        private sealed class OperationLockTrackingLease(
            IAsyncDisposable innerLock,
            MetadataReadBlockingFileSystem owner,
            bool tracksRootLock,
            bool tracksMemberLock)
            : IAsyncDisposable
        {
            private int _disposed;

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                await innerLock.DisposeAsync().ConfigureAwait(false);
                if (tracksRootLock)
                    Interlocked.Decrement(ref owner._activeRootLocks);
                if (tracksMemberLock)
                    Interlocked.Decrement(ref owner._activeMemberLocks);
            }
        }
    }

    internal sealed class MetadataReadBarrier
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _onlyAfterRootUnlock;
        private PhysicalStoreFileHandle? _file;
        private int _blocked;
        private int _rootLockCountAtPause = -1;
        private int _memberLockCountAtPause = -1;

        internal MetadataReadBarrier(bool onlyAfterRootUnlock) => _onlyAfterRootUnlock = onlyAfterRootUnlock;

        internal int RootLockCountAtPause => Volatile.Read(ref _rootLockCountAtPause);
        internal int MemberLockCountAtPause => Volatile.Read(ref _memberLockCountAtPause);

        internal void Observe(PhysicalStoreFileHandle file, int activeRootLocks, int activeMemberLocks)
        {
            if (_onlyAfterRootUnlock && (activeRootLocks != 0 || activeMemberLocks != 0))
                return;

            Interlocked.CompareExchange(ref _file, file, null);
        }

        internal async Task WaitUntilBlockedAsync()
            => await _started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        internal void Release() => _release.TrySetResult(true);

        internal bool HasStarted => _started.Task.IsCompleted;

        internal void PauseIfObserved(PhysicalStoreFileHandle file, int activeRootLocks, int activeMemberLocks)
        {
            if ((_onlyAfterRootUnlock && (activeRootLocks != 0 || activeMemberLocks != 0)) ||
                !ReferenceEquals(Volatile.Read(ref _file), file) || Interlocked.Exchange(ref _blocked, 1) != 0)
                return;

            Volatile.Write(ref _rootLockCountAtPause, activeRootLocks);
            Volatile.Write(ref _memberLockCountAtPause, activeMemberLocks);
            _started.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }
    }

    internal sealed class PackageFileOpenBarrier
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;
        private int _rootLockCountAtPause = -1;
        private int _memberLockCountAtPause = -1;
        private PhysicalFileIdentity? _parentIdentity;
        private string? _basename;

        internal int RootLockCountAtPause => Volatile.Read(ref _rootLockCountAtPause);
        internal int MemberLockCountAtPause => Volatile.Read(ref _memberLockCountAtPause);
        internal PhysicalFileIdentity ParentIdentity => Volatile.Read(ref _parentIdentity)
            ?? throw new InvalidOperationException("The package-file barrier has not observed an open.");
        internal string Basename => Volatile.Read(ref _basename)
            ?? throw new InvalidOperationException("The package-file barrier has not observed an open.");

        internal async Task WaitUntilBlockedAsync()
            => await _started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        internal void Release() => _release.TrySetResult(true);

        internal void PauseBeforeOpen(PhysicalFileIdentity parentIdentity, string basename,
            int activeRootLocks, int activeMemberLocks)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
                return;

            Volatile.Write(ref _parentIdentity, parentIdentity);
            Volatile.Write(ref _basename, basename);
            Volatile.Write(ref _rootLockCountAtPause, activeRootLocks);
            Volatile.Write(ref _memberLockCountAtPause, activeMemberLocks);
            _started.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }
    }

}
