using Nuplane.Abstractions;

namespace Nuplane.Runtime.Sources;

internal sealed record DesiredReadResult(
    IReadOnlyList<PackageRequest> Requests,
    bool UsedFallback,
    bool AllSourcesFresh);

