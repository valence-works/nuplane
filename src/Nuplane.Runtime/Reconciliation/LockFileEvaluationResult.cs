using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Represents the result of evaluating a package against the lock file.
/// </summary>
/// <param name="Allowed">Whether the package passed the lock file check.</param>
/// <param name="ReasonCode">A machine-readable code describing the evaluation outcome.</param>
/// <param name="EffectivePackage">The effective resolved package after lock file enforcement, or <see langword="null"/> if blocked.</param>
/// <param name="ExpectedHash">The expected integrity hash from the lock entry, if any.</param>
public sealed record LockFileEvaluationResult(
    bool Allowed,
    string ReasonCode,
    ResolvedPackage? EffectivePackage,
    string? ExpectedHash);

