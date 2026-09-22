using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Versioning;
using Nuplane.Loading;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Restore;
using Nuplane.Sources;
using Nuplane.Sources.Directory.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Restore;

/// <summary>
/// End-to-end tests for the host-free restore entry point. Every one of them runs against a local
/// directory feed and temporary directories: nothing here touches a network.
/// </summary>
public sealed class NuplaneRestoreTests : IDisposable
{
    private const string FeedName = "local-drop";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuplane-host-free-restore", Guid.NewGuid().ToString("N"));
    private readonly string _feedDirectory;
    private readonly string _installRoot;
    private readonly string _stateFilePath;
    private readonly string _lockFilePath;

    public NuplaneRestoreTests()
    {
        _feedDirectory = Path.Combine(_root, "drop");
        _installRoot = Path.Combine(_root, "packages");
        _stateFilePath = Path.Combine(_root, "state", "store-state.json");
        _lockFilePath = Path.Combine(_root, "nuplane.lock.json");
        Directory.CreateDirectory(_feedDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_WithADirectoryFeed_InstallsUnderTheOverriddenInstallRoot()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var result = await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.Equal(_installRoot, result.InstallRoot);
        Assert.True(File.Exists(Path.Combine(package.InstallDirectory(_installRoot, FeedName), ".nuplane-ready")));
    }

    [Fact]
    public async Task RestoreAsync_WithADirectoryFeed_WritesTheStateFileAtTheOverriddenPath()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var result = await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.Equal(_stateFilePath, result.StateFilePath);
        Assert.True(File.Exists(_stateFilePath));
        var active = Assert.Single(result.ActivePackages);
        Assert.Equal(package.PackageId, active.PackageId);
        Assert.Equal(package.Version, active.Version);
    }

    [Fact]
    public async Task RestoreAsync_WithADirectoryFeed_ReportsTheActiveSetTheStoreItWroteNowHolds()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var result = await NuplaneRestore.RestoreAsync(configuration, Options());

