using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Metadata;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class PackageStoreCompositionValidator
{
    internal static void RequireSupportedParticipantSelection(
        PackageStoreCompositionSnapshot composition,
        IPhysicalStoreFileSystem files,
        IStoreRegistry storeRegistry,
        IStoreStateSerializer serializer,
        IPackageStoreAdmission admission,
        ITrustedPackageStoreRootCatalog roots,
        ITrustedPackageStoreMemberDeclarationCatalog members,
        FeedResolutionOptions feedOptions)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (!composition.IsParticipant)
            return;

        PackageStoreRuntimeAdmission.RequireCompleteParticipantRoster(composition);
        var supportedProvider = files switch
        {
            UnixPhysicalStoreFileSystem => UnixPhysicalStoreFileSystem.IsSupportedPlatform,
            WindowsPhysicalStoreFileSystem => WindowsPhysicalStoreFileSystem.IsSupportedPlatform,
            _ => false
        };
        if (!supportedProvider ||
            storeRegistry is not StoreRegistry builtInRegistry ||
            serializer is not StoreStateSerializer builtInSerializer ||
            !builtInRegistry.IsParticipantGuarded ||
            !builtInSerializer.IsParticipantGuarded ||
            !ReferenceEquals(builtInRegistry.PayloadSerializer, serializer) ||
            admission is not PackageStoreAdmission builtInAdmission ||
            !ReferenceEquals(builtInAdmission.SelectedStoreRegistry, storeRegistry) ||
            !ReferenceEquals(builtInAdmission.Registry.Files, files))
        {
            throw Refuse("The selected participant provider, admission, registry, and protection serializer are not one supported built-in composition.");
        }

        if (string.IsNullOrWhiteSpace(builtInRegistry.CoordinatedStateFileLocator))
            throw Refuse("Coordinated participation requires a configured on-disk state file.");

        var expectedDefaultRoot = PackageInstallStore.ResolveInstallRoot(feedOptions);
        var actualRoots = roots.Roots;
        var expectedRoots = composition.Roots;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (actualRoots.Count != expectedRoots.Count + 1 ||
            !string.Equals(actualRoots[0].Label, "default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualRoots[0].RootPath, expectedDefaultRoot, pathComparison))
        {
            throw Refuse("The selected package-store root catalog differs from the frozen participant composition.");
        }

        for (var index = 0; index < expectedRoots.Count; index++)
        {
            var expected = expectedRoots[index];
            var actual = actualRoots[index + 1];
            if (!string.Equals(actual.Label, expected.Label, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.RootPath, expected.RootLocator, pathComparison))
            {
                throw Refuse("The selected package-store root catalog differs from the frozen participant composition.");
            }
        }

        if (!members.IsConfigured || !members.IsComplete || members.All.Count != composition.Members.Count ||
            composition.Members.Any(expected => !members.All.Any(actual =>
                string.Equals(actual.RootLabel, expected.RootLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(actual.MemberId, expected.MemberId, StringComparison.Ordinal) &&
                string.Equals(actual.StateFileLocator, expected.StateFileLocator, pathComparison))))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.IncompleteEnrollment,
                "The selected member catalog differs from the frozen complete participant roster.");
        }

        var currentStatePath = Path.GetFullPath(builtInRegistry.CoordinatedStateFileLocator);
        if (!composition.Members.Any(member =>
                string.Equals(member.RootLabel, "default", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(member.StateFileLocator), currentStatePath, pathComparison)))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.IncompleteEnrollment,
                "The configured state file is not one of the declared members of the default package-store root.");
        }
    }

    internal static void RequireSupportedParticipantExecutionRoute(
        PackageStoreCompositionSnapshot composition,
        IPackageResolver packageResolver,
        IPackageMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(packageResolver);
        ArgumentNullException.ThrowIfNull(metadataReader);
        if (!composition.IsParticipant)
            return;

        if (packageResolver is not IScopedPackageResolver)
            throw Refuse("The selected participant package resolver has no scoped package-store contract.");
        if (packageResolver is MultiFeedPackageResolver builtInResolver &&
            (!builtInResolver.IsParticipantGuarded ||
             builtInResolver.RemotePackageAcquirer is not IScopedRemotePackageAcquirer scopedAcquirer ||
             scopedAcquirer is NuGetRemotePackageAcquirer builtInAcquirer && !builtInAcquirer.IsParticipantGuarded))
        {
            throw Refuse("The selected built-in participant resolver and acquirer are not guarded as one scoped composition.");
        }

        if (metadataReader is not NuplanePackageMetadataReader builtInMetadataReader ||
            !builtInMetadataReader.IsParticipantGuarded)
        {
            throw Refuse("The selected participant metadata reader does not require a live scoped native borrow.");
        }
    }

    private static PackageStoreAdmissionException Refuse(string message)
        => new(PackageStoreAdmissionReason.UnsupportedParticipant, message);
}
