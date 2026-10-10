using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedExactCacheHitRemainsGateFreeAndChangedProjectionIsGated(bool includeDependency)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");
        var template = fixture.Graphs["member-a"];
        var source = CreateGraphVariant(fixture, template, includeDependency,
            feedDirectory: "gate-source", marker: "GateSource");
        var replacement = CreateGraphVariant(fixture, template, includeDependency,
            feedDirectory: "gate-replacement", marker: "GateReplacement");
        var gate = new CountingPathIndependentActivationGate();
        var loader = new PackageLoader(activationGates: [gate]);
        var scopedLoader = (IScopedPackageLoader)loader;
        PackageGraphUseLeaseOwner? sourceOwner = null;
        PackageGraphUseLeaseOwner? repeatedSourceOwner = null;
        PackageGraphUseLeaseOwner? replacementOwner = null;
        PackageGraphUseLeaseOwner? repeatedReplacementOwner = null;

        try
        {
            sourceOwner = await AcquireGraphUseAsync(provider, source);
            var sourceResult = await scopedLoader.EnsureGraphLoadedAsync(
                [source.CreateEnvelope(sourceOwner!)], [], CancellationToken.None);
            Assert.Empty(sourceResult.FailedByPackageId);
            var sourceSession = Assert.Single(sourceResult.Loaded,
                session => session.PackageId == "Root.First");
            Assert.Equal(1, gate.EvaluationCount);

            repeatedSourceOwner = await AcquireGraphUseAsync(provider, source);
            var repeatedSourceResult = await scopedLoader.EnsureGraphLoadedAsync(
                [source.CreateEnvelope(repeatedSourceOwner!)], [], CancellationToken.None);
            Assert.Empty(repeatedSourceResult.FailedByPackageId);
            Assert.Same(sourceSession, Assert.Single(repeatedSourceResult.Loaded,
                session => session.PackageId == "Root.First"));
            Assert.Equal(1, gate.EvaluationCount);

            replacementOwner = await AcquireGraphUseAsync(provider, replacement);
            var replacementResult = await scopedLoader.EnsureGraphLoadedAsync(
                [replacement.CreateEnvelope(replacementOwner!)], [], CancellationToken.None);
            Assert.Empty(replacementResult.FailedByPackageId);
            var replacementSession = Assert.Single(replacementResult.Loaded,
                session => session.PackageId == "Root.First");
            Assert.NotSame(sourceSession, replacementSession);
            Assert.Equal(replacement.Packages.Single(package => package.Id == "Root.First").InstallPath,
                replacementSession.ActiveInstallPath);
            Assert.Equal(2, gate.EvaluationCount);

            repeatedReplacementOwner = await AcquireGraphUseAsync(provider, replacement);
            var repeatedReplacementResult = await scopedLoader.EnsureGraphLoadedAsync(
                [replacement.CreateEnvelope(repeatedReplacementOwner!)], [], CancellationToken.None);
            Assert.Empty(repeatedReplacementResult.FailedByPackageId);
            Assert.Same(replacementSession, Assert.Single(repeatedReplacementResult.Loaded,
                session => session.PackageId == "Root.First"));
            Assert.Equal(2, gate.EvaluationCount);
        }
        finally
        {
            if (sourceOwner is not null)
                await sourceOwner.DisposeAsync();
            if (repeatedSourceOwner is not null)
                await repeatedSourceOwner.DisposeAsync();
            if (replacementOwner is not null)
                await replacementOwner.DisposeAsync();
            if (repeatedReplacementOwner is not null)
                await repeatedReplacementOwner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    [Fact]
    public async Task ScopedConcurrentProjectionReplacementsNeverMaterializeBlockedProjection()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");
        var template = fixture.Graphs["member-a"];
        var variants = new[]
        {
            CreateGraphVariant(fixture, template, includeDependency: false,
                feedDirectory: "stress-a", marker: "StressA"),
            CreateGraphVariant(fixture, template, includeDependency: false,
                feedDirectory: "stress-b", marker: "StressB")
        };
        var blockedSourcePath = variants[0].Packages.Single().InstallPath;
        var gate = new SeededProjectionBlockingActivationGate(blockedSourcePath);
        var loader = new PackageLoader(activationGates: [gate]);
        var scopedLoader = (IScopedPackageLoader)loader;
        var owners = new List<PackageGraphUseLeaseOwner>();
        PackageGraphUseLeaseOwner? initialOwner = null;
        try
        {
            // Seed one projection so replacement requests can race with exact cache-hit requests.
            initialOwner = await AcquireGraphUseAsync(provider, variants[0]);
            var initialResult = await scopedLoader.EnsureGraphLoadedAsync(
                [variants[0].CreateEnvelope(initialOwner)], [], CancellationToken.None);
            Assert.Empty(initialResult.FailedByPackageId);
            var seededSession = Assert.Single(initialResult.Loaded,
                session => session.PackageId == "Root.First");

            for (var index = 0; index < 48; index++)
                owners.Add(await AcquireGraphUseAsync(provider, variants[index % variants.Length]));

            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = owners.Select((owner, index) => Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                return await scopedLoader.EnsureGraphLoadedAsync(
                    [variants[index % variants.Length].CreateEnvelope(owner)], [], CancellationToken.None);
            })).ToArray();
            start.SetResult(true);
            var results = await Task.WhenAll(loads);
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                if (index % variants.Length == 0)
                {
                    if (result.FailedByPackageId.ContainsKey("Root.First"))
                    {
                        Assert.Empty(result.Loaded);
                        continue;
                    }

                    Assert.Same(seededSession, Assert.Single(result.Loaded,
                        session => session.PackageId == "Root.First"));
                    continue;
                }

                Assert.Empty(result.FailedByPackageId);
                var loadedSession = Assert.Single(result.Loaded,
                    session => session.PackageId == "Root.First");
                Assert.Equal(variants[1].Packages.Single().InstallPath, loadedSession.ActiveInstallPath);
            }
        }
        finally
        {
            if (initialOwner is not null)
                await initialOwner.DisposeAsync();
            foreach (var owner in owners)
                await owner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    [Fact]
    public async Task ScopedSingletonCacheUsesExactInstallProjectionAndReusesExactProjection()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");

        var source = CreateGraphVariant(fixture, fixture.Graphs["member-a"], includeDependency: false,
            feedDirectory: "single-a", marker: "SingleA");
        var replacement = CreateGraphVariant(fixture, fixture.Graphs["member-a"], includeDependency: false,
            feedDirectory: "single-b", marker: "SingleB");

        await VerifyProjectionReplacementAndReuseAsync(provider, fixture, source, replacement, "Root.First", "SingleA", "SingleB");
        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    [Fact]
    public async Task ScopedSharedGraphCacheUsesEveryInstallProjectionAndReusesExactProjection()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");

        var source = CreateGraphVariant(fixture, fixture.Graphs["member-a"], includeDependency: true,
            feedDirectory: "graph-a", marker: "GraphA");
        var replacement = CreateGraphVariant(fixture, fixture.Graphs["member-a"], includeDependency: true,
            feedDirectory: "graph-b", marker: "GraphB");

        await VerifyProjectionReplacementAndReuseAsync(provider, fixture, source, replacement, "Root.First", "GraphA", "GraphB");
        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    [Fact]
    public async Task ScopedSharedGraphCacheIncludesInertNodeInstallProjection()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");
        var template = fixture.Graphs["member-a"];
        var inertIds = new HashSet<string>(["Shared.Dependency"], StringComparer.OrdinalIgnoreCase);
        var sharedPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.First"] = "same-root",
            ["Shared.Dependency"] = "inert-a"
        };
        var source = CreateGraphVariant(fixture, template, includeDependency: true,
            feedDirectory: "inert", marker: "InertA", perPackageFeedDirectories: sharedPath,
            packagesWithoutAssemblies: inertIds);
        var replacement = CreateGraphVariant(fixture, template, includeDependency: true,
            feedDirectory: "inert", marker: "InertB",
            perPackageFeedDirectories: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Root.First"] = "same-root",
                ["Shared.Dependency"] = "inert-b"
            }, packagesWithoutAssemblies: inertIds);

        PackageGraphUseLeaseOwner? sourceOwner = null;
        PackageGraphUseLeaseOwner? replacementOwner = null;
        try
        {
            var loader = provider.GetRequiredService<PackageLoader>();
            sourceOwner = await AcquireGraphUseAsync(provider, source);
            var first = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [source.CreateEnvelope(sourceOwner)], [], CancellationToken.None);
            Assert.Empty(first.FailedByPackageId);
            var scopedLoader = provider.GetRequiredService<IScopedPackageLoader>();
            Assert.True(scopedLoader.IsInertPackage(
                source.Packages.Single(package => package.Id == "Shared.Dependency"), sourceOwner.Lease));
            Assert.True(loader.TryGetContext("Root.First", "1.0.0", out var sourceHandle));
            var sourceContext = Assert.IsAssignableFrom<AssemblyLoadContext>(sourceHandle!.Context);

            replacementOwner = await AcquireGraphUseAsync(provider, replacement);
            Assert.False(scopedLoader.IsInertPackage(
                replacement.Packages.Single(package => package.Id == "Shared.Dependency"), replacementOwner.Lease));
            var second = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [replacement.CreateEnvelope(replacementOwner)], [], CancellationToken.None);
            Assert.Empty(second.FailedByPackageId);
            Assert.True(loader.TryGetContext("Root.First", "1.0.0", out var replacementHandle));
            Assert.NotSame(sourceContext, replacementHandle!.Context);
            Assert.Equal(source.Graph.GraphId, replacement.Graph.GraphId);
            Assert.Equal(source.Graph.GenerationId, replacement.Graph.GenerationId);
        }
        finally
        {
            if (sourceOwner is not null)
                await sourceOwner.DisposeAsync();
            if (replacementOwner is not null)
                await replacementOwner.DisposeAsync();
            provider.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    [Fact]
    public async Task ScopedInertLookupRequiresExactNativeInstallIdentityAfterSamePathReplacement()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");
        var inertIds = new HashSet<string>(["Shared.Dependency"], StringComparer.OrdinalIgnoreCase);
        var graph = CreateGraphVariant(fixture, fixture.Graphs["member-a"], includeDependency: true,
            feedDirectory: "same-path", marker: "SamePath",
            perPackageFeedDirectories: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Root.First"] = "same-root",
                ["Shared.Dependency"] = "same-inert"
            }, packagesWithoutAssemblies: inertIds);
        var inertPackage = graph.Packages.Single(package => package.Id == "Shared.Dependency");
        var exactInstallPath = inertPackage.InstallPath;
        var packageContentHash = inertPackage.PackageContentHash;
        var loader = provider.GetRequiredService<PackageLoader>();
        var scopedLoader = provider.GetRequiredService<IScopedPackageLoader>();
        PackageGraphUseLeaseOwner? originalOwner = null;
        PackageGraphUseLeaseOwner? replacementOwner = null;
        try
        {
            originalOwner = await AcquireGraphUseAsync(provider, graph);
            var originalResult = await scopedLoader.EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(originalOwner)], [], CancellationToken.None);
            Assert.Empty(originalResult.FailedByPackageId);
            Assert.True(scopedLoader.IsInertPackage(inertPackage, originalOwner.Lease));
            var originalIdentity = originalOwner.Lease.GetInstallIdentityForExactPath(inertPackage.InstallPath);

            await originalOwner.DisposeAsync();
            originalOwner = null;
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());

            // Fixture-only removal of freshly verified stale control artifacts precedes replacement.
            // This is not the production maintenance/reaping API, which remains separate delivery.
            await RemoveStaleFixtureUseArtifactsAsync(provider);
            Directory.Delete(exactInstallPath, recursive: true);
            WriteInstall(exactInstallPath, inertPackage.Id, inertPackage.Version,
                "ReplacementInert", dependencyId: null, includeAssembly: false);
            replacementOwner = await AcquireGraphUseAsync(provider, graph);
            var replacementIdentity = replacementOwner.Lease.GetInstallIdentityForExactPath(exactInstallPath);

            Assert.Equal(exactInstallPath, inertPackage.InstallPath);
            Assert.Equal(packageContentHash, inertPackage.PackageContentHash);
            Assert.Equal(exactInstallPath,
                graph.Packages.Single(package => package.Id == inertPackage.Id).InstallPath);
            Assert.NotEqual(originalIdentity, replacementIdentity);
            Assert.False(scopedLoader.IsInertPackage(inertPackage, replacementOwner.Lease));

            var replacementResult = await scopedLoader.EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(replacementOwner)], [], CancellationToken.None);
            Assert.Empty(replacementResult.FailedByPackageId);
            Assert.True(scopedLoader.IsInertPackage(inertPackage, replacementOwner.Lease));
        }
        finally
        {
            if (originalOwner is not null)
                await originalOwner.DisposeAsync();
            if (replacementOwner is not null)
                await replacementOwner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
    }

    private static async Task RemoveStaleFixtureUseArtifactsAsync(IServiceProvider provider)
    {
        await using var operation = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        await PackageStoreOperationAccess.WithValidatedRootAsync(borrow, async (files, root, token) =>
        {
            var ledger = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger;
            var inspection = await new PackageGraphUseRecordStore(files).InspectAsync(root, borrow.Root, ledger, token);
            Assert.NotEmpty(inspection.Entries);
            Assert.All(inspection.Entries, entry => Assert.Equal(GraphUseRecordOwnershipState.Stale, entry.OwnershipState));
            using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
            var publication = Assert.IsAssignableFrom<IPhysicalStorePublicationFileSystem>(files);
            foreach (var entry in inspection.Entries)
            {
                var suffix = entry.Record.UseId.ToString("N");
                var sentinelName = $"use-{suffix}.sentinel";
                var recordName = $"use-{suffix}.json";
                using (var sentinel = files.OpenFileChildNoFollow(control, sentinelName, FileAccess.ReadWrite))
                {
                    Assert.Equal(entry.Record.SentinelIdentity, files.InspectHandle(sentinel).Identity);
                    await using var sentinelLock = await files.TryAcquireExclusiveLock(sentinel);
                    Assert.NotNull(sentinelLock);
                    var record = files.InspectChildNoFollow(control, recordName);
                    Assert.NotNull(record);
                    publication.RemoveControlFileAt(control, recordName, record.Identity);
                }
                publication.RemoveControlFileAt(control, sentinelName, entry.Record.SentinelIdentity);
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class CountingPathIndependentActivationGate : IPackagePathIndependentActivationGate
    {
        private int _evaluationCount;
        internal int EvaluationCount => Volatile.Read(ref _evaluationCount);

        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _evaluationCount);
            return ValueTask.FromResult(PackageActivationGateResult.Allow);
        }
    }

    private sealed class SeededProjectionBlockingActivationGate(string blockedInstallPath)
        : IPackagePathIndependentActivationGate
    {
        private int _blockedProjectionEvaluationCount;

        public async ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            var evaluatesBlockedProjection = context.Packages.Any(package =>
                string.Equals(package.InstallPath, blockedInstallPath, StringComparison.Ordinal));
            if (!evaluatesBlockedProjection || Interlocked.Increment(ref _blockedProjectionEvaluationCount) == 1)
                return PackageActivationGateResult.Allow;

            return PackageActivationGateResult.Block("the seeded source projection is blocked after initial activation");
        }
    }

    private static async Task VerifyProjectionReplacementAndReuseAsync(
        IServiceProvider provider,
        GraphUseFixture fixture,
        ResolvedGraphFixture source,
        ResolvedGraphFixture replacement,
        string primaryPackageId,
        string sourceMarker,
        string replacementMarker)
    {
        var loader = provider.GetRequiredService<PackageLoader>();
        PackageGraphUseLeaseOwner? sourceOwner = null;
        PackageGraphUseLeaseOwner? replacementOwner = null;
        PackageGraphUseLeaseOwner? repeatedOwner = null;
        try
        {
            sourceOwner = await AcquireGraphUseAsync(provider, source);
            var sourceResult = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [source.CreateEnvelope(sourceOwner!)], [], CancellationToken.None);
            Assert.Empty(sourceResult.FailedByPackageId);

            Assert.True(loader.TryGetContext(primaryPackageId, "1.0.0", out var sourceHandle));
            var sourceContext = Assert.IsAssignableFrom<AssemblyLoadContext>(sourceHandle!.Context);
            var sourceAssembly = Assert.Single(sourceContext.Assemblies,
                assembly => string.Equals(assembly.GetName().Name, primaryPackageId, StringComparison.Ordinal));
            var sourcePath = source.Packages.Single(package => package.Id == primaryPackageId).InstallPath;
            Assert.Equal(sourcePath, sourceResult.Loaded.Single(session => session.PackageId == primaryPackageId).ActiveInstallPath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(sourcePath, primaryPackageId + ".dll")),
                File.ReadAllBytes(sourceAssembly.Location));
            Assert.Contains(sourceAssembly.GetTypes(), type => type.Name.Contains(sourceMarker, StringComparison.Ordinal));

            replacementOwner = await AcquireGraphUseAsync(provider, replacement);
            var replacementResult = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [replacement.CreateEnvelope(replacementOwner!)], [], CancellationToken.None);
            Assert.Empty(replacementResult.FailedByPackageId);

            Assert.True(loader.TryGetContext(primaryPackageId, "1.0.0", out var replacementHandle));
            var replacementContext = Assert.IsAssignableFrom<AssemblyLoadContext>(replacementHandle!.Context);
            var replacementAssembly = Assert.Single(replacementContext.Assemblies,
                assembly => string.Equals(assembly.GetName().Name, primaryPackageId, StringComparison.Ordinal));
            Assert.NotSame(sourceContext, replacementContext);
            var replacementPath = replacement.Packages.Single(package => package.Id == primaryPackageId).InstallPath;
            Assert.Equal(replacementPath, replacementResult.Loaded.Single(session => session.PackageId == primaryPackageId).ActiveInstallPath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(replacementPath, primaryPackageId + ".dll")),
                File.ReadAllBytes(replacementAssembly.Location));
            Assert.Contains(replacementAssembly.GetTypes(), type => type.Name.Contains(replacementMarker, StringComparison.Ordinal));

            // Caller disposal cannot release the old generation while its actual ALC is still retained.
            await sourceOwner.DisposeAsync();
            var liveRecords = await InspectGraphUseRecordsAsync(provider.GetRequiredService<IPackageStoreAdmission>(), fixture);
            Assert.All(liveRecords.Entries, entry => Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState));
            var retainedPaths = liveRecords.Entries.SelectMany(static entry => entry.Record.GraphSnapshot.Nodes)
                .Select(static node => node.Install.RootRelativeInstallPath).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(ToRootRelativePath(fixture, source.Packages.Single(package => package.Id == primaryPackageId).InstallPath), retainedPaths);
            Assert.Contains(ToRootRelativePath(fixture, replacement.Packages.Single(package => package.Id == primaryPackageId).InstallPath), retainedPaths);

            repeatedOwner = await AcquireGraphUseAsync(provider, replacement);
            var repeatedResult = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [replacement.CreateEnvelope(repeatedOwner!)], [], CancellationToken.None);
            Assert.Empty(repeatedResult.FailedByPackageId);
            Assert.True(loader.TryGetContext(primaryPackageId, "1.0.0", out var repeatedHandle));
            Assert.Same(replacementContext, repeatedHandle!.Context);
        }
        finally
        {
            if (sourceOwner is not null)
                await sourceOwner.DisposeAsync();
            if (replacementOwner is not null)
                await replacementOwner.DisposeAsync();
            if (repeatedOwner is not null)
                await repeatedOwner.DisposeAsync();
            loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static ResolvedGraphFixture CreateGraphVariant(
        GraphUseFixture fixture,
        ResolvedGraphFixture template,
        bool includeDependency,
        string feedDirectory,
        string marker,
        IReadOnlyDictionary<string, string>? perPackageFeedDirectories = null,
        IReadOnlySet<string>? packagesWithoutAssemblies = null)
    {
        var selectedIds = includeDependency
            ? template.Graph.Nodes.Select(static node => node.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : template.Graph.Roots.Select(static node => node.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPackages = template.Packages.Where(package => selectedIds.Contains(package.Id)).ToArray();
        var replacementPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in selectedPackages)
        {
            var packageFeedDirectory = perPackageFeedDirectories is not null &&
                perPackageFeedDirectories.TryGetValue(package.Id, out var selectedFeedDirectory)
                    ? selectedFeedDirectory
                    : feedDirectory;
            var path = Path.Combine(fixture.RootPath, packageFeedDirectory, package.Id, package.Version);
            WriteInstall(path, package.Id, package.Version, marker + package.Id.Replace('.', '_'),
                includeDependency && template.Graph.Edges.Any(edge =>
                    string.Equals(edge.FromPackageId, package.Id, StringComparison.OrdinalIgnoreCase))
                    ? template.Graph.Edges.First(edge => string.Equals(edge.FromPackageId, package.Id, StringComparison.OrdinalIgnoreCase)).ToPackageId
                    : null,
                includeAssembly: packagesWithoutAssemblies is null || !packagesWithoutAssemblies.Contains(package.Id));
            replacementPaths.Add(package.Id, path);
        }

        var nodes = template.Graph.Nodes
            .Where(node => selectedIds.Contains(node.PackageId))
            .Select(node => node with
            {
                InstallPath = replacementPaths[node.PackageId],
                RuntimeAssets = packagesWithoutAssemblies?.Contains(node.PackageId) == true ? [] : node.RuntimeAssets,
                DiscoverableAssets = packagesWithoutAssemblies?.Contains(node.PackageId) == true ? [] : node.DiscoverableAssets,
                SupportAssets = packagesWithoutAssemblies?.Contains(node.PackageId) == true ? [] : node.SupportAssets
            })
            .ToArray();
        var nodesByKey = nodes.ToDictionary(static node => node.PackageId, StringComparer.OrdinalIgnoreCase);
        var roots = template.Graph.Roots
            .Where(node => selectedIds.Contains(node.PackageId))
            .Select(node => nodesByKey[node.PackageId])
            .ToArray();
        var edges = includeDependency ? template.Graph.Edges.ToArray() : [];
        var decisions = template.Graph.SourceDecisions
            .Where(decision => selectedIds.Contains(decision.PackageId)).ToArray();
        var graphId = ResolvedPackageGraph.CreateGraphId(
            template.Graph.TargetFramework, roots, nodes, edges, decisions);
        var graph = template.Graph with
        {
            GraphId = graphId,
            Roots = roots,
            Nodes = nodes,
            Edges = edges,
            SourceDecisions = decisions
        };
        var packages = selectedPackages.Select(package => package with { InstallPath = replacementPaths[package.Id] }).ToArray();
        return new(graph, packages, template.Requests);
    }

    private static string ToRootRelativePath(GraphUseFixture fixture, string path) =>
        Path.GetRelativePath(fixture.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void WriteInstall(
        string path,
        string packageId,
        string version,
        string markerTypeName,
        string? dependencyId,
        bool includeAssembly = true)
    {
        Directory.CreateDirectory(path);
        var dependency = dependencyId is null
            ? string.Empty
            : $"<dependencies><dependency id=\"{dependencyId}\" version=\"[2.1.0]\" /></dependencies>";
        File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
            $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependency}</metadata></package>");
        if (includeAssembly)
            WriteAssembly(Path.Combine(path, packageId + ".dll"), packageId, markerTypeName);
        File.WriteAllText(Path.Combine(path, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName),
            "{\"schemaVersion\":2,\"loading\":{\"loadMode\":\"Collectible\",\"scope\":\"DependencyClosure\"}}");
        File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
    }

    private static void WriteAssembly(string path, string assemblyName, string markerTypeName)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(assemblyName);
        module.DefineType(markerTypeName, TypeAttributes.Public | TypeAttributes.Class).CreateType();
        assembly.Save(path);
    }
}
