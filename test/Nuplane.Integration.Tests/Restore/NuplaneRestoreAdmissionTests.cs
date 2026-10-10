using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Sources;
using Nuplane.Sources.Directory;
using Nuplane.Store.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests.Restore;

[Trait("Platform", "Native")]
public sealed class NuplaneRestoreAdmissionTests
{
    [Fact]
    public async Task DescribeDesiredAsync_CompleteRootOwnerCoversAwaitedScopedSourceRead()
    {
        using var root = await CreateCompleteRootAsync();
        var source = new GatedScopedSource(root.Initial.RootIdentity, root.Initial.EnrollmentEpoch);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));
        var competitor = CreateAdmission(root);

        var describe = NuplaneRestore.DescribeDesiredAsync(Configuration(), options);
        await source.WaitUntilStartedAsync();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance));

        source.Release();
        var result = await describe;
        Assert.Equal("Scoped.Package", Assert.Single(result.Requests).PackageId);

        await using var afterDrain = await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
    }

    [Fact]
    public async Task RestoreAsync_RequirePinnedVersionsPreflightUsesConfiguredRootAdmission()
    {
        using var root = await CreateCompleteRootAsync();
        var source = new CountingPathIndependentSource(
            "pinned-preflight",
            [new("Range.Package", "[1.0.0,2.0.0)", "local", PackageUpdatePolicy.Range, "preflight")]);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));
        options.RequirePinnedVersions = true;

        var result = await NuplaneRestore.RestoreAsync(Configuration(), options);

        Assert.True(result.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.UnpinnedRequests, result.SkipReason);
        Assert.Equal(1, source.ReadCount);
        Assert.Empty(result.ActivePackages);
    }

    [Fact]
    public async Task DescribeDesiredAsync_IncompleteRootRefusesBeforeFirstSourceRead()
    {
        using var root = await CreateRootAsync(complete: false);
        var source = new CountingPathIndependentSource("must-not-run", [Request("Never.Read")]);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));

        var exception = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), options));

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, exception.Reason);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task DescribeDesiredAsync_UnsupportedSourceRefusesBeforeAnySourceCallback()
    {
        using var root = await CreateCompleteRootAsync();
        var unsupported = new CountingUnclassifiedSource();
        var supported = new CountingPathIndependentSource("also-must-not-run", [Request("Never.Read")]);
        var options = Options(root, (builder, _) =>
        {
            builder.Services.AddSingleton<IDesiredPackageSource>(unsupported);
            builder.Services.AddSingleton<IDesiredPackageSource>(supported);
        });

        var exception = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), options));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, exception.Reason);
        Assert.Equal(0, unsupported.ReadCount);
        Assert.Equal(0, supported.ReadCount);
    }

    [Fact]
    public async Task DescribeDesiredAsync_RealDirectorySourceReadsUnderCompleteRootOwnerAcrossAwaitedProbe()
    {
        using var root = await CreateCompleteRootAsync();
        var feedDirectory = Path.Combine(root.RootPath, "directory-source-probe");
        Directory.CreateDirectory(feedDirectory);
        File.WriteAllBytes(Path.Combine(feedDirectory, "Probe.Package.1.0.0.nupkg"), [0x50, 0x4B, 0x03, 0x04]);

        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: async (_, cancellationToken) =>
            {
                probeStarted.TrySetResult();
                await releaseProbe.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            });
        var source = new DirectoryNupkgDesiredSource(
            "directory-under-store",
            feedDirectory,
            ["*"],
            stabilityProbe: probe);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));
        var competitor = CreateAdmission(root);

        var describe = NuplaneRestore.DescribeDesiredAsync(Configuration(), options);
        try
        {
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance));
        }
        finally
        {
            releaseProbe.TrySetResult();
        }

        var description = await describe;
        var request = Assert.Single(description.Requests);
        Assert.Equal("Probe.Package", request.PackageId);
        Assert.Equal("1.0.0", request.PinnedVersion);

        await using var afterDrain = await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
    }

    [Fact]
    public async Task DescribeDesiredAsync_UnenrolledRootPreservesRealDirectorySourceBehavior()
    {
        using var store = new PackageStoreFixture();
        var feedDirectory = store.CreateDirectory("directory-feed");
        File.WriteAllBytes(Path.Combine(feedDirectory, "Compat.Package.1.2.3.nupkg"), []);
        var source = new DirectoryNupkgDesiredSource("directory-compat", feedDirectory, ["*"]);
        var options = new NuplaneRestoreOptions
        {
            InstallRoot = store.PackageInstallRoot,
            StateFilePath = store.StateFilePath,
            LockFilePath = Path.Combine(Path.GetDirectoryName(store.StateFilePath)!, "restore.lock.json"),
            ConfigureBuilder = (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source)
        };

        var description = await NuplaneRestore.DescribeDesiredAsync(Configuration(), options);

        var request = Assert.Single(description.Requests);
        Assert.Equal("Compat.Package", request.PackageId);
        Assert.Equal("1.2.3", request.PinnedVersion);
    }

    [Fact]
    public async Task DescribeDesiredAsync_RealDirectorySourceRefusesForeignCompleteRootBeforeProbe()
    {
        using var root = await CreateCompleteRootAsync();
        using var foreignRoot = await CreateCompleteRootAsync();
        var feedDirectory = Path.Combine(foreignRoot.RootPath, "foreign-directory-source");
        Directory.CreateDirectory(feedDirectory);
        File.WriteAllBytes(Path.Combine(feedDirectory, "Foreign.Package.1.0.0.nupkg"), [0x50, 0x4B, 0x03, 0x04]);

        var callbacks = 0;
        var probe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: (_, _) =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.CompletedTask;
            });
        var source = new DirectoryNupkgDesiredSource(
            "foreign-directory",
            feedDirectory,
            ["*"],
            stabilityProbe: probe);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), options));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task DescribeDesiredAsync_UnscopedCustomAggregatorRefusesBeforeAnyCallback()
    {
        using var root = await CreateCompleteRootAsync();
        var source = new CountingPathIndependentSource("must-not-run", [Request("Never.Read")]);
        var aggregator = new UnscopedAggregator();
        var options = Options(root, (builder, _) =>
        {
            builder.Services.AddSingleton<IDesiredPackageSource>(source);
            builder.Services.RemoveAll<IDesiredStateAggregator>();
            builder.Services.AddSingleton<IDesiredStateAggregator>(aggregator);
        });

        var exception = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), options));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, exception.Reason);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(0, aggregator.CallCount);
    }

    [Fact]
    public async Task DescribeDesiredAsync_ScopedAdmissionRefusalPropagatesButOrdinarySourceErrorIsReported()
    {
        using var root = await CreateCompleteRootAsync();
        var refusal = new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority, "scope refused");
        var refusedSource = new FaultingScopedSource(refusal);
        var refusedOptions = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(refusedSource));

        var propagated = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), refusedOptions));

        Assert.Same(refusal, propagated);
        Assert.Equal(1, refusedSource.ReadCount);

        var ordinary = new FaultingScopedSource(new IOException("directory temporarily unavailable"));
        var ordinaryOptions = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(ordinary));
        var description = await NuplaneRestore.DescribeDesiredAsync(Configuration(), ordinaryOptions);

        Assert.Empty(description.Requests);
        Assert.Equal("directory temporarily unavailable",
            Assert.Single(description.SourceErrors).Value);
        Assert.Equal(typeof(FaultingScopedSource).FullName, Assert.Single(description.SourceErrors).Key);
    }

    [Fact]
    public async Task DescribeDesiredAsync_AdmissionRefusalReturnedInCustomScopedErrorsIsNotProjectedToText()
    {
        using var root = await CreateCompleteRootAsync();
        var source = new CountingPathIndependentSource("custom-scoped", [Request("Never.Read")]);
        var refusal = new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch, "member changed");
        var aggregator = new RefusalReturningScopedAggregator(refusal);
        var options = Options(root, (builder, _) =>
        {
            builder.Services.AddSingleton<IDesiredPackageSource>(source);
            builder.Services.RemoveAll<IDesiredStateAggregator>();
            builder.Services.AddSingleton<IDesiredStateAggregator>(aggregator);
        });

        var propagated = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => NuplaneRestore.DescribeDesiredAsync(Configuration(), options));

        Assert.Same(refusal, propagated);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(1, aggregator.ScopedCallCount);
    }

    [Fact]
    public async Task DescribeDesiredAsync_PreservesOriginalSourceTypeAndToStringOrdering()
    {
        using var root = await CreateCompleteRootAsync();
        var observedOrder = new List<string>();
        var zSource = new OrderedPathIndependentSource("zulu", observedOrder, [Request("Zulu.Package", "zulu")]);
        var aSource = new OrderedPathIndependentSource("alpha", observedOrder, [Request("Alpha.Package", "alpha")]);
        var options = Options(root, (builder, _) =>
        {
            // Registration order is deliberately the reverse of the required same-type ToString order.
            builder.Services.AddSingleton<IDesiredPackageSource>(zSource);
            builder.Services.AddSingleton<IDesiredPackageSource>(aSource);
        });

        var description = await NuplaneRestore.DescribeDesiredAsync(Configuration(), options);

        Assert.Equal(["alpha", "zulu"], observedOrder);
        Assert.Equal(["Alpha.Package", "Zulu.Package"], description.Requests.Select(static item => item.PackageId));
        Assert.Equal(["alpha", "zulu"], description.Requests.Select(static item => item.SourceName));
    }

    [Fact]
    public async Task DescribeDesiredAsync_CancellationDrainsScopedBorrowAndConfiguredRootOwner()
    {
        using var root = await CreateCompleteRootAsync();
        var source = new GatedScopedSource(root.Initial.RootIdentity, root.Initial.EnrollmentEpoch);
        var options = Options(root, (builder, _) => builder.Services.AddSingleton<IDesiredPackageSource>(source));
        var competitor = CreateAdmission(root);
        using var cancellation = new CancellationTokenSource();

        var describe = NuplaneRestore.DescribeDesiredAsync(Configuration(), options, cancellation.Token);
        await source.WaitUntilStartedAsync();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describe);
        await using var afterDrain = await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
    }

    private static async Task<RootMembershipProcessFixture> CreateRootAsync(bool complete)
    {
        var fixture = await RootMembershipProcessFixture.CreateAsync(RootMembershipPriorShape.Acknowledged);
        if (!complete)
            return fixture;

        try
        {
            await fixture.Registry.CompleteEnrollmentAsync(fixture.Root, fixture.Initial.RootIdentity,
                fixture.Initial.EnrollmentEpoch, quiescentCutoverConfirmed: true, cancellationToken: CancellationToken.None);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static Task<RootMembershipProcessFixture> CreateCompleteRootAsync() => CreateRootAsync(complete: true);

    private static PackageStoreAdmission CreateAdmission(RootMembershipProcessFixture root) =>
        new(root.Files, root.Registry, root.RootPath);

    private static NuplaneRestoreOptions Options(
        RootMembershipProcessFixture root,
        Action<NuplaneBuilder, IConfiguration> configureBuilder) => new()
    {
        InstallRoot = root.RootPath,
        StateFilePath = root.StateFilePath,
        LockFilePath = Path.Combine(Path.GetDirectoryName(root.StateFilePath)!, "restore.lock.json"),
        ConfigureBuilder = configureBuilder
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nuplane:Reconciliation:PollInterval"] = "00:05:00"
        })
        .Build();

    private static PackageRequest Request(string packageId, string sourceName = "test") =>
        new(packageId, "1.0.0", "local", PackageUpdatePolicy.Exact, sourceName);

    private sealed class GatedScopedSource(PhysicalRootIdentity expectedRoot, long expectedEpoch) : IScopedDesiredPackageSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        public void Release() => _release.TrySetResult();

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(
                new InvalidOperationException("The scoped callback must be selected for an enrolled source."));

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow, CancellationToken ct)
        {
            Assert.Equal(expectedRoot, borrow.Root);
            Assert.Equal(expectedEpoch, borrow.Epoch);
            _started.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return [Request("Scoped.Package")];
        }
    }

    private sealed class CountingPathIndependentSource(
        string name,
        IReadOnlyList<PackageRequest> requests) : IPackagePathIndependentDesiredPackageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override string ToString() => name;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(requests);
        }
    }

    private sealed class CountingUnclassifiedSource : IDesiredPackageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult<IReadOnlyList<PackageRequest>>([Request("Unsupported.Read")]);
        }
    }

    private sealed class FaultingScopedSource(Exception exception) : IScopedDesiredPackageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException("Unscoped path was called."));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromException<IReadOnlyList<PackageRequest>>(exception);
        }
    }

    private sealed class OrderedPathIndependentSource(
        string name,
        ICollection<string> observedOrder,
        IReadOnlyList<PackageRequest> requests) : IPackagePathIndependentDesiredPackageSource
    {
        public override string ToString() => name;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            observedOrder.Add(name);
            return Task.FromResult(requests);
        }
    }

    private sealed class UnscopedAggregator : IDesiredStateAggregator
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DesiredAggregateResult> AggregateAsync(IEnumerable<IDesiredPackageSource> sources, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new DesiredAggregateResult([], new Dictionary<string, Exception>()));
        }
    }

    private sealed class RefusalReturningScopedAggregator(PackageStoreAdmissionException refusal)
        : IScopedDesiredStateAggregator
    {
        private int _scopedCallCount;

        public int ScopedCallCount => Volatile.Read(ref _scopedCallCount);

        public Task<DesiredAggregateResult> AggregateAsync(IEnumerable<IDesiredPackageSource> sources, CancellationToken ct) =>
            Task.FromException<DesiredAggregateResult>(new InvalidOperationException("The unscoped aggregator was called."));

        public Task<DesiredAggregateResult> AggregateAsync(
            IEnumerable<IDesiredPackageSource> sources,
            PackageStoreOperationOwner owner,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _scopedCallCount);
            return Task.FromResult(new DesiredAggregateResult([], new Dictionary<string, Exception>
            {
                [typeof(RefusalReturningScopedAggregator).FullName!] = refusal
            }));
        }
    }
}
