using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Sources;

/// <summary>Shared admission checks and scoped reads for desired package sources.</summary>
internal static class DesiredPackageSourceAccess
{
    internal static IReadOnlyList<DesiredPackageSourceEntry> Describe(IEnumerable<IDesiredPackageSource> sources)
    {
        var entries = new List<DesiredPackageSourceEntry>();
        foreach (var group in sources.Select(DescribeSource).GroupBy(entry => entry.SnapshotKey, StringComparer.OrdinalIgnoreCase))
        {
            var registrations = group.ToArray();
            if (registrations.Length == 1)
            {
                entries.Add(registrations[0]);
                continue;
            }

            if (registrations.Any(entry => entry.InstanceIdentityProvided))
            {
                throw new InvalidOperationException(
                    $"More than one desired-state source declares instance identity '{registrations[0].DiagnosticName}'.");
            }

            // Registration order distinguishes diagnostics. It cannot establish persisted ownership
            // because an operator may reorder or replace indistinguishable legacy registrations.
            for (var index = 0; index < registrations.Length; index++)
            {
                var entry = registrations[index];
                entries.Add(entry with
                {
                    DiagnosticName = $"{entry.DiagnosticName}#{index + 1}",
                    SnapshotKey = null,
                });
            }
        }

        return entries.OrderBy(entry => entry.DiagnosticName, StringComparer.Ordinal).ToArray();
    }

    private static DesiredPackageSourceEntry DescribeSource(IDesiredPackageSource source)
    {
        var identitySource = source is IDesiredPackageSourceAdapter adapter ? adapter.Source : source;
        var type = identitySource.GetType();
        var typeName = type.FullName ?? type.Name;
        if (identitySource is not IIdentifiedDesiredPackageSource identified)
        {
            return new(source, typeName, typeName);
        }

        var instanceId = identified.SourceInstanceId;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidOperationException($"Desired-state source '{typeName}' declares a blank instance identity.");
        }

        return new(
            source, instanceId, $"source-instance:v1:{instanceId}",
            InstanceIdentityProvided: true,
            LegacySnapshotKey: typeName,
            LegacySnapshotVerifier: identitySource as ILegacyDesiredPackageSourceSnapshotVerifier);
    }

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

internal sealed record DesiredPackageSourceEntry(
    IDesiredPackageSource Source,
    string DiagnosticName,
    string? SnapshotKey,
    bool InstanceIdentityProvided = false,
    string? LegacySnapshotKey = null,
    ILegacyDesiredPackageSourceSnapshotVerifier? LegacySnapshotVerifier = null);

/// <summary>Exposes the original source identity through a scoped access adapter.</summary>
internal interface IDesiredPackageSourceAdapter
{
    IDesiredPackageSource Source { get; }
}

/// <summary>Verifies request provenance before using a snapshot keyed by implementation type.</summary>
internal interface ILegacyDesiredPackageSourceSnapshotVerifier
{
    bool OwnsLegacySnapshot(IReadOnlyList<PackageRequest> requests);
}
