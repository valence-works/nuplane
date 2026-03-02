namespace Nuplane.Abstractions;

public sealed record PackageChangeSet(
    IReadOnlyList<ResolvedPackage> Added,
    IReadOnlyList<ResolvedPackage> Updated,
    IReadOnlyList<string> Removed,
    string CorrelationId,
    DateTimeOffset Timestamp);