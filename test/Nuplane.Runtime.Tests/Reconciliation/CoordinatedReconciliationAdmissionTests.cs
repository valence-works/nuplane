using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Events;
using Nuplane.Hosting;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Models;
using Nuplane.Registration;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class CoordinatedReconciliationAdmissionTests
{
    public enum UnknownParticipant
    {
        DiffEngine,
        DryRunPlanner,
        DryRunPlannerWithUnknownNestedDiff,
        LockFileCoordinator,
        RetryPolicy
    }

    [Fact]
    public async Task TwoAddNuplaneProviders_SerializeScopedSourceReadsAndPublishFailureAndStateUnderTheirMembers()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var initialFirst = fixture.States["first"];
        var initialSecond = fixture.States["second"];
        var firstEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSource = new ScopedFileSource(fixture.SharedInstallPath, firstEntered, releaseFirst.Task,
            failAfterRead: false);
        var secondSource = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null,
            failAfterRead: true);

        using var firstProvider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], firstSource);
        using var secondProvider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["second"], secondSource);
        var firstService = firstProvider.GetRequiredService<IReconciliationService>();
        var secondService = secondProvider.GetRequiredService<IReconciliationService>();
        var firstCycle = firstService.TriggerAsync(ReconciliationTrigger.Manual("first-cycle"), CancellationToken.None);

        try
        {
            Assert.Equal("shared sentinel", await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            // The first scoped callback has already proved the root and both state slots are owned.
            // Cancellation is the bounded observation that the second independent provider cannot
            // reach its callback while that ownership remains live. The native lock may fail closed
            // immediately with a typed busy refusal instead of waiting for cancellation.
            using var blockedAttempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var blockedResult = await Record.ExceptionAsync(() =>
                secondService.TriggerAsync(ReconciliationTrigger.Manual("blocked-cycle"), blockedAttempt.Token));
            Assert.True(blockedResult is OperationCanceledException ||
                blockedResult is PackageStoreAdmissionException { Reason: PackageStoreAdmissionReason.UnknownAuthority },
                $"Expected a bounded cancellation or native root-lock busy refusal, got {blockedResult?.GetType().Name ?? "no exception"}.");
            Assert.Equal(0, secondSource.CallbackCount);
            Assert.Equal(0, secondSource.SentinelReadCount);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await firstCycle;
        await secondService.TriggerAsync(ReconciliationTrigger.Manual("second-cycle"), CancellationToken.None);

        Assert.Equal(1, firstSource.CallbackCount);
        Assert.Equal(1, firstSource.SentinelReadCount);
        Assert.Equal(1, secondSource.CallbackCount);
        Assert.Equal(1, secondSource.SentinelReadCount);

        var firstAfter = await fixture.ReadFreshStateAsync("first");
        var secondAfter = await fixture.ReadFreshStateAsync("second");
        Assert.Equal(initialFirst.ActiveVersionById, firstAfter.ActiveVersionById);
        Assert.Equal(initialSecond.ActiveVersionById, secondAfter.ActiveVersionById);
        Assert.Equal(initialFirst.ProtectionRecord!.Revision + 2, firstAfter.ProtectionRecord!.Revision);
        Assert.Equal(initialSecond.ProtectionRecord!.Revision + 2, secondAfter.ProtectionRecord!.Revision);
        Assert.Single(firstAfter.LastSuccessfulSourceSnapshots);
        Assert.Empty(secondAfter.LastSuccessfulSourceSnapshots);
        var failure = Assert.Single(secondAfter.LastFailureById.Values);
        Assert.Equal("source-read", failure.Stage);
        Assert.Equal("second-cycle", failure.CorrelationId);
    }

    [Fact]
    public async Task ThirdUnlistedStateSlot_IsRefusedBeforeSourceCallback()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var unlistedStatePath = Path.Combine(fixture.Temp.Path, "third-state", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unlistedStatePath)!);
        var source = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null, failAfterRead: false);
        using var provider = CreateProvider(fixture.PackageInstallRoot, unlistedStatePath, source);
        var service = provider.GetRequiredService<IReconciliationService>();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.TriggerAsync(ReconciliationTrigger.Manual("unlisted-state"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);
    }

    [Fact]
    public async Task UnscopedCustomSource_IsRefusedBeforeAnyCallback()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new UnscopedSourceProbe();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source);
        var service = provider.GetRequiredService<IReconciliationService>();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.TriggerAsync(ReconciliationTrigger.Manual("unscoped-source"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(fixture.States["first"].ProtectionRecord!.Revision, after.ProtectionRecord!.Revision);
    }

    [Fact]
    public async Task EnabledManifestInsideEnrolledInstall_IsRefusedBeforeManifestRead()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = true;
        options.Manifest.Path = Path.Combine(fixture.SharedInstallPath, "desired.json");
        await File.WriteAllTextAsync(options.Manifest.Path,
            """{"schemaVersion":"1.0","packages":[]}""");
        var source = new DesiredManifestPackageSource(new DesiredManifestReader(), options);
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source);
        var service = provider.GetRequiredService<IReconciliationService>();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.TriggerAsync(ReconciliationTrigger.Manual("enrolled-manifest"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Null(source.LastReadResult);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(fixture.States["first"].ProtectionRecord!.Revision, after.ProtectionRecord!.Revision);

        // The manifest is valid and the real source can read it; refusal came from preflight.
        Assert.Empty(await source.GetDesiredAsync(CancellationToken.None));
        Assert.Equal(ManifestReadStatus.Succeeded, source.LastReadResult!.Status);
    }

    [Theory]
    [InlineData(UnknownParticipant.DiffEngine)]
    [InlineData(UnknownParticipant.DryRunPlanner)]
    [InlineData(UnknownParticipant.DryRunPlannerWithUnknownNestedDiff)]
    [InlineData(UnknownParticipant.LockFileCoordinator)]
    [InlineData(UnknownParticipant.RetryPolicy)]
    public async Task UnknownPathConsumingParticipant_IsRefusedBeforeSourceCallback(UnknownParticipant participant)
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null,
            failAfterRead: false);
        var diffEngine = participant == UnknownParticipant.DiffEngine
            ? Substitute.For<IDesiredActualDiffEngine>()
            : null;
        var dryRunPlanner = participant switch
        {
            UnknownParticipant.DryRunPlanner => Substitute.For<IDryRunPlanner>(),
            UnknownParticipant.DryRunPlannerWithUnknownNestedDiff => new DryRunPlanner(
                Substitute.For<IDesiredActualDiffEngine>()),
            _ => null
        };
        var lockFileCoordinator = participant == UnknownParticipant.LockFileCoordinator
            ? Substitute.For<ILockFileCoordinator>()
            : null;
        var retryPolicy = participant == UnknownParticipant.RetryPolicy
            ? Substitute.For<IReconciliationRetryPolicy>()
            : null;
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            diffEngineOverride: diffEngine, dryRunPlannerOverride: dryRunPlanner,
            lockFileCoordinatorOverride: lockFileCoordinator, retryPolicyOverride: retryPolicy);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                ReconciliationTrigger.Manual("unknown-path-consumer"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);
    }

    [Fact]
    public async Task ScopedResolverReturningInstall_GraphReadFailureIsRecordedWithoutActiveTransition()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new SinglePackageSource("Root.First", fixture.SharedInstallPath);
        var resolver = new ControlledScopedResolver(fixture.SharedInstallPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.SharedInstallPath, "Shared.Dependency.nuspec"),
            "<package><metadata>");
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            resolverOverride: resolver);

        var result = await provider.GetRequiredService<IReconciliationService>().TriggerAsync(
            ReconciliationTrigger.Manual("invalid-graph-metadata"), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);
        Assert.Equal("Root.First", Assert.Single(result.FailedPackages));
        Assert.Equal(1, resolver.CallbackCount);
        var after = await fixture.ReadFreshStateAsync("first");
        var failure = Assert.Single(after.LastFailureById);
        Assert.Equal("Root.First", failure.Key);
        Assert.Equal("resolve", failure.Value.Stage);
        Assert.Equal("invalid-graph-metadata", failure.Value.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(failure.Value.Message));
        Assert.Equal(fixture.States["first"].ActiveVersionById, after.ActiveVersionById);
    }

    [Fact]
    public async Task EnrolledNonemptyTransition_IsRefusedBeforeChangingObserversOrTransactionArtifacts()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var candidatePath = Path.Combine(fixture.PackageInstallRoot, "Candidate.Root", "1.0.0");
        Directory.CreateDirectory(candidatePath);
        var source = new SinglePackageSource("Candidate.Root", candidatePath);
        // A path-free synthetic result reaches the transition guard without invoking a package
        // reader. The separate real-nuspec test covers the earlier production graph boundary.
        var resolver = new ControlledScopedResolver(candidatePath, returnInstallPath: false);
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            preserveActiveDiff: false, dispatcherOverride: dispatcher, resolverOverride: resolver);
        var entriesBefore = Directory.GetFileSystemEntries(fixture.PackageInstallRoot)
            .Order(StringComparer.Ordinal).ToArray();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                ReconciliationTrigger.Manual("nonempty-enrolled"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(1, source.CallbackCount);
        Assert.Equal(1, resolver.CallbackCount);
        Assert.Equal(0, dispatcher.ChangingCount);
        Assert.Equal(0, dispatcher.ChangedCount);
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(fixture.PackageInstallRoot)
            .Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ManualCustomStoreComposition_PreservesOnlyPositivelyUnenrolledRootBehavior()
    {
        using var temp = new TempDirectory();
        var unenrolledRoot = temp.CreateSubdirectory("unenrolled-packages");
        var customRegistry = Substitute.For<IStoreRegistry>();
        var admission = PackageStoreRuntimeAdmission.CreateManual(customRegistry,
            new FeedResolutionOptions { PackageInstallRoot = unenrolledRoot });

        await using (var positive = await admission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation, CancellationToken.None))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, positive.Status);
            Assert.Null(positive.Owner);
        }

        var customSerializer = Substitute.For<IStoreStateSerializer>();
        var customSerializerRegistry = new StoreRegistry(customSerializer, stateFilePath: null);
        var serializerAdmission = PackageStoreRuntimeAdmission.CreateManual(customSerializerRegistry,
            new FeedResolutionOptions { PackageInstallRoot = unenrolledRoot });
        await using (var serializerPositive = await serializerAdmission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation, CancellationToken.None))
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, serializerPositive.Status);

        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var protectedAdmission = PackageStoreRuntimeAdmission.CreateManual(customRegistry,
            new FeedResolutionOptions { PackageInstallRoot = fixture.PackageInstallRoot });
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await protectedAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());

        var protectedSerializerAdmission = PackageStoreRuntimeAdmission.CreateManual(customSerializerRegistry,
            new FeedResolutionOptions { PackageInstallRoot = fixture.PackageInstallRoot });
        var serializerRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await protectedSerializerAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, serializerRefusal.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddNuplaneCustomRegistry_PreservesUnenrolledExistingAndMissingRootBehavior(bool missingRoot)
    {
        using var temp = new TempDirectory();
        var root = missingRoot
            ? Path.Combine(temp.Path, "absent", "nested-packages")
            : temp.CreateSubdirectory("existing-packages");
        var customRegistry = Substitute.For<IStoreRegistry>();
        var observingSerializer = new ObservingProtectionSerializer();
        using var provider = CreateAdmissionProvider(root, customRegistry, observingSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, operation.Status);
        Assert.Null(operation.Owner);
        Assert.Equal(missingRoot, operation.Root is null);
        Assert.Equal(0, observingSerializer.PayloadReadCount);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddNuplaneUnsupportedSerializer_PreservesUnenrolledExistingAndMissingRootBehavior(bool missingRoot)
    {
        using var temp = new TempDirectory();
        var root = missingRoot
            ? Path.Combine(temp.Path, "absent", "nested-packages")
            : temp.CreateSubdirectory("existing-packages");
        var unsupportedSerializer = Substitute.For<IStoreStateSerializer>();
        using var provider = CreateAdmissionProvider(root, registryOverride: null, unsupportedSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, operation.Status);
        Assert.Null(operation.Owner);
        Assert.Equal(missingRoot, operation.Root is null);
        await unsupportedSerializer.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await unsupportedSerializer.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNuplaneCustomRegistry_RefusesCompleteRootBeforeSelectedStatePayloadOrRegistryRead()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var customRegistry = Substitute.For<IStoreRegistry>();
        var observingSerializer = new ObservingProtectionSerializer();
        using var provider = CreateAdmissionProvider(fixture.PackageInstallRoot, customRegistry, observingSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, observingSerializer.PayloadReadCount);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNuplaneUnsupportedSerializer_RefusesCompleteRootBeforeSelectedStatePayloadRead()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var unsupportedSerializer = Substitute.For<IStoreStateSerializer>();
        using var provider = CreateAdmissionProvider(fixture.PackageInstallRoot, registryOverride: null,
            unsupportedSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        await unsupportedSerializer.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await unsupportedSerializer.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicManualLkg_RefusesProtectedInstallPathBeforeValidationOrObservers()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var selectedRegistry = Substitute.For<IStoreRegistry>();
        selectedRegistry.GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fixture.States["first"]));
        var dispatcher = Substitute.For<IObserverEventDispatcher>();
        var recovery = new LastKnownGoodStartupRecoveryService(selectedRegistry, dispatcher,
            new StartupRecoveryState());

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            recovery.TryRecoverAsync("manual-protected-lkg", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(fixture.RootIdentity, error.Root);
        await selectedRegistry.Received(1).GetStateAsync(Arg.Any<CancellationToken>());
        Assert.Empty(dispatcher.ReceivedCalls());
    }

    private static ServiceProvider CreateProvider(
        string packageInstallRoot,
        string stateFilePath,
        IDesiredPackageSource source,
        bool preserveActiveDiff = true,
        IObserverEventDispatcher? dispatcherOverride = null,
        IPackageResolver? resolverOverride = null,
        IDesiredActualDiffEngine? diffEngineOverride = null,
        IDryRunPlanner? dryRunPlannerOverride = null,
        ILockFileCoordinator? lockFileCoordinatorOverride = null,
        IReconciliationRetryPolicy? retryPolicyOverride = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = packageInstallRoot);
        services.Configure<StoreRegistryOptions>(options => options.StateFilePath = stateFilePath);
        services.Configure<ReconciliationOptions>(options => options.MaxRetryAttempts = 0);

        services.RemoveAll<IDesiredPackageSource>();
        services.AddSingleton<IDesiredPackageSource>(source);
        services.RemoveAll<IDesiredStateContributor>();
        services.RemoveAll<IPackageResolver>();
        services.AddSingleton<IPackageResolver>(resolverOverride ?? (IPackageResolver)new NeverCalledScopedResolver());
        services.RemoveAll<IDesiredActualDiffEngine>();
        if (diffEngineOverride is not null)
            services.AddSingleton<IDesiredActualDiffEngine>(diffEngineOverride);
        else if (preserveActiveDiff)
            services.AddSingleton<IDesiredActualDiffEngine, PreserveActiveDiffEngine>();
        else
            services.AddSingleton<IDesiredActualDiffEngine, DesiredActualDiffEngine>();
        if (dryRunPlannerOverride is not null)
        {
            services.RemoveAll<IDryRunPlanner>();
            services.AddSingleton<IDryRunPlanner>(dryRunPlannerOverride);
        }
        if (lockFileCoordinatorOverride is not null)
        {
            services.RemoveAll<ILockFileCoordinator>();
            services.AddSingleton<ILockFileCoordinator>(lockFileCoordinatorOverride);
        }
        if (retryPolicyOverride is not null)
        {
            services.RemoveAll<IReconciliationRetryPolicy>();
            services.AddSingleton<IReconciliationRetryPolicy>(retryPolicyOverride);
        }
        if (dispatcherOverride is not null)
        {
            services.RemoveAll<IObserverEventDispatcher>();
            services.AddSingleton<IObserverEventDispatcher>(dispatcherOverride);
        }
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateAdmissionProvider(
        string packageInstallRoot,
        IStoreRegistry? registryOverride,
        IStoreStateSerializer serializerOverride)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = packageInstallRoot);
        services.RemoveAll<IStoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(serializerOverride);
        if (registryOverride is not null)
        {
            services.RemoveAll<IStoreRegistry>();
            services.AddSingleton<IStoreRegistry>(registryOverride);
        }
        return services.BuildServiceProvider();
    }

    private sealed class ObservingProtectionSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private int _payloadReadCount;

        internal int PayloadReadCount => Volatile.Read(ref _payloadReadCount);

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _payloadReadCount);
            return await _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class ScopedFileSource(
        string installDirectory,
        TaskCompletionSource<string>? entered,
        Task? release,
        bool failAfterRead) : IScopedDesiredPackageSource
    {
        private int _callbackCount;
        private int _sentinelReadCount;

        internal int CallbackCount => Volatile.Read(ref _callbackCount);
        internal int SentinelReadCount => Volatile.Read(ref _sentinelReadCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installDirectory);
            var text = await File.ReadAllTextAsync(Path.Combine(installDirectory, "sentinel.txt"), ct);
            Interlocked.Increment(ref _sentinelReadCount);
            entered?.TrySetResult(text);
            if (release is not null)
                await release.WaitAsync(ct);
            if (failAfterRead)
                throw new IOException("controlled desired-source failure after the scoped read");
            return Array.Empty<PackageRequest>();
        }
    }

    private sealed class UnscopedSourceProbe : IDesiredPackageSource
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _callbackCount);
            return Task.FromResult<IReadOnlyList<PackageRequest>>(Array.Empty<PackageRequest>());
        }
    }

    private sealed class NeverCalledScopedResolver : IScopedPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException("The empty source should not resolve packages."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException("The empty source should not resolve packages."));
    }

    private sealed class PreserveActiveDiffEngine : IPackagePathIndependentDesiredActualDiffEngine
    {
        public PackageChangeSet Compute(IReadOnlyCollection<ResolvedPackage> desired,
            IReadOnlyDictionary<string, string> activeVersions, string correlationId, DateTimeOffset timestamp)
            => new([], [], [], correlationId, timestamp);

        public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired)
            => desired.ToDictionary(static package => package.Id, static package => package.Version,
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SinglePackageSource(string packageId, string installPath) : IScopedDesiredPackageSource
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installPath);
            IReadOnlyList<PackageRequest> requests =
            [new PackageRequest(packageId, "1.0.0", "test-feed", PackageUpdatePolicy.Exact, "nonempty-source")];
            return Task.FromResult(requests);
        }
    }

    private sealed class ControlledScopedResolver(string installPath, bool returnInstallPath = true) : IScopedPackageResolver
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException(
                "An enrolled resolver must be invoked through its scoped overload."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installPath);
            return Task.FromResult(new ResolvedPackage(request.Id, "1.0.0", "test-feed", returnInstallPath ? installPath : string.Empty,
                DateTimeOffset.UnixEpoch, request.SourceName)
            {
                PackageContentHash = "sha512:" + Convert.ToBase64String(new byte[64])
            });
        }
    }

    private sealed class CountingScopedDispatcher : IScopedObserverEventDispatcher
    {
        private int _changingCount;
        private int _changedCount;

        internal int ChangingCount => Volatile.Read(ref _changingCount);
        internal int ChangedCount => Volatile.Read(ref _changedCount);

        public void ValidateCoordinatedParticipants() { }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changingCount);
            return Task.CompletedTask;
        }

        public Task PublishChangingAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changingCount);
            return Task.CompletedTask;
        }

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changedCount);
            return Task.CompletedTask;
        }

        public Task PublishChangedAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changedCount);
            return Task.CompletedTask;
        }

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            PackageStoreOperationOwner owner, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PublishReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CompletedMembershipFixture : IDisposable
    {
        internal TempDirectory Temp { get; } = new();
        internal IPhysicalStoreFileSystem Files { get; } = PackageStoreRuntimeAdmission.CreatePhysicalFileSystem();
        internal string PackageInstallRoot { get; private set; } = null!;
        internal string SharedInstallPath { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal Dictionary<string, string> StatePaths { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, StoreStateRecord> States { get; } = new(StringComparer.Ordinal);

        internal static async Task<CompletedMembershipFixture> CreateAsync()
        {
            var fixture = new CompletedMembershipFixture();
            try
            {
                fixture.PackageInstallRoot = fixture.Temp.CreateSubdirectory("packages");
                fixture.StatePaths.Add("first", Path.Combine(fixture.Temp.Path, "state-first", "state.json"));
                fixture.StatePaths.Add("second", Path.Combine(fixture.Temp.Path, "state-second", "state.json"));
                foreach (var path in fixture.StatePaths.Values)
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var root = OpenDirectory(fixture.Files, fixture.PackageInstallRoot);
                fixture.RootIdentity = new PhysicalRootIdentity(fixture.Files.InspectHandle(root).Identity);
                var serializer = new StoreStateSerializer();
                var registry = new RootMembershipRegistry(fixture.Files, serializer);
                var declarations = fixture.StatePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
                    new RootMemberRecord.DeclaredBinding())).ToArray();
                // Migration inputs are read while positively Unenrolled, before declaring authority.
                fixture.SharedInstallPath = fixture.Install("Shared.Dependency", "2.1.0", dependencyId: null);
                fixture.States.Add("first", await fixture.BuildProtectedStateAsync("first", "Root.First"));
                fixture.States.Add("second", await fixture.BuildProtectedStateAsync("second", "Root.Second"));
                registry.InitializeIncomplete(root, fixture.RootIdentity, 1, declarations, true, CancellationToken.None);

                var parents = fixture.StatePaths.ToDictionary(pair => pair.Key,
                    pair => OpenDirectory(fixture.Files, Path.GetDirectoryName(pair.Value)!), StringComparer.Ordinal);
                try
                {
                    await registry.BindDeclaredMembersAsync(root, fixture.RootIdentity, 1, declarations,
                        parents.ToDictionary(pair => pair.Key,
                            pair => (pair.Value, Path.GetFileName(fixture.StatePaths[pair.Key])), StringComparer.Ordinal),
                        true, CancellationToken.None);
                }
                finally
                {
                    foreach (var parent in parents.Values.Reverse())
                        parent.Dispose();
                }

                await registry.WithQuiescentBoundIncompleteMemberLocationsAsync(root, fixture.RootIdentity, 1, true,
                    async (locked, token) =>
                    {
                        foreach (var member in locked.Ledger.Members)
                            await locked.PublishStateAsync(member.MemberId, fixture.States[member.MemberId], token);
                        return true;
                    }, CancellationToken.None);
                await registry.CompleteEnrollmentAsync(root, fixture.RootIdentity, 1, true, CancellationToken.None);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal async Task<StoreStateRecord> ReadFreshStateAsync(string memberId)
        {
            var registry = new StoreRegistry(new StoreStateSerializer(), StatePaths[memberId]);
            return await registry.GetStateAsync(CancellationToken.None);
        }

        private async Task<StoreStateRecord> BuildProtectedStateAsync(string memberId, string rootPackageId)
        {
            var rootPath = Install(rootPackageId, "1.0.0", "Shared.Dependency");
            var request = new PackageRequest(rootPackageId, string.Empty, "test-feed", PackageUpdatePolicy.Range, memberId);
            var packages = new[]
            {
                new ResolvedPackage(rootPackageId, "1.0.0", "test-feed", rootPath, DateTimeOffset.UnixEpoch, memberId),
                new ResolvedPackage("Shared.Dependency", "2.1.0", "test-feed", SharedInstallPath,
                    DateTimeOffset.UnixEpoch, "dependency")
            };
            var graphResolver = new PackageDependencyGraphResolver(Substitute.For<IPackageResolver>(),
                Substitute.For<IReconciliationRetryPolicy>());
            var resolution = await graphResolver.ResolveAsync([request], (_, _) => Task.FromResult(packages[0]),
                (_, _) => Task.FromResult(packages[1]), CancellationToken.None);
            var graph = Assert.Single(resolution.ResolvedGraphs);
            var installs = new List<PackageInstallIdentity>();
            using var root = OpenDirectory(Files, PackageInstallRoot);
            foreach (var node in graph.Nodes)
            {
                var relativePath = Path.GetRelativePath(PackageInstallRoot, node.InstallPath!).Replace('\\', '/');
                using var observation = new PackageInstallIdentityReader(Files).Observe(
                    root, RootIdentity, relativePath, node.PackageId, node.Version);
                installs.Add(observation.InstallIdentity);
            }

            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(graph, [request], installs, 1);
            var versions = packages.ToDictionary(static package => package.Id, static package => package.Version,
                StringComparer.OrdinalIgnoreCase);
            var empty = StoreStateRecord.Empty();
            var changes = new PackageChangeSet(packages, [], [], memberId, DateTimeOffset.UnixEpoch);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(empty, versions, packages, changes,
                memberId, DateTimeOffset.UnixEpoch, [graph]);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(empty, [graph], versions,
                memberId, DateTimeOffset.UnixEpoch);
            var state = empty with
            {
                ActiveVersionById = versions,
                LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors,
                    StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords,
                    StringComparer.OrdinalIgnoreCase),
                UpdatedAt = DateTimeOffset.UnixEpoch
            };
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            var candidate = new PackageProtectionRecord(1, RootIdentity, 1, memberId, 1,
                ProtectionDigest.StateBody(state), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, RootIdentity, 1, memberId, 1,
                candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);
            return state with { ProtectionRecord = protection };
        }

        private string Install(string packageId, string version, string? dependencyId)
        {
            var path = Path.Combine(PackageInstallRoot, "test-feed", packageId, version);
            Directory.CreateDirectory(path);
            var dependencies = dependencyId is null ? string.Empty
                : $"<dependencies><dependency id=\"{dependencyId}\" version=\"2.0.0\" /></dependencies>";
            File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
                $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependencies}</metadata></package>");
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
            if (packageId == "Shared.Dependency")
                File.WriteAllText(Path.Combine(path, "sentinel.txt"), "shared sentinel");
            return path;
        }

        public void Dispose() => Temp.Dispose();
    }

    private static PhysicalStoreDirectoryHandle OpenDirectory(IPhysicalStoreFileSystem files, string ownedPath)
    {
        var path = OperatingSystem.IsWindows() ? Path.GetFullPath(ownedPath) : RealPath(ownedPath);
        var anchor = OperatingSystem.IsWindows() ? Path.GetPathRoot(path)! : "/";
        var current = files.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in path[anchor.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = files.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static string RealPath(string path)
    {
        var pointer = OperatingSystem.IsMacOS() ? DarwinRealPath(path, IntPtr.Zero) : UnixRealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            throw new IOException($"Temporary fixture realpath failed ({Marshal.GetLastPInvokeError()}).");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Temporary fixture realpath returned no path."); }
        finally
        {
            if (OperatingSystem.IsMacOS()) DarwinFree(pointer);
            else UnixFree(pointer);
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr DarwinRealPath(string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr buffer);
    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    private static extern void DarwinFree(IntPtr pointer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);
}
