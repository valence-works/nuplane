using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation.Models;

internal sealed record DesiredReadResult(
    IReadOnlyList<PackageRequest> Requests,
    bool UsedFallback,
    bool AllSourcesFresh);