        var readBack = await NuplaneStore.ReadActivePackagesAsync(result.StateFilePath);
        Assert.Equal(
            readBack.Select(static package => package.PackageId),
            result.ActivePackages.Select(static package => package.PackageId));
    }

    [Fact]
    public async Task RestoreAsync_WithADirectoryFeed_LoadsNoAssembly()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        var contextsBefore = AssemblyLoadContext.All.Count();

        await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.False(HostFreeRestoreTestSupport.IsAssemblyVisible(package));
        Assert.Equal(contextsBefore, AssemblyLoadContext.All.Count());
    }

    [Fact]
    public async Task RestoreAsync_CalledTwiceOverAnUnchangedConfiguration_IsIdempotent()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        var first = await NuplaneRestore.RestoreAsync(configuration, Options());
        var installedAt = Directory.GetCreationTimeUtc(package.InstallDirectory(_installRoot, FeedName));

        var second = await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.False(second.IsDegraded);
        Assert.Empty(second.FailedPackages);
        Assert.Equal(
            first.ActivePackages.Select(static active => (active.PackageId, active.Version)),
            second.ActivePackages.Select(static active => (active.PackageId, active.Version)));
        Assert.Equal(installedAt, Directory.GetCreationTimeUtc(package.InstallDirectory(_installRoot, FeedName)));
    }

    [Fact]
    public async Task RestoreAsync_WhenAPackageLeavesTheDesiredSet_LeavesItsInstalledFilesOnDisk()
    {
        var kept = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var dropped = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        await NuplaneRestore.RestoreAsync(configuration, Options());
        File.Delete(Path.Combine(_feedDirectory, $"{dropped.PackageId}.{dropped.Version}.nupkg"));

        var result = await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.Equal(kept.PackageId, Assert.Single(result.ActivePackages).PackageId);
        Assert.True(File.Exists(Path.Combine(dropped.InstallDirectory(_installRoot, FeedName), ".nuplane-ready")));
    }

    [Fact]
    public async Task RestoreAsync_WhenAPackageCannotBeResolved_ReportsDegradedAndTheFailedPackageIds()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        var options = Options(restore => restore.ConfigureBuilder += (builder, _) =>
            builder.Services.AddSingleton<IDesiredPackageSource>(new StaticDesiredSource(
                [new("Absent.Package", "9.9.9", FeedName, PackageUpdatePolicy.Exact, "test-source")])));

        var result = await NuplaneRestore.RestoreAsync(configuration, options);

        Assert.True(result.IsDegraded);
        Assert.Equal("Absent.Package", Assert.Single(result.FailedPackages));
    }

    [Fact]
    public async Task RestoreAsync_WithRelativeConfiguredPaths_ResolvesThemAgainstBasePath()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure(
            ("Nuplane:FeedResolution:PackageInstallRoot", "relative-packages"),
            ("Nuplane:StoreRegistry:StateFilePath", "relative-state/store-state.json"));

        var result = await NuplaneRestore.RestoreAsync(
            configuration,
            new() { BasePath = _root, ConfigureBuilder = DirectoryFeeds });

        Assert.Equal(Path.Combine(_root, "relative-packages"), result.InstallRoot);
        Assert.Equal(Path.Combine(_root, "relative-state", "store-state.json"), result.StateFilePath);
        Assert.True(File.Exists(result.StateFilePath));
    }

    [Fact]
    public async Task RestoreAsync_WithARelativeDirectoryFeedPath_ResolvesItAgainstBasePathNotTheCurrentDirectory()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = ConfigureWithRelativeDirectoryPath("drop");
        var elsewhere = Path.Combine(Path.GetTempPath(), "nuplane-host-free-restore-elsewhere", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        var originalDirectory = Environment.CurrentDirectory;
        // Proves resolution follows BasePath rather than the process's own directory: if it fell back
        // to the current directory instead, "drop" would resolve under `elsewhere`, find no feed
        // directory there, and the restore would report no active packages instead of the one written
        // to _feedDirectory.
        Environment.CurrentDirectory = elsewhere;

        try
        {
            var result = await NuplaneRestore.RestoreAsync(configuration, Options(o => o.BasePath = _root));

            Assert.False(result.IsDegraded);
            Assert.Equal(package.PackageId, Assert.Single(result.ActivePackages).PackageId);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_WithNoConfiguredPathsAndABasePath_UsesTheHostDefaultLayoutUnderIt()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var result = await NuplaneRestore.RestoreAsync(
            configuration,
            new() { BasePath = _root, ConfigureBuilder = DirectoryFeeds });

        Assert.Equal(Path.Combine(_root, ".nuplane", "packages"), result.InstallRoot);
        Assert.Equal(Path.Combine(_root, ".nuplane", "store-state.json"), result.StateFilePath);
    }

    [Fact]
    public async Task RestoreAsync_WithNothingPinningTheStateFile_RefusesInsteadOfWritingBesideThisProcess()
    {
        var configuration = Configure();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NuplaneRestore.RestoreAsync(
                configuration,
                new() { InstallRoot = _installRoot, ConfigureBuilder = DirectoryFeeds }));

        // The default a running host would apply — .nuplane/store-state.json under its own base
        // directory — is named in the refusal rather than applied to this process's base directory.
        Assert.Contains(".nuplane/store-state.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NuplaneRestoreOptions.BasePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NuplaneRestoreOptions.StateFilePath), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_WithNothingPinningTheInstallRoot_RefusesInsteadOfWritingBesideThisProcess()
    {
        var configuration = Configure();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NuplaneRestore.RestoreAsync(
                configuration,
                new() { StateFilePath = _stateFilePath, ConfigureBuilder = DirectoryFeeds }));

        Assert.Contains(".nuplane/packages", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NuplaneRestoreOptions.BasePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NuplaneRestoreOptions.InstallRoot), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_WithARelativeConfiguredPathAndNoBasePath_Refuses()
    {
        var configuration = Configure(("Nuplane:StoreRegistry:StateFilePath", "relative-state/store-state.json"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NuplaneRestore.RestoreAsync(
                configuration,
                new() { InstallRoot = _installRoot, ConfigureBuilder = DirectoryFeeds }));

        Assert.Contains("relative-state/store-state.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_WithARelativeOverride_Throws()
    {
        var configuration = Configure();

        await Assert.ThrowsAsync<ArgumentException>(
            () => NuplaneRestore.RestoreAsync(configuration, new() { InstallRoot = "packages", StateFilePath = _stateFilePath }));
    }

    [Fact]
    public async Task RestoreAsync_WhenConfigurationSelectsInMemoryPersistence_Refuses()
    {
        var configuration = Configure(("Nuplane:StoreRegistry:UseInMemoryStore", "true"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NuplaneRestore.RestoreAsync(configuration, Options()));

        Assert.Contains("in-memory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreAsync_WithARelativeLockFilePath_AnchorsItToTheStateDirectoryNotThisProcess()
    {
        var configuration = Configure(
            ("Nuplane:LockFile:Mode", "Enforce"),
            ("Nuplane:LockFile:Path", "nuplane.lock.json"));
        var options = Options();
        options.LockFilePath = null;

        await using var composition = await RestoreComposition.Create(configuration, options);

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(_stateFilePath)!, "nuplane.lock.json"),
            composition.Services.GetRequiredService<IOptions<LockFileOptions>>().Value.Path);
    }

    [Fact]
    public async Task RestoreAsync_WhenAFeedConfiguresCredentials_RemovesItFromResolutionBeforeAnyNetworkCall()
    {
        var configuration = Configure(
            ("Nuplane:Setup:Feeds:private-feed:ServiceIndex", "https://packages.example.com/v3/index.json"),
            ("Nuplane:Setup:Feeds:private-feed:Credentials", "secrets://packages/token"),
            ("Nuplane:Setup:Feeds:private-feed:IncludePatterns:0", "Ghost.Package [1.0.0]"));

        await using var composition = await RestoreComposition.Create(configuration, Options());

        Assert.Equal("private-feed", Assert.Single(composition.CredentialRefusedFeeds));
        Assert.DoesNotContain(
            composition.Services.GetRequiredService<IOptions<FeedResolutionOptions>>().Value.Feeds,
            feed => string.Equals(feed.Name, "private-feed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreAsync_WhenAFeedConfiguresCredentials_NeverEnumeratesOrAcquiresAgainstIt()
    {
        // A reserved, unroutable address (RFC 2606) so that a resolver which contacted it despite
        // the refusal would fail loudly instead of this test passing by luck or by timing out.
        var configuration = Configure(
            ("Nuplane:Setup:Feeds:private-feed:ServiceIndex", "https://credentialed.invalid/index.json"),
            ("Nuplane:Setup:Feeds:private-feed:Credentials", "secrets://packages/token"),
            ("Nuplane:Setup:Feeds:private-feed:IncludePatterns:0", "Ghost.Package [1.0.0]"));
        var versionEnumerator = Substitute.For<IFeedVersionEnumerator>();
        versionEnumerator
            .EnumerateVersionsAsync(default!, default!, default)
            .ReturnsForAnyArgs<Task<PackageVersionList>>(_ => throw new InvalidOperationException(
                "The credentialed feed must never be enumerated."));
        var remoteAcquirer = Substitute.For<IRemotePackageAcquirer>();
        remoteAcquirer
            .AcquireAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs<Task<string>>(_ => throw new InvalidOperationException(
                "The credentialed feed must never be acquired from."));
        var options = Options(restore => restore.ConfigureBuilder += (builder, _) =>
        {
            builder.Services.AddSingleton(versionEnumerator);
            builder.Services.AddSingleton(remoteAcquirer);
        });

        var result = await NuplaneRestore.RestoreAsync(configuration, options);

        Assert.Equal("private-feed", Assert.Single(result.CredentialRefusedFeeds));
        // The desired source still asks for it — the feed is missing from resolution, not from the
        // desired set — so the refusal is what stops resolution, not an absence of anything to do.
        Assert.Equal("Ghost.Package", Assert.Single(result.FailedPackages));
        await versionEnumerator.DidNotReceiveWithAnyArgs().EnumerateVersionsAsync(default!, default!, default);
        await remoteAcquirer.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task DescribeDesiredAsync_WhenAFeedConfiguresCredentials_NamesItWithoutContactingIt()
    {
        var configuration = Configure(
            ("Nuplane:Setup:Feeds:private-feed:ServiceIndex", "https://packages.example.com/v3/index.json"),
            ("Nuplane:Setup:Feeds:private-feed:Credentials", "secrets://packages/token"),
            ("Nuplane:Setup:Feeds:private-feed:IncludePatterns:0", "Ghost.Package [1.0.0]"));

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, Options());

        Assert.Equal("private-feed", Assert.Single(description.CredentialRefusedFeeds));
        Assert.Contains(description.Requests, request => request.PackageId == "Ghost.Package");
    }

    [Fact]
    public async Task DescribeDesiredAsync_ReportsTheHostsConfiguredCapabilitySelections()
    {
        var configuration = Configure(
            ("Nuplane:Capabilities:ef-provider", "PostgreSql,Sqlite"),
            ("Nuplane:Capabilities:message-broker:Option", "RabbitMq"),
            ("Nuplane:Capabilities:message-broker:Version", "[6.0.0]"));

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, Options());

        Assert.Equal(2, description.CapabilitySelections.Count);
        Assert.Equal(["PostgreSql", "Sqlite"], description.CapabilitySelections["ef-provider"].Options);
        Assert.Equal("RabbitMq", Assert.Single(description.CapabilitySelections["message-broker"].Options));
        Assert.Equal("[6.0.0]", description.CapabilitySelections["message-broker"].Version);
    }

    [Fact]
    public async Task DescribeDesiredAsync_WithNoConfiguredCapabilities_ReportsNoCapabilitySelections()
    {
        var configuration = Configure();

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, Options());

        Assert.Empty(description.CapabilitySelections);
    }

    [Fact]
    public async Task DescribeDesiredAsync_ForEveryShapeOfVersionConstraint_TellsPinnedFromUnpinned()
    {
        var configuration = Configure(
            ("Nuplane:Setup:Feeds:remote:ServiceIndex", "https://packages.example.com/v3/index.json"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:0", "Pinned.Package [1.2.3]"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:1", "Bare.Package"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:2", "Ranged.Package [1.0.0,2.0.0)"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:3", "Floating.Package 1.*"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:4", "Wildcard.*"));
        var options = Options(restore => restore.ConfigureBuilder += (builder, _) =>
            builder.Services.AddSingleton<IDesiredPackageSource>(
                new FeedRuleDesiredSource("catalog", ["Catalogued.*"], int.MaxValue, ["Catalogued.One"])));

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, options);

        Assert.Equal("1.2.3", Pin(description, "Pinned.Package"));
        Assert.Null(Pin(description, "Bare.Package"));
        Assert.Null(Pin(description, "Ranged.Package"));
        Assert.Null(Pin(description, "Floating.Package"));
        Assert.Null(Pin(description, "Catalogued.One"));
        // A wildcard against a remote feed has no catalog to expand against, so it asks for nothing.
        Assert.DoesNotContain(description.Requests, request => request.PackageId.StartsWith("Wildcard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DescribeDesiredAsync_ForADirectoryFeed_ReportsThePinnedVersionOnDisk()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory, version: "2.5.0");
        var configuration = Configure();

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, Options());

        var request = Assert.Single(description.Requests);
        Assert.Equal(package.PackageId, request.PackageId);
        Assert.Equal(FeedName, request.FeedName);
        Assert.True(request.IsPinned);
        Assert.Equal("2.5.0", request.PinnedVersion);
    }

    [Fact]
    public async Task DescribeDesiredAsync_ReportsWhereARestoreWouldWriteWithoutWritingThere()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var description = await NuplaneRestore.DescribeDesiredAsync(configuration, Options());

        Assert.Equal(_installRoot, description.InstallRoot);
        Assert.Equal(_stateFilePath, description.StateFilePath);
        Assert.False(Directory.Exists(_installRoot));
        Assert.False(File.Exists(_stateFilePath));
        Assert.False(File.Exists(StoreLock.GetLockFilePath(_stateFilePath)));
        Assert.False(File.Exists(_lockFilePath));
    }

    [Fact]
    public async Task RestoreAsync_WhenRequirePinnedVersionsAndARequestIsNotPinned_RefusesBeforeAcquiringAnything()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure(
            ("Nuplane:Setup:Feeds:remote:ServiceIndex", "https://packages.example.com/v3/index.json"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:0", "Unpinned.Package"));

        var result = await NuplaneRestore.RestoreAsync(
            configuration,
            Options(restore => restore.RequirePinnedVersions = true));

        Assert.True(result.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.UnpinnedRequests, result.SkipReason);
        Assert.Equal("Unpinned.Package", Assert.Single(result.UnpinnedRequests).PackageId);
        Assert.Empty(result.ActivePackages);
        Assert.False(Directory.Exists(_installRoot));
        Assert.False(File.Exists(_stateFilePath));
    }

    [Fact]
    public async Task RestoreAsync_WhenRequirePinnedVersionsAndEveryRequestIsPinned_Restores()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();

        var result = await NuplaneRestore.RestoreAsync(
            configuration,
            Options(restore => restore.RequirePinnedVersions = true));

        Assert.False(result.Skipped);
        Assert.Empty(result.UnpinnedRequests);
        Assert.Equal(package.PackageId, Assert.Single(result.ActivePackages).PackageId);
    }

    [Fact]
    public async Task RestoreAsync_WhenAnotherProcessOwnsTheStore_ReportsItAndWritesNothing()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        // Exactly the handle another process would hold: the same lock file, opened the same way.
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        using var heldElsewhere = new FileStream(
            StoreLock.GetLockFilePath(_stateFilePath),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await NuplaneRestore.RestoreAsync(configuration, Options());

        Assert.True(result.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.StoreLockUnavailable, result.SkipReason);
        Assert.Empty(result.ActivePackages);
        Assert.False(File.Exists(_stateFilePath));
        Assert.False(Directory.Exists(_installRoot));
    }

    [Fact]
    public async Task RestoreAsync_WhenTwoRestoresOfOneStoreOverlap_TheSecondDeclinesAndTheStateFileStaysReadable()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        using var gate = new RestoreGate();

        var first = NuplaneRestore.RestoreAsync(
            configuration,
            Options(restore => restore.ConfigureBuilder += (builder, _) => gate.Register(builder)));
        await gate.WaitUntilInsideAsync();

        var second = await NuplaneRestore.RestoreAsync(configuration, Options());
        gate.Release();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(second.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.StoreLockUnavailable, second.SkipReason);
        Assert.False(firstResult.Skipped);
        Assert.Equal(package.PackageId, Assert.Single(firstResult.ActivePackages).PackageId);
        Assert.Equal(package.PackageId, Assert.Single(await NuplaneStore.ReadActivePackagesAsync(_stateFilePath)).PackageId);
    }

    [Fact]
    public async Task RestoreAsync_ThenLoadFromState_LoadsWhatItRestored()
    {
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var configuration = Configure();
        var result = await NuplaneRestore.RestoreAsync(configuration, Options());
        Assert.False(HostFreeRestoreTestSupport.IsAssemblyVisible(package));

        var loaded = await NuplaneHostIntegratedLoader.LoadFromStateAsync(result.StateFilePath);

        Assert.Empty(loaded.FailedByPackageId);
        Assert.Equal(package.PackageId, Assert.Single(loaded.Packages).PackageId);
        Assert.NotNull(Type.GetType(package.MarkerTypeName));
    }

    [Fact]
    public async Task RestoreAsync_WithARealisticHostConfigurationRoot_YieldsTheSameResultAsItsNuplaneSection()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var section = Configure();
        var root = ConfigureAsRoot();

        var fromSection = await NuplaneRestore.RestoreAsync(section, Options());
        var fromRoot = await NuplaneRestore.RestoreAsync(root, Options());

        Assert.Equal(fromSection.InstallRoot, fromRoot.InstallRoot);
        Assert.Equal(fromSection.StateFilePath, fromRoot.StateFilePath);
        Assert.Equal(
            fromSection.ActivePackages.Select(static active => (active.PackageId, active.Version)),
            fromRoot.ActivePackages.Select(static active => (active.PackageId, active.Version)));
    }

    [Fact]
    public async Task DescribeDesiredAsync_WithARealisticHostConfigurationRoot_YieldsTheSameResultAsItsNuplaneSection()
    {
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory, version: "2.5.0");
        var section = Configure();
        var root = ConfigureAsRoot();

        var fromSection = await NuplaneRestore.DescribeDesiredAsync(section, Options());
        var fromRoot = await NuplaneRestore.DescribeDesiredAsync(root, Options());

        Assert.Equal(fromSection.InstallRoot, fromRoot.InstallRoot);
        Assert.Equal(fromSection.StateFilePath, fromRoot.StateFilePath);
        Assert.Equal(
            fromSection.Requests.Select(static request => (request.PackageId, request.PinnedVersion)),
            fromRoot.Requests.Select(static request => (request.PackageId, request.PinnedVersion)));
    }

    [Fact]
    public async Task RestoreAsync_WithAnUnnestedConfigurationRootDedicatedToNuplane_Restores()
    {
        // AddNuplane genuinely supports being handed configuration already scoped to its own keys,
        // with no "Nuplane" wrapper at all — one cheap assertion covers that shape.
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var root = BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["Setup:Feeds:" + FeedName + ":DirectoryPath"] = _feedDirectory,
                ["Setup:Feeds:" + FeedName + ":IncludeAll"] = "true",
                ["Setup:Feeds:" + FeedName + ":Directory:Watch"] = "false"
            },
            []);

        var result = await NuplaneRestore.RestoreAsync(root, Options());

        Assert.Equal(package.PackageId, Assert.Single(result.ActivePackages).PackageId);
    }

    [Fact]
    public async Task DescribeDesiredAsync_WithARootConfigurationHavingADirectoryFeedAndAnOrdinaryFeed_IncludesBoth()
    {
        // A caller passing the configuration root must get the directory feed too: its module
        // registration helper only works against the resolved Nuplane configuration
        // ConfigureBuilder's second argument supplies, not against a captured, unresolved root.
        HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var root = ConfigureAsRoot(
            ("Nuplane:Setup:Feeds:remote:ServiceIndex", "https://packages.example.com/v3/index.json"),
            ("Nuplane:Setup:Feeds:remote:IncludePatterns:0", "Ghost.Package [1.0.0]"));

        var description = await NuplaneRestore.DescribeDesiredAsync(root, Options());

        Assert.Contains(description.Requests, request => request.FeedName == FeedName);
        Assert.Contains(description.Requests, request => request.PackageId == "Ghost.Package");
    }

    [Fact]
    public async Task RestoreAsync_WithARootConfigurationHavingOnlyADirectoryFeed_RestoresInsteadOfRefusing()
    {
        // With only the module-registered directory feed and a root passed in, an entry point that
        // handed ConfigureBuilder the wrong configuration would see no directory feed and no other
        // desired source, and refuse an otherwise-populated configuration as empty.
        var package = HostFreeRestoreTestSupport.WriteNupkg(_feedDirectory);
        var root = ConfigureAsRoot();

        var result = await NuplaneRestore.RestoreAsync(root, Options());

        Assert.False(result.Skipped);
        Assert.Equal(package.PackageId, Assert.Single(result.ActivePackages).PackageId);
    }

    public static IEnumerable<object[]> EntryPoints()
    {
        yield return [true];
        yield return [false];
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task EntryPoint_WithNeitherAConfigurationRootNorItsNuplaneSection_RefusesNamingBothShapes(bool describeOnly)
    {
        var configuration = ConfigureWithoutANuplaneSection();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeEntryPoint(describeOnly, configuration, Options()));

        Assert.Contains("configuration root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"'{RestoreComposition.NuplaneSectionName}' section", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task EntryPoint_WhenTheNuplaneSectionConfiguresNoFeeds_Refuses(bool describeOnly)
    {
        var configuration = ConfigureWithoutAnyFeeds();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeEntryPoint(describeOnly, configuration, Options()));

        Assert.Contains("No feed and no desired package source", exception.Message, StringComparison.Ordinal);
    }

    private static Task InvokeEntryPoint(bool describeOnly, IConfiguration configuration, NuplaneRestoreOptions options) =>
        describeOnly
            ? NuplaneRestore.DescribeDesiredAsync(configuration, options)
            : NuplaneRestore.RestoreAsync(configuration, options);

    private static string? Pin(NuplaneDesiredDescription description, string packageId) =>
        description.Requests.Single(request => request.PackageId == packageId).PinnedVersion;

    private IConfigurationSection Configure(params (string Key, string? Value)[] settings) =>
        ConfigureAsRoot(settings).GetSection(RestoreComposition.NuplaneSectionName);

    /// <summary>
    /// Builds a realistic host configuration root: the feed settings nested under a
    /// <c>Nuplane</c> section, beside an unrelated sibling section a real host's own
    /// <c>appsettings</c> would also carry.
    /// </summary>
    private IConfigurationRoot ConfigureAsRoot(params (string Key, string? Value)[] settings) =>
        BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["Nuplane:Setup:Feeds:" + FeedName + ":DirectoryPath"] = _feedDirectory,
                ["Nuplane:Setup:Feeds:" + FeedName + ":IncludeAll"] = "true",
                ["Nuplane:Setup:Feeds:" + FeedName + ":Directory:Watch"] = "false",
                ["Logging:LogLevel:Default"] = "Information"
            },
            settings);

    /// <summary>
    /// Builds a configuration section whose directory feed's <c>DirectoryPath</c> is
    /// <paramref name="relativeDirectoryPath"/> instead of the absolute <see cref="_feedDirectory"/>
    /// <see cref="ConfigureAsRoot"/> uses, so a restore's resolution of it against
    /// <see cref="NuplaneRestoreOptions.BasePath"/> can be exercised end to end.
    /// </summary>
    private IConfigurationSection ConfigureWithRelativeDirectoryPath(string relativeDirectoryPath) =>
        BuildConfiguration(
            new Dictionary<string, string?>
            {
                ["Nuplane:Setup:Feeds:" + FeedName + ":DirectoryPath"] = relativeDirectoryPath,
                ["Nuplane:Setup:Feeds:" + FeedName + ":IncludeAll"] = "true",
                ["Nuplane:Setup:Feeds:" + FeedName + ":Directory:Watch"] = "false"
            },
            [])
            .GetSection(RestoreComposition.NuplaneSectionName);

    /// <summary>
    /// A host configuration root that carries neither a <c>Nuplane</c> section nor Nuplane's own
    /// keys at its top level — the shape both entry points must refuse rather than silently restore
    /// nothing from.
    /// </summary>
    private static IConfigurationRoot ConfigureWithoutANuplaneSection() =>
        BuildConfiguration(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Information" }, []);

    /// <summary>
    /// A host root whose <c>Nuplane</c> section exists but configures no feed — the composition an
    /// otherwise-valid configuration yields when a host genuinely has nothing to restore.
    /// </summary>
    private static IConfigurationRoot ConfigureWithoutAnyFeeds() =>
        BuildConfiguration(new Dictionary<string, string?> { ["Nuplane:Reconciliation:PollInterval"] = "00:05:00" }, []);

    private static IConfigurationRoot BuildConfiguration(
        Dictionary<string, string?> baseSettings,
        (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(baseSettings
                .Concat(settings.Select(static setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
                .ToDictionary(static setting => setting.Key, static setting => setting.Value))
            .Build();

    private NuplaneRestoreOptions Options(Action<NuplaneRestoreOptions>? configure = null)
    {
        var options = new NuplaneRestoreOptions
        {
            InstallRoot = _installRoot,
            StateFilePath = _stateFilePath,
            LockFilePath = _lockFilePath,
            ConfigureBuilder = DirectoryFeeds
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// The caller-supplied hook that adds the module-owned directory feeds the core package
    /// deliberately skips — the reason <see cref="NuplaneRestoreOptions.ConfigureBuilder"/> exists.
    /// Uses only the callback's own <c>configuration</c> argument — the already-resolved Nuplane
    /// configuration — never a configuration captured from the call to
    /// <see cref="NuplaneRestore.RestoreAsync"/>/<see cref="NuplaneRestore.DescribeDesiredAsync"/>,
    /// which is the trap the option's docs warn against.
    /// </summary>
    private static readonly Action<NuplaneBuilder, IConfiguration> DirectoryFeeds =
        (builder, configuration) => builder.AddDirectoryFeedsFromConfiguration(configuration);

    /// <summary>
    /// Holds one restore inside its reconciliation cycle — and therefore inside its store lock —
    /// until the test lets it go, so the overlap of two restores is deterministic rather than timed.
    /// </summary>
    private sealed class RestoreGate : IDisposable
    {
        private readonly TaskCompletionSource _inside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Register(NuplaneBuilder builder) =>
            builder.Services.AddSingleton<IDesiredPackageSource>(new GatedSource(this));

        public Task WaitUntilInsideAsync() => _inside.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release() => _release.TrySetResult();

        public void Dispose() => Release();

        private sealed class GatedSource(RestoreGate gate) : IDesiredPackageSource
        {
            public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            {
                gate._inside.TrySetResult();
                await gate._release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                return [];
            }
        }
    }
}
