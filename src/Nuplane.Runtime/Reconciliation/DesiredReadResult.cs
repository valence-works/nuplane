using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

internal sealed record DesiredReadResult(
    IReadOnlyList<PackageRequest> Requests,
    bool UsedFallback,
    bool AllSourcesFresh);

