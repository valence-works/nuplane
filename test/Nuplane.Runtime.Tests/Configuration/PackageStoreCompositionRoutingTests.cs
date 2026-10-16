using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Metadata;
using Nuplane.Reconciliation;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class PackageStoreCompositionRoutingTests
{
    [Fact]
    public void AddNuplane_OrdinaryDefaultAndAdditionalRootDoNotSelectParticipation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.AddPackageStoreRoot("extra", Path.GetTempPath()));

        var composition = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(PackageStoreCompositionSnapshot)).ImplementationInstance
            as PackageStoreCompositionSnapshot;

        Assert.NotNull(composition);
        Assert.Equal(PackageStoreCompositionMode.Ordinary, composition!.Mode);
        Assert.False(composition.AllClientsParticipate);
        Assert.Empty(composition.Members);
        Assert.Single(composition.Roots);
    }

    [Fact]
    public void UseCoordinatedPackageStore_CopiesCompleteRosterAndPreservesItAcrossRepeatedCalls()
    {
        using var temp = new TempDirectory();
        var installRoot = Path.Combine(temp.Path, "packages");
        var extraRoot = Path.Combine(temp.Path, "extra-packages");
        var roster = new List<PackageStoreMemberDeclaration>
        {
            new("default", "host", Path.Combine(temp.Path, "host-state.json")),
            new("extra", "worker", Path.Combine(temp.Path, "worker-state.json"))
        };
        var originalRoster = roster.ToArray();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = installRoot);

        services.AddNuplane(builder =>
        {
            builder.AddPackageStoreRoot("extra", extraRoot);
            builder.UseCoordinatedPackageStore(roster, allClientsParticipate: true);
        });
        roster.Clear();

        services.AddNuplane(builder => builder.UseCoordinatedPackageStore(originalRoster, allClientsParticipate: true));
        var composition = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(PackageStoreCompositionSnapshot)).ImplementationInstance
            as PackageStoreCompositionSnapshot;

        Assert.NotNull(composition);
        Assert.Equal(PackageStoreCompositionMode.CoordinatedParticipant, composition!.Mode);
        Assert.True(composition.AllClientsParticipate);
        Assert.Equal(["extra"], composition.Roots.Select(static root => root.Label));
        Assert.Equal(2, composition.Members.Count);
        Assert.Contains(composition.Members, member => member.RootLabel == "default" && member.MemberId == "host");
        Assert.Contains(composition.Members, member => member.RootLabel == "extra" && member.MemberId == "worker");

        Assert.Throws<InvalidOperationException>(() => services.AddNuplane(builder =>
            builder.UseCoordinatedPackageStore(
                [.. originalRoster, new("extra", "late-client", Path.Combine(temp.Path, "late-state.json"))],
                allClientsParticipate: true)));
    }

    [Fact]
    public void UseCoordinatedPackageStore_RequiresAcknowledgedNonemptyCompleteRootRoster()
    {
        var unacknowledged = new ServiceCollection();
        unacknowledged.AddLogging();
        Assert.Throws<ArgumentException>(() => unacknowledged.AddNuplane(builder =>
            builder.UseCoordinatedPackageStore(
                [new("default", "host", Path.GetFullPath("state.json"))],
                allClientsParticipate: false)));

        var empty = new ServiceCollection();
        empty.AddLogging();
        Assert.Throws<ArgumentException>(() => empty.AddNuplane(builder =>
            builder.UseCoordinatedPackageStore([], allClientsParticipate: true)));

        var incomplete = new ServiceCollection();
        incomplete.AddLogging();
        Assert.Throws<PackageStoreAdmissionException>(() => incomplete.AddNuplane(builder =>
        {
            builder.AddPackageStoreRoot("extra", Path.GetTempPath());
            builder.UseCoordinatedPackageStore(
                [new("default", "host", Path.GetFullPath("state.json"))],
                allClientsParticipate: true);
        }));
    }

    [Fact]
    public async Task ParticipantDi_GuardsUnscopedMetadataResolverAndRawStateAccess()
    {
        using var temp = new TempDirectory();
        var stateFile = Path.Combine(temp.Path, "store-state.json");
        var services = CreateParticipantServices(temp.Path, stateFile);
        using var provider = services.BuildServiceProvider();

        var metadata = provider.GetRequiredService<NuplanePackageMetadataReader>();
        var metadataRefusal = metadata.Read("Acme.Package", "1.0.0", Path.Combine(temp.Path, "missing-install"));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, metadataRefusal.AdmissionRefusalReason);

        var registryRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IStoreRegistry>().GetStateAsync(CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, registryRefusal.Reason);

        var serializerRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IStoreStateSerializer>().LoadAsync(stateFile, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, serializerRefusal.Reason);

        var request = new PackageRequest("Acme.Package", "[1.0.0]", null, PackageUpdatePolicy.Exact, "test");
        var resolverRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IPackageResolver>().ResolveAsync(request, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, resolverRefusal.Reason);

        Assert.False(File.Exists(stateFile));
    }

    [Fact]
    public void OrdinaryDi_ResolvesReconciliationWithoutActivatingNativeStoreProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        var providerActivated = false;
        services.Replace(ServiceDescriptor.Singleton<IPhysicalStoreFileSystem>(_ =>
        {
            providerActivated = true;
            throw new InvalidOperationException("Ordinary composition must not probe native admission.");
        }));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IReconciliationService>();

        Assert.False(providerActivated);
        Assert.Equal(PackageStoreCompositionMode.Ordinary,
            provider.GetRequiredService<PackageStoreCompositionSnapshot>().Mode);
    }

    [Fact]
    public void ParticipantDi_RejectsUnsupportedSelectedProviderBeforeResolvingDesiredSources()
    {
        using var temp = new TempDirectory();
        var services = CreateParticipantServices(temp.Path, Path.Combine(temp.Path, "store-state.json"));
        var files = new UnsupportedPhysicalStoreFileSystem();
        services.Replace(ServiceDescriptor.Singleton<IPhysicalStoreFileSystem>(files));
        var desiredSourceFactoryCalls = 0;
        services.AddSingleton<IDesiredPackageSource>(_ =>
        {
            desiredSourceFactoryCalls++;
            return Substitute.For<IDesiredPackageSource>();
        });
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(0, desiredSourceFactoryCalls);
    }

    private static ServiceCollection CreateParticipantServices(string root, string stateFile)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = Path.Combine(root, "packages"));
        services.Configure<StoreRegistryOptions>(options => options.StateFilePath = stateFile);
        services.AddNuplane(builder => builder.UseCoordinatedPackageStore(
            [new("default", "host", stateFile)],
            allClientsParticipate: true));
        return services;
    }

    private sealed class UnsupportedPhysicalStoreFileSystem : IPhysicalStoreFileSystem
    {
        private static InvalidOperationException UnexpectedCall() =>
            new("An unsupported participant provider must be rejected before filesystem operations.");

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => throw UnexpectedCall();
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName) => throw UnexpectedCall();
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName) => throw UnexpectedCall();
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory) => throw UnexpectedCall();
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access) => throw UnexpectedCall();
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity) => throw UnexpectedCall();
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => throw UnexpectedCall();
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName) => throw UnexpectedCall();
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName) => throw UnexpectedCall();
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes) => throw UnexpectedCall();
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents) => throw UnexpectedCall();
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file) => throw UnexpectedCall();
    }
}
