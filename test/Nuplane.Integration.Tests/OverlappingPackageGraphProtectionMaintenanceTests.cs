using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task MaintenanceInspection_OverlapsActualLoadingAndRetainsLiveOnlyOldGraph()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var providerA = fixture.CreateProvider("member-a",
            desiredRequests: fixture.Graphs["member-a"].Requests);
        await using var providerB = fixture.CreateProvider("member-b",
            desiredRequests: fixture.Graphs["member-b-v2"].Requests);

        PackageGraphUseLeaseOwner? oldGraphOwner = null;
        PackageLoadResult? oldGraphLoad = null;
        AssemblyLoadContext? oldGraphContext = null;
        MetadataReadBarrier? loadingReadBarrier = null;
        Task<ReconciliationRunResult>? cycleA = null;
        Exception? primaryFailure = null;
        try
        {
            oldGraphOwner = await AcquireGraphUseAsync(providerB, fixture.Graphs["member-b"]);
            oldGraphLoad = await providerB.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [fixture.Graphs["member-b"].CreateEnvelope(oldGraphOwner)], [], CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty(oldGraphLoad.FailedByPackageId);
            var oldGraphKey = PackageLoader.BuildGraphKey(fixture.Graphs["member-b"].Packages);
            oldGraphContext = Assert.Single(AssemblyLoadContext.All,
                context => context.IsCollectible && string.Equals(context.Name, oldGraphKey, StringComparison.Ordinal));
            Assert.Equal(
                new[] { "Root.Second", "Shared.Dependency" }.ToHashSet(StringComparer.OrdinalIgnoreCase),
                oldGraphContext.Assemblies.Select(static assembly => assembly.GetName().Name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            await oldGraphOwner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            var retainedOldUse = Assert.Single((await InspectGraphUseRecordsAsync(
                providerB.GetRequiredService<IPackageStoreAdmission>(), fixture)).Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, retainedOldUse.OwnershipState);
            Assert.Equal(oldGraphOwner.Lease.Snapshot.GenerationId, retainedOldUse.Record.GraphSnapshot.GenerationId);

            // Ignore earlier package-metadata reads under Phase A's root lock. The fixture barrier
            // claims the first metadata file opened only after the real root/member owner is released,
            // which is PackageMetadataLoadModeAdvisor's read in deferred Loading with the published
            // graph-use lease and exact-path read pins already in place.
            loadingReadBarrier = fixture.Files.BlockNextMetadataReadAfterRootUnlock();
            cycleA = Task.Run(() => providerA.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("maintenance-overlap-a"), CancellationToken.None));
            await loadingReadBarrier.WaitUntilBlockedAsync();
            Assert.Equal(0, loadingReadBarrier.RootLockCountAtPause);
            Assert.Equal(0, loadingReadBarrier.MemberLockCountAtPause);
            Assert.True(fixture.Files.SuccessfulRootLockAcquisitions > 0);
            Assert.True(fixture.Files.SuccessfulMemberLockAcquisitions > 0);
            Assert.False(cycleA.IsCompleted);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resultB = await providerB.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("maintenance-overlap-b"), deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(resultB.Skipped);

            var stateB = await providerB.GetRequiredService<IStoreRegistry>()
                .GetStateAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("2.0.0", stateB.ActiveVersionById["Root.Second"]);
            Assert.Equal("2.0.0", stateB.LastKnownGoodById["Root.Second"]);
            var verifiedB = PersistedStoreStateGraphVerifier.Verify(stateB);
            var oldRootInstall = oldGraphOwner.Lease.Snapshot.Nodes
                .Single(static node => string.Equals(node.Install.PackageId, "Root.Second", StringComparison.OrdinalIgnoreCase))
                .Install;
            Assert.DoesNotContain(verifiedB.ActiveGraphs.Concat(verifiedB.RecoverableGraphs)
                .SelectMany(static graph => graph.Nodes)
                .Select(static node => node.Install), install => install == oldRootInstall);
            AssertGraphLoaded(providerB, fixture.Graphs["member-b-v2"], "Root.Second", "Shared.Dependency");
            Assert.Contains(AssemblyLoadContext.All,
                context => context.IsCollectible &&
                           string.Equals(context.Name, PackageLoader.BuildGraphKey(fixture.Graphs["member-b-v2"].Packages),
                               StringComparison.Ordinal));

            var nativeBefore = CaptureFixtureNativeEvidence(fixture);
            var inspectionService = new PackageStoreInspectionService(
                fixture.Files,
                providerB.GetRequiredService<IPackageStoreAdmission>(),
                new PackageStoreInventory(fixture.Files),
                new PackageStoreRetentionPlanner());
            var inspection = await inspectionService.InspectAsync(0, deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
            var nativeAfter = CaptureFixtureNativeEvidence(fixture);

            Assert.False(cycleA.IsCompleted);
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, inspection.AdmissionStatus);
            Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, inspection.ProtectionKnowledge);
            Assert.Equal(fixture.RootIdentity, inspection.Root);
            Assert.Equal(37, inspection.EnrollmentEpoch);
            var inventory = Assert.IsType<PackageStoreInventorySnapshot>(inspection.Inventory);
            Assert.True(inventory.IsComplete);
            Assert.Equal(fixture.RootIdentity, inventory.Root);
            Assert.Equal(inspection.EnrollmentEpoch, inventory.Epoch);
            Assert.Equal(2, inspection.Members.Count);
            Assert.Equal(3, inspection.Uses.Count);
            Assert.All(inspection.Uses,
                static use => Assert.Equal(GraphUseRecordOwnershipState.Live, use.OwnershipState));
            Assert.NotNull(inspection.RetentionPlan);
            Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known,
                inspection.RetentionPlan!.ProtectionKnowledge);
            Assert.Equal(PackageStoreRetentionInventoryStatus.Complete,
                inspection.RetentionPlan.InventoryStatus);
            Assert.Equal(0, inspection.RetentionPlan.KeepNewestInactiveVersionsPerPackage);

            Assert.Equal(nativeBefore.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                nativeAfter.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            Assert.Equal(nativeBefore.Count, inventory.Entries.Count(static entry =>
                entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate));
            foreach (var row in inventory.Entries.Where(static entry =>
                         entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate))
            {
                Assert.Equal(nativeBefore[row.RootRelativePath].Identity, row.InstallIdentity);
            }

            var aGraphInstalls = fixture.Graphs["member-a"].Packages
                .Select(package => nativeBefore[RootRelativePath(fixture, package.InstallPath)].Identity)
                .ToArray();
            var oldBGraphInstalls = fixture.Graphs["member-b"].Packages
                .Select(package => nativeBefore[RootRelativePath(fixture, package.InstallPath)].Identity)
                .ToArray();
            var newBGraphInstalls = fixture.Graphs["member-b-v2"].Packages
                .Select(package => nativeBefore[RootRelativePath(fixture, package.InstallPath)].Identity)
                .ToArray();

            // Assert the old Root.Second install first: after B's real transition it has no
            // persistent Active/LKG protection, so this row is protected solely by its retained ALC.
            var oldRootEntry = AssertPlanClassification(inspection, oldRootInstall,
                PackageStoreRetentionClassification.Retained,
                PackageStoreRetentionReason.ProtectedLiveUse);
            Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedActive, oldRootEntry.Reasons);
            Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood, oldRootEntry.Reasons);

            foreach (var install in oldBGraphInstalls.Where(install => install != oldRootInstall))
            {
                AssertPlanClassification(inspection, install,
                    PackageStoreRetentionClassification.Retained,
                    PackageStoreRetentionReason.ProtectedLiveUse);
            }

            foreach (var install in aGraphInstalls)
            {
                AssertPlanClassification(inspection, install, PackageStoreRetentionClassification.Retained,
                    PackageStoreRetentionReason.ProtectedActive,
                    PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood,
                    PackageStoreRetentionReason.ProtectedLiveUse);
            }

            foreach (var install in newBGraphInstalls)
            {
                AssertPlanClassification(inspection, install, PackageStoreRetentionClassification.Retained,
                    PackageStoreRetentionReason.ProtectedActive,
                    PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood,
                    PackageStoreRetentionReason.ProtectedLiveUse);
            }

            var unrelatedIdentity = nativeBefore[RootRelativePath(fixture, fixture.UnrelatedInstall.InstallPath)].Identity;
            AssertPlanClassification(inspection, unrelatedIdentity, PackageStoreRetentionClassification.Eligible,
                PackageStoreRetentionReason.OutsideInactiveRetentionBudget);
            var eligibleEntries = inspection.RetentionPlan!.Entries
                .Where(static entry => entry.Classification == PackageStoreRetentionClassification.Eligible)
                .ToArray();
            Assert.Equal(unrelatedIdentity, Assert.Single(eligibleEntries).Install);

            await AssertInspectionMatchesRereadMemberStatesAsync(inspection, providerA, providerB, deadline.Token);

            loadingReadBarrier.Release();
            var resultA = await cycleA.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(resultA.Skipped);
            AssertGraphLoaded(providerA, fixture.Graphs["member-a"], "Root.First", "Shared.Dependency");
            AssertGraphLoaded(providerB, fixture.Graphs["member-b-v2"], "Root.Second", "Shared.Dependency");
            Assert.True(oldGraphContext.IsCollectible);
            Assert.Equal(fixture.Graphs["member-b"].Graph.GenerationId,
                oldGraphOwner.Lease.Snapshot.GenerationId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        var cleanupFailures = new List<Exception>();
        loadingReadBarrier?.Release();
        if (cycleA is not null)
        {
            try
            {
                await cycleA.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (oldGraphOwner is not null)
        {
            try { await oldGraphOwner.DisposeAsync(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try
        {
            providerA.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            providerB.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        oldGraphContext = null;
        oldGraphLoad = null;
        try
        {
            await fixture.WaitForGraphUseReleaseAsync(providerA.GetRequiredService<IPackageStoreAdmission>());
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The overlap assertion and graph-use cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Graph-use cleanup failed after the overlap test.", cleanupFailures);
    }

    private static PackageStoreRetentionPlanEntry AssertPlanClassification(
        PackageStoreInspectionSnapshot inspection,
        PackageInstallIdentity install,
        PackageStoreRetentionClassification classification,
        params PackageStoreRetentionReason[] reasons)
    {
        var entry = Assert.Single(inspection.RetentionPlan!.Entries, candidate => candidate.Install == install);
        Assert.Equal(classification, entry.Classification);
        foreach (var reason in reasons)
            Assert.Contains(reason, entry.Reasons);
        return entry;
    }

    private static async Task AssertInspectionMatchesRereadMemberStatesAsync(
        PackageStoreInspectionSnapshot inspection,
        IServiceProvider providerA,
        IServiceProvider providerB,
        CancellationToken cancellationToken)
    {
        foreach (var (memberId, provider) in new[]
                 {
                     ("member-a", providerA),
                     ("member-b", providerB)
                 })
        {
            var state = await provider.GetRequiredService<IStoreRegistry>()
                .GetStateAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
            var member = Assert.Single(inspection.Members, candidate => candidate.MemberId == memberId);
            Assert.Equal(protection.Revision, member.Revision);
            Assert.Equal(protection.StateBodyDigest, member.StateBodyDigest);
            Assert.Equal(protection.ProtectionDigest, member.ProtectionDigest);
        }
    }

    private static Dictionary<string, NativeInstallEvidence> CaptureFixtureNativeEvidence(GraphUseFixture fixture)
    {
        var packages = fixture.Graphs.Values.SelectMany(static graph => graph.Packages)
            .Append(fixture.UnrelatedInstall)
            .DistinctBy(static package => package.InstallPath, StringComparer.Ordinal)
            .OrderBy(static package => package.InstallPath, StringComparer.Ordinal)
            .ToArray();
        var reader = new PackageInstallIdentityReader(fixture.Files);
        var evidence = new Dictionary<string, NativeInstallEvidence>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var relativePath = RootRelativePath(fixture, package.InstallPath);
            using var observation = reader.Observe(fixture.Root, fixture.RootIdentity, relativePath,
                package.Id, package.Version);
            var payloadFiles = new[]
            {
                package.Id + ".nuspec",
                package.Id + ".dll",
                Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName,
                PackageInstallStore.CompletionMarkerFileName
            };
            var payloadDigest = string.Join("\n", payloadFiles.Select(fileName =>
                fileName + ":" + Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(package.InstallPath, fileName))))));
            evidence.Add(relativePath, new NativeInstallEvidence(observation.InstallIdentity, payloadDigest));
        }

        return evidence;
    }

    private static string RootRelativePath(GraphUseFixture fixture, string installPath)
        => Path.GetRelativePath(fixture.RootPath, installPath).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record NativeInstallEvidence(PackageInstallIdentity Identity, string PayloadDigest);
}
