namespace Nuplane.Loading.Events;

/// <summary>
/// Published after a batch of packages has been successfully loaded into
/// Assembly Load Contexts during a reconciliation cycle.
/// Only fired when at least one package was loaded.
/// </summary>
/// <param name="CorrelationId">Correlation ID from the reconciliation cycle that triggered the load.</param>
/// <param name="LoadedAt">UTC timestamp recorded immediately after loading completed.</param>
/// <param name="LoadedPackages">Sessions for every package successfully loaded in this batch.</param>
internal sealed record PackageLoadedEvent(
    string CorrelationId,
    DateTimeOffset LoadedAt,
    IReadOnlyList<PackageLoadSession> LoadedPackages);
