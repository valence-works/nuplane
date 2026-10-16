using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Store.Coordination;

namespace Nuplane.Registration;

/// <summary>Selects the package-store route frozen by host composition.</summary>
internal enum PackageStoreCompositionMode
{
    Ordinary,
    CoordinatedParticipant
}

/// <summary>
/// Immutable composition selection shared with optional modules. The mode is a routing choice, not
/// filesystem authority; root and member definitions remain subject to runtime admission.
/// </summary>
internal sealed class PackageStoreCompositionSnapshot
{
    private static readonly PackageStoreCompositionSnapshot DefaultOrdinary = new(
        PackageStoreCompositionMode.Ordinary,
        allClientsParticipate: false,
        Array.Empty<TrustedPackageStoreRootRegistration>(),
        Array.Empty<TrustedPackageStoreMemberDeclaration>());

    internal PackageStoreCompositionSnapshot(
        PackageStoreCompositionMode mode,
        bool allClientsParticipate,
        IEnumerable<TrustedPackageStoreRootRegistration> roots,
        IEnumerable<TrustedPackageStoreMemberDeclaration> members)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(members);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PackageStoreCompositionMode.CoordinatedParticipant && !allClientsParticipate)
            throw new ArgumentException("Coordinated participation requires an explicit complete-client-roster acknowledgement.", nameof(allClientsParticipate));

        Mode = mode;
        AllClientsParticipate = allClientsParticipate;
        Roots = Array.AsReadOnly(roots.Select(static root => new TrustedPackageStoreRootRegistration(
            root.Label,
            root.RootLocator)).ToArray());
        Members = Array.AsReadOnly(members.Select(static member => new TrustedPackageStoreMemberDeclaration(
            member.RootLabel,
            member.MemberId,
            member.StateFileLocator)).ToArray());
    }

    internal PackageStoreCompositionMode Mode { get; }
    internal bool IsParticipant => Mode == PackageStoreCompositionMode.CoordinatedParticipant;
    internal bool AllClientsParticipate { get; }
    internal IReadOnlyList<TrustedPackageStoreRootRegistration> Roots { get; }
    internal IReadOnlyList<TrustedPackageStoreMemberDeclaration> Members { get; }

    internal static PackageStoreCompositionSnapshot Ordinary => DefaultOrdinary;

    internal static PackageStoreCompositionSnapshot? From(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.LastOrDefault(static descriptor =>
                descriptor.ServiceType == typeof(PackageStoreCompositionSnapshot))
            ?.ImplementationInstance as PackageStoreCompositionSnapshot;
    }

    internal static void Register(IServiceCollection services, PackageStoreCompositionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(snapshot);
        services.Replace(ServiceDescriptor.Singleton(snapshot));
    }
}
