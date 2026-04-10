namespace Nuplane.Operational;

/// <summary>
/// Represents a module- or feature-owned contribution to the operational-state surface.
/// </summary>
/// <param name="Contributor">The stable contributor name.</param>
/// <param name="DegradedReasons">Machine-readable degraded reasons contributed by the module.</param>
public sealed record OperationalStateContribution(
    string Contributor,
    IReadOnlyList<string> DegradedReasons)
{
    /// <summary>
    /// Gets whether the contribution indicates a degraded condition.
    /// </summary>
    public bool IsDegraded => DegradedReasons.Count > 0;
}

