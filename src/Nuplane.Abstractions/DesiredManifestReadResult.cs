namespace Nuplane.Abstractions;

/// <summary>
/// Represents the result of reading and parsing a desired manifest for a single reconciliation cycle.
/// </summary>
/// <param name="Status">The outcome status of the manifest read operation.</param>
/// <param name="ReasonCode">The reason code describing the outcome.</param>
/// <param name="SourceId">The identifier of the manifest source (e.g., file path).</param>
/// <param name="CorrelationId">The correlation identifier for the current reconciliation cycle.</param>
/// <param name="ObservedAtUtc">The UTC timestamp when the manifest was observed.</param>
/// <param name="Manifest">The parsed manifest, if the read was successful; otherwise <see langword="null"/>.</param>
public sealed record DesiredManifestReadResult(
    ManifestReadStatus Status,
    string ReasonCode,
    string SourceId,
    string CorrelationId,
    DateTimeOffset ObservedAtUtc,
    DesiredManifest? Manifest = null);