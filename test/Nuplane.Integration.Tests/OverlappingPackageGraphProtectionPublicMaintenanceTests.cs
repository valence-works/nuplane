using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Admin;
using Nuplane.Store.Coordination;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using CoreExecutionService = Nuplane.Store.Maintenance.PackageStoreExecutionService;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicMaintenance_DefaultPreviewPreservesBytesAndMissingPolicyRetainsAll()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var before = CaptureFixtureNativeEvidence(fixture);

        var preview = await operations.PruneAsync(new PackageStorePruneRequest());

        Assert.Equal(PackageStorePruneMode.Preview, preview.Mode);
        Assert.Equal("Preview", preview.Status);
        Assert.True(preview.InventoryComplete);
        Assert.NotEmpty(preview.Candidates);
        Assert.All(preview.Candidates, candidate =>
        {
            Assert.Equal("Retained", candidate.Classification);
            Assert.Equal("NotAttempted", candidate.Attempt);
            Assert.Null(candidate.PhysicalOutcome);
        });
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));

        var noOp = await operations.PruneAsync(Confirm(preview, retention: null));

        Assert.Equal("Completed", noOp.Status);
        Assert.All(noOp.Candidates, candidate => Assert.Equal("Retained", candidate.PhysicalOutcome));
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PublicMaintenance_ExecuteReplansAfterPreviewAndPreservesNewProtection()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        var newlyProtected = Assert.Single(preview.Candidates, static candidate =>
            candidate.Install.PackageId == "Root.Second" && candidate.Install.Version == "2.0.0");
        Assert.Equal("Eligible", newlyProtected.Classification);

        // Change protection under the real root/state publication boundary after taking the preview.
        await using (var owner = await provider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading))
        {
            var borrow = owner.Owner!.Borrow();
            try
            {
                var locked = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                var current = await locked.ReadMemberStateAsync("member-b", CancellationToken.None);
                var updated = fixture.BuildProtectedState("member-b", fixture.Graphs["member-b-v2"], current!,
                    current!.ProtectionRecord!.Revision + 1, isUpdate: true);
                await locked.PublishStateAsync("member-b", updated, CancellationToken.None);
            }
            finally { borrow.Dispose(); }
        }
        var stateBefore = CaptureMaintenanceState(fixture);

        var executed = await operations.PruneAsync(Confirm(preview, retention));

        Assert.Equal("Completed", executed.Status);
        var protectedResult = Assert.Single(executed.Candidates, candidate => candidate.Install == newlyProtected.Install);
        Assert.Equal("Retained", protectedResult.Classification);
        Assert.Equal("Retained", protectedResult.PhysicalOutcome);
        Assert.True(Directory.Exists(Path.Combine(fixture.RootPath, newlyProtected.Install.RootRelativeInstallPath)));
        Assert.Contains(executed.Candidates, candidate => candidate.Install.PackageId == "Unrelated.Package" && candidate.PhysicalOutcome == "Deleted");
        Assert.All(executed.Candidates.Where(static candidate => candidate.PhysicalOutcome == "Deleted"), candidate =>
            Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, candidate.Install.RootRelativeInstallPath))));
        AssertMaintenanceStateEqual(stateBefore, fixture);

        var repeated = await operations.PruneAsync(Confirm(preview, retention));
        Assert.Equal("Completed", repeated.Status);
        Assert.DoesNotContain(repeated.Candidates, static candidate => candidate.PhysicalOutcome == "Deleted");
        AssertMaintenanceStateEqual(stateBefore, fixture);
    }

    [Fact]
    public async Task PublicMaintenance_RejectsUnconfirmedOrChangedRootBeforeDeleting()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        var before = CaptureFixtureNativeEvidence(fixture);

        await Assert.ThrowsAsync<ArgumentException>(() => operations.PruneAsync(
            new PackageStorePruneRequest(Retention: retention, Mode: PackageStorePruneMode.Execute)));
        var request = Confirm(preview, retention);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.PruneAsync(request with
        {
            Retention = new PackageStoreRetentionPolicy(1)
        }));
        var root = preview.RootIdentity!.HandleIdentity;
        var refused = await operations.PruneAsync(request with
        {
            Confirmation = request.Confirmation! with
            {
                RootIdentity = new PhysicalRootIdentity(new PhysicalFileIdentity(root.Provider, root.VolumeOrDeviceId, root.FileId + "-changed"))
            }
        });
        Assert.Equal("Refused", refused.Status);
        Assert.Contains(refused.Diagnostics, static diagnostic => diagnostic.Code == "RootMismatch");
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PublicMaintenance_CancellationAfterRemovalReturnsPartialFactsAndCanResume()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var checkpointCount = 0;
        await using var provider = CreateMaintenanceProvider(fixture, services =>
            services.AddSingleton(sp => new CoreExecutionService(
                ((PackageStoreAdmission)sp.GetRequiredService<IPackageStoreAdmission>()).Registry,
                sp.GetRequiredService<PackageStoreInspectionService>(),
                deletionExecutionCheckpoint: (point, _) =>
                {
                    if (point == PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved &&
                        Interlocked.Increment(ref checkpointCount) == 1)
                        cancellation.Cancel();
                })));
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        var states = CaptureMaintenanceState(fixture);

        var partial = await operations.PruneAsync(Confirm(preview, retention), cancellation.Token);

        Assert.Equal("Cancelled", partial.Status);
        var interrupted = Assert.Single(partial.Candidates, static candidate => candidate.Attempt == "Cancelled");
        Assert.Equal("PartiallyRemoved", interrupted.PhysicalOutcome);
        Assert.NotEmpty(interrupted.MissingAtLastObservation!);
        AssertMaintenanceStateEqual(states, fixture);

        var resumed = await operations.PruneAsync(Confirm(preview, retention));
        Assert.Equal("Completed", resumed.Status);
        Assert.Contains(resumed.Candidates, candidate => candidate.Install == interrupted.Install && candidate.PhysicalOutcome == "Deleted");
        AssertMaintenanceStateEqual(states, fixture);
    }

    private static ServiceProvider CreateMaintenanceProvider(GraphUseFixture fixture, Action<IServiceCollection>? configure = null)
        => fixture.CreateProvider("member-a", participant: true, configureServices: services =>
        {
            services.AddNuplanePackageStoreMaintenance();
            configure?.Invoke(services);
        });

    private static PackageStorePruneRequest Confirm(PackageStoreMaintenanceResult preview, PackageStoreRetentionPolicy? retention)
        => new(preview.RootLabel, retention, PackageStorePruneMode.Execute,
            new(preview.RootLabel, preview.RootIdentity!, preview.EnrollmentEpoch!.Value, retention));

    private static Dictionary<string, byte[]> CaptureMaintenanceState(GraphUseFixture fixture)
        => fixture.StatePaths.ToDictionary(static pair => pair.Key, static pair => File.ReadAllBytes(pair.Value), StringComparer.Ordinal);

    private static void AssertMaintenanceStateEqual(Dictionary<string, byte[]> expected, GraphUseFixture fixture)
    {
        foreach (var pair in expected)
            Assert.Equal(pair.Value, File.ReadAllBytes(fixture.StatePaths[pair.Key]));
    }
}
