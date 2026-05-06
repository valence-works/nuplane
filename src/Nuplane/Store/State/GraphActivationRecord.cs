namespace Nuplane.Store.State;

/// <summary>
/// Represents persisted activation state for one resolved package graph generation.
/// </summary>
/// <param name="GraphId">The active graph identity.</param>
/// <param name="GenerationId">The active graph generation identity.</param>
/// <param name="RootPackageIds">The root package identifiers for the graph.</param>
/// <param name="NodePackageIds">The package identifiers selected in the graph.</param>
/// <param name="ActivatedAtUtc">The UTC timestamp when the graph became active.</param>
/// <param name="CorrelationId">The reconciliation correlation that activated the graph.</param>
/// <param name="Status">The persisted graph activation status.</param>
/// <param name="Failure">The optional graph failure summary.</param>
/// <param name="NodeVersionsByPackageId">The package versions selected in the graph, keyed by package identifier.</param>
public sealed record GraphActivationRecord(
    string GraphId,
    string GenerationId,
    IReadOnlyList<string> RootPackageIds,
    IReadOnlyList<string> NodePackageIds,
    DateTimeOffset ActivatedAtUtc,
    string CorrelationId,
    GraphActivationStatus Status,
    GraphActivationFailure? Failure = null,
    IReadOnlyDictionary<string, string>? NodeVersionsByPackageId = null);

/// <summary>
/// Describes the persisted lifecycle status of an activation graph.
/// </summary>
public enum GraphActivationStatus
{
    /// <summary>
    /// The graph generation is active.
    /// </summary>
    Active,

    /// <summary>
    /// The graph generation is stale but retained for fallback diagnostics.
    /// </summary>
    Stale,

    /// <summary>
    /// The graph generation failed before it could become active.
    /// </summary>
    Failed,

    /// <summary>
    /// The graph generation was replaced by a newer generation.
    /// </summary>
    Replaced
}

/// <summary>
/// Describes a persisted graph activation failure without runtime-only objects.
/// </summary>
/// <param name="FailureStage">The stage where graph activation failed.</param>
/// <param name="ReasonCode">The stable reason code for the failure.</param>
/// <param name="Message">The failure message.</param>
/// <param name="CyclePath">The optional cycle path for dependency cycle failures.</param>
/// <param name="UnsupportedAssetPath">The optional package-relative unsupported asset path.</param>
public sealed record GraphActivationFailure(
    string FailureStage,
    string ReasonCode,
    string Message,
    IReadOnlyList<string>? CyclePath = null,
    string? UnsupportedAssetPath = null);
