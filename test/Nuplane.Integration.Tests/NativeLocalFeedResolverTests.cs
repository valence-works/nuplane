using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Versioning;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task Reconciliation_ResolvesNativeLocalFeedUnderBorrowedRoot_AndActivatesExactInstalledGraph()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(fixture.RootPath, "native-local-feed");
        Directory.CreateDirectory(localFeedPath);
        var packageBytes = CreatePackageArchive("Root.Second", "3.0.0", "Shared.Dependency", "2.1.0");
        var packagePath = Path.Combine(localFeedPath, "rOoT.SeCoNd.3.0.NUPKG");
        await File.WriteAllBytesAsync(packagePath, packageBytes);
        var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(packageBytes));
        var request = new PackageRequest("ROOT.SECOND", "[2.0.0,4.0.0)", "feed",
            PackageUpdatePolicy.Range, "native-local-feed-test");
        var localFeed = new FeedDefinition("local-cache", new Uri(localFeedPath + Path.DirectorySeparatorChar));
        var remoteFeed = new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json"));

        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: remoteFeed, additionalFeeds: [localFeed], participant: true);

        Assert.True(provider.GetRequiredService<PackageStoreCompositionSnapshot>().IsParticipant);
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var resolver = Assert.IsType<MultiFeedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        Assert.IsAssignableFrom<IScopedPackageResolver>(resolver);

        var lockPath = Path.Combine(Path.GetDirectoryName(fixture.StatePaths["member-b"])!, "nuplane.lock.json");
        var lockStore = new LockFileStore(Microsoft.Extensions.Options.Options.Create(new LockFileOptions { Path = lockPath }));
        await lockStore.WriteAsync(new PackageLockFile("2.0", DateTimeOffset.UtcNow,
            [new PackageLockEntry("Root.Second", "3.0.0", "local-cache", expectedHash, DateTimeOffset.UtcNow)]),
            CancellationToken.None);

        try
        {
            var cycle = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("native-local-feed"), CancellationToken.None);

            Assert.False(cycle.IsDegraded);
            var updated = Assert.Single(cycle.ChangeSet.Updated);
            Assert.Equal("ROOT.SECOND", updated.Id);
            Assert.Equal("3.0.0", updated.Version);
            var installPath = PackageInstallStore.GetInstallDirectory(fixture.RootPath,
                "local-cache", "rOoT.SeCoNd", "3.0.0");
            var expectedAssemblyPath = Path.Combine(installPath, "lib", "net10.0", "Root.Second.dll");
            string generationId;

            await using (var verificationOperation = await admission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Reconciliation))
            {
                using var borrow = verificationOperation.Owner!.Borrow();
                var stateRegistry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
                var state = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
                Assert.Equal("3.0.0", state.ActiveVersionById["Root.Second"]);
                Assert.Equal("3.0.0", state.LastKnownGoodById["Root.Second"]);
                Assert.True(PackageInstallStore.IsInstalled(installPath, borrow));
                Assert.Equal(expectedHash,
                    await PackageInstallStore.ReadContentHashAsync(installPath, borrow, CancellationToken.None));

                var graph = Assert.Single(state.ActiveGraphsByIdNormalized.Values);
                Assert.Equal(GraphActivationStatus.Active, graph.Status);
                Assert.Equal("3.0.0", graph.NodeVersionsByPackageId!
                    .Single(pair => string.Equals(pair.Key, "Root.Second", StringComparison.OrdinalIgnoreCase)).Value);
                Assert.Equal("2.1.0", graph.NodeVersionsByPackageId!
                    .Single(pair => string.Equals(pair.Key, "Shared.Dependency", StringComparison.OrdinalIgnoreCase)).Value);
                Assert.Equal(new HashSet<string>(["Root.Second", "Shared.Dependency"], StringComparer.OrdinalIgnoreCase),
                    graph.NodePackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
                generationId = graph.GenerationId;

                var assemblyContext = Assert.Single(AssemblyLoadContext.All, context => context.IsCollectible &&
                    context.Assemblies.Any(assembly => PathEquals(assembly.Location, expectedAssemblyPath)));
                Assert.Contains(assemblyContext.Assemblies, assembly =>
                    assembly.GetName().Name == "Root.Second" && PathEquals(assembly.Location, expectedAssemblyPath));
            }

            var graphUseRecords = await InspectGraphUseRecordsAsync(admission, fixture);
            Assert.Contains(graphUseRecords.Entries, entry => entry.OwnershipState == GraphUseRecordOwnershipState.Live &&
                entry.Record.GraphSnapshot.GenerationId == generationId);
        }
        finally
        {
            provider.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await fixture.WaitForGraphUseReleaseAsync(admission);
        }
    }

    [Fact]
    public async Task ScopedResolver_ResolvesExactNormalizedMixedCaseFromExternalUnenrolledLocalFeed()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(Path.GetTempPath(), "nuplane-native-local-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localFeedPath);
        try
        {
            var packageBytes = CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0");
            await File.WriteAllBytesAsync(Path.Combine(localFeedPath, "rOoT.SeCoNd.1.0.NUPKG"), packageBytes);
            var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(packageBytes));
            var request = new PackageRequest("root.second", "1.0", "local", PackageUpdatePolicy.Exact,
                "native-external-local-feed-test");
            var localFeed = new FeedDefinition("local", new Uri(localFeedPath + Path.DirectorySeparatorChar));
            await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
                remoteFeed: localFeed, participant: true);
            var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
            var admission = provider.GetRequiredService<IPackageStoreAdmission>();

            await using var operation = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation);
            using var borrow = operation.Owner!.Borrow();
            var resolved = await resolver.ResolveAsync(request, borrow, CancellationToken.None);

            Assert.Equal("1.0.0", resolved.Version);
            Assert.Equal("local", resolved.FeedName);
            Assert.Equal(expectedHash, resolved.PackageContentHash);
            Assert.Equal(PackageInstallStore.GetInstallDirectory(fixture.RootPath,
                "local", "rOoT.SeCoNd", "1.0.0"), resolved.InstallPath);
            Assert.True(PackageInstallStore.IsInstalled(resolved.InstallPath, borrow));
            Assert.Equal(expectedHash,
                await PackageInstallStore.ReadContentHashAsync(resolved.InstallPath, borrow, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(localFeedPath, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedResolver_RefusesLocalCandidateReplacedAfterSelection_BeforeStaging()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(fixture.RootPath, "replacement-local-feed");
        Directory.CreateDirectory(localFeedPath);
        var candidatePath = Path.Combine(localFeedPath, "Root.Second.1.0.0.nupkg");
        var replacementPath = Path.Combine(localFeedPath, "replacement.tmp");
        var packageBytes = CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0");
        await File.WriteAllBytesAsync(candidatePath, packageBytes);
        await File.WriteAllBytesAsync(replacementPath, packageBytes);
        var request = new PackageRequest("Root.Second", "[1.0.0,2.0.0)", "local",
            PackageUpdatePolicy.Range, "native-local-replacement-test");
        var localFeed = new FeedDefinition("local", new Uri(localFeedPath + Path.DirectorySeparatorChar));
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: localFeed, participant: true,
            configureServices: services => services.AddSingleton<IVersionRangeEvaluator>(
                new ReplacingVersionRangeEvaluator(candidatePath, replacementPath)));
        var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        _ = await ResolveExpectedRefusalAndDrainAsync(
            admission, resolver, request, PackageStoreAdmissionReason.UnknownAuthority);

        Assert.False(Directory.Exists(
            PackageInstallStore.GetInstallDirectory(fixture.RootPath, "local", "Root.Second", "1.0.0")));
        Assert.True(File.Exists(candidatePath));
        Assert.False(File.Exists(replacementPath));
    }

    [Fact]
    public async Task ScopedResolver_RefusesMatchingDirectoryCandidateBeforeInstallStaging()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(fixture.RootPath, "directory-candidate-feed");
        Directory.CreateDirectory(Path.Combine(localFeedPath, "Root.Second.1.0.0.nupkg"));
        var request = new PackageRequest("Root.Second", "1.0.0", "local", PackageUpdatePolicy.Exact,
            "native-local-directory-candidate-test");
        var localFeed = new FeedDefinition("local", new Uri(localFeedPath + Path.DirectorySeparatorChar));
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: localFeed, participant: true);
        var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        _ = await ResolveExpectedRefusalAndDrainAsync(
            admission, resolver, request, PackageStoreAdmissionReason.UnknownAuthority);
        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "local", "Root.Second")));
    }

    [Fact]
    public async Task ScopedResolver_RefusesFeedUnderAnotherCompleteRootBeforeCandidateSampling()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var foreignFixture = await GraphUseFixture.CreateAsync();
        var foreignFeedPath = Path.Combine(foreignFixture.RootPath, "foreign-local-feed");
        Directory.CreateDirectory(foreignFeedPath);
        await File.WriteAllBytesAsync(Path.Combine(foreignFeedPath, "Root.Second.1.0.0.nupkg"),
            CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0"));
        var request = new PackageRequest("Root.Second", "1.0.0", "foreign", PackageUpdatePolicy.Exact,
            "native-local-foreign-root-test");
        var foreignFeed = new FeedDefinition("foreign", new Uri(foreignFeedPath + Path.DirectorySeparatorChar));
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: foreignFeed, participant: true);
        var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        var refusal = await ResolveExpectedRefusalAndDrainAsync(
            admission, resolver, request, PackageStoreAdmissionReason.RootMismatch);
        Assert.Equal(fixture.RootIdentity, refusal.Root);
        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "foreign", "Root.Second")));
    }

    [Fact]
    public async Task ScopedResolver_RefusesIncompleteForeignLocalFeedBeforeFallbackOrStaging()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var incompleteFixture = new Nuplane.Tests.Shared.PackageStoreFixture();
        IPhysicalStoreFileSystem foreignFiles = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        using var foreignRoot = Nuplane.Tests.Shared.OwnedProcessDirectory.Open(
            foreignFiles, incompleteFixture.PackageInstallRoot);
        var foreignRootIdentity = new PhysicalRootIdentity(foreignFiles.InspectHandle(foreignRoot).Identity);
        var foreignRegistry = new RootMembershipRegistry(foreignFiles, new StoreStateSerializer());
        foreignRegistry.InitializeIncomplete(foreignRoot, foreignRootIdentity, 1,
            [new RootMemberRecord("foreign-member", incompleteFixture.StateFilePath,
                new RootMemberRecord.DeclaredBinding())],
            quiescentCutoverConfirmed: true, CancellationToken.None);
        var foreignFeedPath = incompleteFixture.CreateDirectory("packages/incomplete-local-feed");
        await File.WriteAllBytesAsync(Path.Combine(foreignFeedPath, "Root.Second.1.0.0.nupkg"),
            CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0"));

        var remoteAcquirer = new CountingScopedRemotePackageAcquirer();
        var request = new PackageRequest("Root.Second", "1.0.0", "feed", PackageUpdatePolicy.Exact,
            "native-local-incomplete-authority-test");
        var incompleteFeed = new FeedDefinition("local-cache", new Uri(foreignFeedPath + Path.DirectorySeparatorChar));
        var remoteFeed = new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json"));
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: remoteFeed, additionalFeeds: [incompleteFeed], participant: true,
            configureServices: services =>
            {
                services.RemoveAll<IRemotePackageAcquirer>();
                services.AddSingleton<IRemotePackageAcquirer>(remoteAcquirer);
            });
        var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        _ = await ResolveExpectedRefusalAndDrainAsync(
            admission, resolver, request, PackageStoreAdmissionReason.RootMismatch);
        Assert.Equal(0, remoteAcquirer.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "local-cache", "Root.Second")));
    }

    [Fact]
    public async Task ScopedResolver_RefusesFeedReachedThroughDirectoryAliasBeforeFallbackOrStaging()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var externalFeedPath = Path.Combine(Path.GetTempPath(), "nuplane-native-local-feed-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalFeedPath);
        var aliasPath = Path.Combine(fixture.RootPath, "aliased-local-feed");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(externalFeedPath, "Root.Second.1.0.0.nupkg"),
                CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0"));
            Directory.CreateSymbolicLink(aliasPath, externalFeedPath);

            var remoteAcquirer = new CountingScopedRemotePackageAcquirer();
            var request = new PackageRequest("Root.Second", "1.0.0", "feed", PackageUpdatePolicy.Exact,
                "native-local-alias-authority-test");
            var aliasFeed = new FeedDefinition("local-cache", new Uri(aliasPath + Path.DirectorySeparatorChar));
            var remoteFeed = new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json"));
            await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
                remoteFeed: remoteFeed, additionalFeeds: [aliasFeed], participant: true,
                configureServices: services =>
                {
                    services.RemoveAll<IRemotePackageAcquirer>();
                    services.AddSingleton<IRemotePackageAcquirer>(remoteAcquirer);
                });
            var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
            var admission = provider.GetRequiredService<IPackageStoreAdmission>();

            _ = await ResolveExpectedRefusalAndDrainAsync(
                admission, resolver, request, PackageStoreAdmissionReason.UnknownAuthority);
            Assert.Equal(0, remoteAcquirer.CallCount);
            Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "local-cache", "Root.Second")));
        }
        finally
        {
            Directory.Delete(externalFeedPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("symbolic-link")]
    [InlineData("hard-link")]
    public async Task ScopedResolver_RefusesLinkedArchiveCandidateBeforeFallbackOrStaging(string linkKind)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(fixture.RootPath, "linked-local-feed");
        Directory.CreateDirectory(localFeedPath);
        var targetPath = Path.Combine(localFeedPath, "Root.Second.1.0.0.payload");
        var candidatePath = Path.Combine(localFeedPath, "Root.Second.1.0.0.nupkg");
        await File.WriteAllBytesAsync(targetPath,
            CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0"));
        if (linkKind == "symbolic-link")
            File.CreateSymbolicLink(candidatePath, targetPath);
        else
            CreateHardLink(targetPath, candidatePath);

        var remoteAcquirer = new CountingScopedRemotePackageAcquirer();
        var request = new PackageRequest("Root.Second", "1.0.0", "feed", PackageUpdatePolicy.Exact,
            "native-local-linked-candidate-test");
        var localFeed = new FeedDefinition("local-cache", new Uri(localFeedPath + Path.DirectorySeparatorChar));
        var remoteFeed = new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json"));
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request],
            remoteFeed: remoteFeed, additionalFeeds: [localFeed], participant: true,
            configureServices: services =>
            {
                services.RemoveAll<IRemotePackageAcquirer>();
                services.AddSingleton<IRemotePackageAcquirer>(remoteAcquirer);
            });
        var resolver = Assert.IsAssignableFrom<IScopedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        _ = await ResolveExpectedRefusalAndDrainAsync(
            admission, resolver, request, PackageStoreAdmissionReason.UnknownAuthority);
        Assert.Equal(0, remoteAcquirer.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "local-cache", "Root.Second")));
    }

    private static async Task<PackageStoreAdmissionException> ResolveExpectedRefusalAndDrainAsync(
        IPackageStoreAdmission admission,
        IScopedPackageResolver resolver,
        PackageRequest request,
        PackageStoreAdmissionReason expectedReason)
    {
        var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation);
        var borrow = operation.Owner!.Borrow();
        PackageStoreAdmissionException? refusal = null;
        try
        {
            refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => resolver.ResolveAsync(request, borrow, CancellationToken.None));
            Assert.Equal(expectedReason, refusal.Reason);
        }
        finally
        {
            borrow.Dispose();
            try
            {
                await operation.DisposeAsync();
            }
            catch (PackageStoreAdmissionException closeFailure)
            {
                // A refused owner-aware callback may be replayed as its close result as well.
                Assert.Equal(expectedReason, closeFailure.Reason);
            }
        }

        return refusal!;
    }

    [Fact]
    public async Task ScopedResolver_CancellationDuringNativeLocalArchiveCopy_DrainsBorrowForNextOwner()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var localFeedPath = Path.Combine(fixture.RootPath, "cancellation-local-feed");
        Directory.CreateDirectory(localFeedPath);
        await File.WriteAllBytesAsync(Path.Combine(localFeedPath, "Root.Second.1.0.0.nupkg"),
            CreatePackageArchive("Root.Second", "1.0.0", "Shared.Dependency", "2.1.0"));
        var request = new PackageRequest("Root.Second", "1.0.0", "local", PackageUpdatePolicy.Exact,
            "native-local-cancellation-test");
        await using var provider = fixture.CreateProvider("member-b", participant: true);
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        using var cancellation = new CancellationTokenSource();

        var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation);
        var borrow = operation.Owner!.Borrow();
        Task<bool>? copy = null;
        Task? closing = null;
        var closeObserved = false;
        try
        {
            var selected = Assert.Single(await NativeLocalDirectoryFeedIndex.FindPackageFilesAsync(
                localFeedPath, request.Id, borrow, CancellationToken.None));
            await using var destination = new BlockingWriteStream();
            copy = PackageStoreOperationAccess.WithValidatedRootAsync(
                borrow,
                async (files, heldRoot, token) =>
                {
                    await NativeLocalDirectoryFeedIndex.CopySelectedPackageArchiveAsync(
                        localFeedPath, selected, borrow, files, heldRoot, destination, token);
                    return true;
                },
                cancellation.Token);

            await destination.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            closing = operation.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
            borrow.Dispose();

            try
            {
                await closing;
            }
            catch (OperationCanceledException)
            {
                // The canceled callback is replayed before the native owner is released.
            }
            closeObserved = true;
        }
        finally
        {
            cancellation.Cancel();
            if (copy is not null)
            {
                try
                {
                    await copy;
                }
                catch (OperationCanceledException)
                {
                    // Release an interrupted copy before releasing its borrow.
                }
            }
            borrow.Dispose();
            if (closing is null)
                await operation.DisposeAsync();
            else if (!closeObserved)
            {
                try
                {
                    await closing;
                }
                catch (OperationCanceledException)
                {
                    // The canceled callback is replayed before the native owner is released.
                }
            }
        }

        await using var nextOperation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation);
        using var nextBorrow = nextOperation.Owner!.Borrow();
        Assert.NotNull(nextBorrow.Root);
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource<bool> _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => _writeStarted.Task;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult(true);
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class ReplacingVersionRangeEvaluator(string candidatePath, string replacementPath)
        : IVersionRangeEvaluator
    {
        private readonly NuGetVersionRangeEvaluator _inner = new();
        private int _replaced;

        public VersionResolutionResult SelectBestMatch(string versionRange, IReadOnlyList<string> availableVersions)
        {
            var result = _inner.SelectBestMatch(versionRange, availableVersions);
            if (result.Success && Interlocked.Exchange(ref _replaced, 1) == 0)
                File.Move(replacementPath, candidatePath, overwrite: true);
            return result;
        }

        public bool IsValidRange(string versionRange) => _inner.IsValidRange(versionRange);
    }

    private sealed class CountingScopedRemotePackageAcquirer : IScopedRemotePackageAcquirer
    {
        internal int CallCount => Volatile.Read(ref _callCount);
        private int _callCount;

        public Task<string> AcquireAsync(
            FeedDefinition feed,
            string packageId,
            string version,
            CancellationToken cancellationToken)
            => Task.FromException<string>(new Xunit.Sdk.XunitException("The unscoped remote acquirer must not be called."));

        public Task<string> AcquireAsync(
            FeedDefinition feed,
            string packageId,
            string version,
            PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromException<string>(new Xunit.Sdk.XunitException("The remote fallback acquirer must not be called."));
        }
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero))
                throw new IOException($"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
        }
        else if ((OperatingSystem.IsMacOS()
                     ? CreateHardLinkDarwin(existingPath, newPath)
                     : CreateHardLinkLinux(existingPath, newPath)) != 0)
        {
            throw new IOException($"link failed with {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkDarwin(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkLinux(string existingPath, string newPath);

}
