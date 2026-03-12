using Nuplane.Abstractions;

namespace Nuplane.Sources;

internal sealed record DesiredReadResult(
    IReadOnlyList<PackageRequest> Requests,
    bool UsedFallback,
    bool AllSourcesFresh);

