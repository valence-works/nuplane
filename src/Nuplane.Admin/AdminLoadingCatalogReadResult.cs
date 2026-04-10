using Nuplane.Loading;

namespace Nuplane.Admin;

/// <summary>
/// Result of composing an optional loading catalog read through the admin surface.
/// </summary>
public sealed record AdminLoadingCatalogReadResult(
    bool IsAvailable,
    LoadingCatalogSnapshot? Snapshot,
    string? Reason,
    string CorrelationId);

