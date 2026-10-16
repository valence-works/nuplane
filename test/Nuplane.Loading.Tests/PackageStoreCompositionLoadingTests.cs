using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Loading.Tests.Fixtures;
using Nuplane.Registration;

namespace Nuplane.Loading.Tests;

public sealed class PackageStoreCompositionLoadingTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-composition-loading-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantRawLoader_RefusesBeforeAdvisorOrPackageRead(bool graph)
    {
        using var provider = CreateProvider(participant: true);
        var advisor = new CountingAdvisor();
        var loader = new PackageLoader(loadModeAdvisors: [advisor],
            composition: provider.GetRequiredService<PackageStoreCompositionSnapshot>());
        var package = MissingPackage();

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => graph
            ? loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None)
            : loader.EnsureLoadedAsync([package], [], CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(0, advisor.Calls);
        Assert.Empty(loader.Sessions);
    }

    [Theory]
    [InlineData(PackageLoadModeSelectionPolicy.Automatic)]
    [InlineData(PackageLoadModeSelectionPolicy.ExplicitOnly)]
    public async Task ParticipantLoadModeSelection_RequiresLeaseBeforePolicyOrAdvisor(PackageLoadModeSelectionPolicy policy)
    {
        using var provider = CreateProvider(participant: true);
        var advisor = new CountingAdvisor();
        var selector = new PackageLoadModeSelector([advisor],
            composition: provider.GetRequiredService<PackageStoreCompositionSnapshot>());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => selector.SelectGraphAsync(
            [MissingPackage()], new LoadingOptions { LoadModeSelectionPolicy = policy }, "graph:missing",
            CancellationToken.None).AsTask());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(0, advisor.Calls);
    }

    [Fact]
    public void ParticipantRawAssemblyScan_RefusesBeforeMissingDirectoryIsInspected()
    {
        using var provider = CreateProvider(participant: true);
        var loader = new PackageLoader(composition: provider.GetRequiredService<PackageStoreCompositionSnapshot>());
        var package = MissingPackage();

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            loader.BuildScanCandidates(package.Id, package.InstallPath));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
    }

    [Fact]
    public async Task ParticipantUnscopedObserver_RefusesBeforeLoadingDisabledShortcut()
    {
        using var provider = CreateProvider(participant: true);
        var loader = Substitute.For<IPackageLoader>();
        var observer = new PackageAutoLoadingObserver(loader, Substitute.For<ILoadingEventDispatcher>(),
            Options.Create(new LoadingOptions { Enabled = false }), NullLogger<PackageAutoLoadingObserver>.Instance,
            composition: provider.GetRequiredService<PackageStoreCompositionSnapshot>());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => observer.OnPackagesReconciledAsync(
            new PackageChangeSet([], [], [], "participant-raw", DateTimeOffset.UnixEpoch), [], CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Empty(loader.ReceivedCalls());
    }

    [Fact]
    public void ParticipantMetadataAdapter_UsesSharedGuardBeforeMissingFileShortcut()
    {
        using var provider = CreateProvider(participant: true);
        var reader = provider.GetRequiredService<PackageMetadataLoadModeReader>();
        var package = MissingPackage();

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            reader.Read(package.Id, package.Version, package.InstallPath));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
    }

    [Fact]
    public async Task OrdinaryDiLoaderAndAssemblyQuery_RetainCompatibilityWithIncidentalControlData()
    {
        using var provider = CreateProvider(participant: false);
        var install = _tempDir.CreateSubdirectory("ordinary-install");
        File.Copy(typeof(FixtureMarker).Assembly.Location,
            Path.Combine(install.FullName, "Nuplane.Loading.Tests.Fixtures.dll"));
        var control = install.CreateSubdirectory(".nuplane-store");
        File.WriteAllText(Path.Combine(control.FullName, "membership.json"), "incidental malformed data");
        var package = new ResolvedPackage("Nuplane.Loading.Tests.Fixtures", "1.0.0", "feed", install.FullName,
            DateTimeOffset.UnixEpoch);
        var loader = provider.GetRequiredService<PackageLoader>();
        WeakReference? unloadedContext = null;
        try
        {
            var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

            Assert.Empty(result.FailedByPackageId);
            Assert.Single(result.Loaded);
            unloadedContext = VerifyOrdinaryAssemblyQuery(provider, loader, package);
        }
        finally
        {
            loader.UnloadContextsNotActive(new Dictionary<string, string>());
            if (unloadedContext is not null)
            {
                CollectibleContextTestLifetime.CollectUntilGone(unloadedContext);
                Assert.False(unloadedContext.IsAlive, "The ordinary collectible context must release before fixture deletion.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference VerifyOrdinaryAssemblyQuery(
        ServiceProvider provider, PackageLoader loader, ResolvedPackage package)
    {
        Assert.Contains(provider.GetRequiredService<PackageAssemblyProvider>().GetAssemblies(package.Id, package.Version),
            assembly => assembly.GetName().Name == package.Id);
        Assert.True(loader.TryGetContext(package.Id, package.Version, out var handle));
        return new WeakReference(handle!.Context, trackResurrection: true);
    }

    private ServiceProvider CreateProvider(bool participant)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder =>
        {
            var statePath = Path.Combine(_tempDir.FullName, "member.json");
            builder.WithStateFile(statePath);
            if (participant)
                builder.UseCoordinatedPackageStore([new PackageStoreMemberDeclaration("default", "member-a", statePath)], true);
            builder.AutoloadPackages();
        });
        var root = _tempDir.CreateSubdirectory("packages").FullName;
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = root);
        return services.BuildServiceProvider();
    }

    private ResolvedPackage MissingPackage() =>
        new("missing", "1.0.0", "feed", Path.Combine(_tempDir.FullName, "absent"), DateTimeOffset.UnixEpoch);

    private sealed class CountingAdvisor : IPackageLoadModeAdvisor
    {
        internal int Calls { get; private set; }
        public string Name => "counter";

        public ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
            LoadModeAdvisorContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<LoadModeAdvisorResult>>([]);
        }
    }
}
