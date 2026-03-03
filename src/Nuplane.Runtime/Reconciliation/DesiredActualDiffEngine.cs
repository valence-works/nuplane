using Nuplane.Abstractions;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Reconciliation;

public sealed class DesiredActualDiffEngine
{
    public PackageChangeSet Compute(
        IReadOnlyCollection<ResolvedPackage> desired,
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var deduplicatedDesired = DeduplicateDesired(desired);

        var added = deduplicatedDesired
            .Where(pkg => !activeVersions.ContainsKey(pkg.Id))
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var updated = deduplicatedDesired
            .Where(pkg => activeVersions.TryGetValue(pkg.Id, out var activeVersion) && !string.Equals(activeVersion, pkg.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var removed = activeVersions.Keys
            .Where(activeId => deduplicatedDesired.All(pkg => !string.Equals(pkg.Id, activeId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(activeId => activeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(added, updated, removed, correlationId, timestamp);
    }

    public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        return DeduplicateDesired(desired)
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pkg => pkg.Id, pkg => pkg.Version, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResolvedPackage> DeduplicateDesired(IReadOnlyCollection<ResolvedPackage> desired)
    {
        return desired
            .GroupBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(pkg => VersionKey.Create(pkg.Version))
                .ThenBy(pkg => pkg.SourceName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

}