using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Tests.Shared;
using AdmissionFixture = Nuplane.Store.Tests.Coordination.PackageStoreOperationRootAccessTests.AdmissionFixture;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageGraphUseInstallBindingTests
{
    [SupportedPhysicalStoreFact]
    public async Task Observe_ActualResolvedClosure_BindsEveryExactPathAndNativeInstall()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var context = fixture.Context;
        var graph = context.Graphs["first"];
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);

        var candidate = await PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (files, root, _) =>
        {
            using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, graph.Nodes);
            Assert.Equal(graph.Nodes.Select(static node => node.InstallPath), binding.Installs.Select(static install => install.ExactInstallPath));
            var paired = PackageGraphUseInstallBinding.CreateActiveCandidate(graph, context.Requests["first"], [binding]);
            foreach (var node in paired.Candidate.Nodes)
                Assert.Equal(Assert.Single(binding.Installs, install => install.Identity == node.Install).ExactInstallPath,
                    paired.ExactPathsByNode[node.NodeId]);
            binding.Revalidate();
            return Task.FromResult(paired.Candidate);
        });

        Assert.Equal(ProtectedGraphDisposition.Active, candidate.Disposition);
        Assert.Null(candidate.RecoverySelectionEvidence);
        var persisted = Assert.Single(context.States["first"].ProtectionRecord!.ActiveClosure.Graphs!);
        Assert.Equal(persisted.Nodes.Select(static node => node.Install).OrderBy(static install => install.PackageId),
            candidate.Nodes.Select(static node => node.Install).OrderBy(static install => install.PackageId));
        Assert.Contains(candidate.Nodes, static node => node.Install.PackageId == "Shared.Dependency");
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateActiveCandidate_BindingPairedWithChangedGraphPath_RefusesDespiteSameGraphId()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var context = fixture.Context;
        var graph = context.Graphs["first"];
        var changedNodes = graph.Nodes.Select(node => node with
        {
            InstallPath = Path.Combine(context.Fixture.PackageInstallRoot, "other", "feed", node.PackageId, node.Version)
        }).ToArray();
        var changed = graph with { Nodes = changedNodes };
        Assert.Equal(graph.GraphId, ResolvedPackageGraph.CreateGraphId(changed.TargetFramework,
            changed.Roots, changed.Nodes, changed.Edges, changed.SourceDecisions));
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);

        await PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (files, root, _) =>
        {
            using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, graph.Nodes);
            Assert.Throws<PackageStoreAdmissionException>(() => PackageGraphUseInstallBinding.CreateActiveCandidate(
                changed, context.Requests["first"], [binding]));
            return Task.FromResult(true);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task Observe_PathChangedWithoutGraphIdChange_RefusesDifferentNativeInstall()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var context = fixture.Context;
        var graph = context.Graphs["first"];
        var changed = graph.Nodes.Select(node => node.Role == PackageNodeRole.Dependency ? node : node with
        {
            InstallPath = context.Fixture.CreateDirectory($"packages/other/feed/{node.PackageId}/{node.Version}")
        }).ToArray();
        Assert.Equal(graph.GraphId, ResolvedPackageGraph.CreateGraphId(graph.TargetFramework,
            graph.Roots, changed, graph.Edges, graph.SourceDecisions));
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => PackageStoreOperationAccess.WithValidatedRootAsync(
            fixture.Borrow, (files, root, _) =>
            {
                using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, changed);
                return Task.FromResult(binding.Installs.Count);
            }));
    }

    [SupportedPhysicalStoreFact]
    public async Task Observe_OriginalPathSpelling_IsPreservedAfterNativeVerification()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var context = fixture.Context;
        var graph = context.Graphs["first"];
        var spelled = graph.Nodes.Select(node => node with
        {
            InstallPath = Path.Combine(context.Fixture.PackageInstallRoot, ".", "feed", node.PackageId, node.Version)
        }).ToArray();
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);

        await PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (files, root, _) =>
        {
            using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, spelled);
            Assert.Equal(spelled.Select(static node => node.InstallPath), binding.Installs.Select(static install => install.ExactInstallPath));
            binding.Revalidate();
            return Task.FromResult(true);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task Observe_ForeignRootOrDuplicateNativePath_RefusesTheSelection()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        using var foreign = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var context = fixture.Context;
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);
        var graph = context.Graphs["first"];

        foreach (var nodes in new[] { foreign.Graphs["first"].Nodes, graph.Nodes.Append(graph.Nodes[0]).ToArray() })
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => PackageStoreOperationAccess.WithValidatedRootAsync(
                fixture.Borrow, (files, root, _) =>
                {
                    using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, nodes);
                    return Task.FromResult(true);
                }));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task Observe_PartialFailure_ReleasesObservationsAndAllowsFreshVerification()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var context = fixture.Context;
        var graph = context.Graphs["first"];
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);

        await PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (files, root, _) =>
        {
            var missing = graph.Nodes[^1] with { InstallPath = Path.Combine(context.Fixture.PackageInstallRoot, "missing", "absent", "1.0.0") };
            Assert.Throws<PackageStoreAdmissionException>(() => PackageGraphUseInstallBinding.Observe(
                files, context.Registry, root, members.Ledger, [graph.Nodes[0], missing]));
            using var binding = PackageGraphUseInstallBinding.Observe(files, context.Registry, root, members.Ledger, graph.Nodes);
            binding.Revalidate();
            binding.Dispose();
            binding.Dispose();
            Assert.Throws<ObjectDisposedException>(binding.Revalidate);
            return Task.FromResult(true);
        });
    }
}
