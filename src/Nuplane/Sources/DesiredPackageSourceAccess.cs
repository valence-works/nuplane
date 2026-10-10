using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Sources;

/// <summary>Shared admission checks and scoped reads for desired package sources.</summary>
internal static class DesiredPackageSourceAccess
{
    internal static void Validate(
        IReadOnlyList<IDesiredPackageSource> sources,
        string message = "Every enrolled desired source must declare scoped access or package-path independence.")
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(static source => source is not IScopedDesiredPackageSource
            and not IPackagePathIndependentDesiredPackageSource))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant, message);
        }
    }

    internal static async Task<IReadOnlyList<PackageRequest>> ReadAsync(
        IDesiredPackageSource source,
        PackageStoreOperationOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);

        if (source is IScopedDesiredPackageSource scoped)
        {
            using var borrow = owner.Borrow();
            return await scoped.GetDesiredAsync(borrow, cancellationToken).ConfigureAwait(false);
        }

        if (source is IPackagePathIndependentDesiredPackageSource)
            return await source.GetDesiredAsync(cancellationToken).ConfigureAwait(false);

        throw new PackageStoreAdmissionException(
            PackageStoreAdmissionReason.UnsupportedParticipant,
            "The desired source has no scoped package-store contract.");
    }
}
