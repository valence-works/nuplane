namespace Nuplane.Abstractions;

/// <summary>
/// Represents the set of package changes produced by a reconciliation cycle.
/// </summary>
/// <param name="Added">Packages that were newly added.</param>
/// <param name="Updated">Packages that were updated to a new version.</param>
/// <param name="Removed">Identifiers of packages that were removed.</param>
/// <param name="CorrelationId">The correlation identifier for the reconciliation cycle.</param>
/// <param name="Timestamp">The time at which the change set was produced.</param>
public sealed record PackageChangeSet(
    IReadOnlyList<ResolvedPackage> Added,
    IReadOnlyList<ResolvedPackage> Updated,
    IReadOnlyList<string> Removed,
    string CorrelationId,
    DateTimeOffset Timestamp);