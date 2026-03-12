using Nuplane.Abstractions;

namespace Nuplane.Feeds;

/// <summary>
/// Represents the per-package acquisition outcome used for rollback evaluation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="Stage">The acquisition stage where the outcome was determined.</param>
/// <param name="Status">The outcome status.</param>
/// <param name="ReasonCode">The reason code for the outcome.</param>
public sealed record AcquisitionOutcomeEntry(
    string PackageId,
    string Version,
    AcquisitionStage Stage,
    PackageOperationStatus Status,
    string ReasonCode);

