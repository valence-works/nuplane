using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Represents the outcome of a trust policy evaluation for a package/feed pair.
/// </summary>
/// <param name="Allowed">Whether the package is allowed by the trust policy.</param>
/// <param name="TrustLevel">The trust level of the feed.</param>
/// <param name="OverrideScope">The scope of any applied override.</param>
/// <param name="OverrideReason">The justification for the override, if any.</param>
/// <param name="ReasonCode">A machine-readable code describing the evaluation outcome.</param>
public sealed record FeedTrustPolicyOutcome(
    bool Allowed,
    FeedTrustLevel TrustLevel,
    FeedOverrideScope OverrideScope,
    string? OverrideReason,
    string ReasonCode);

