namespace Nuplane.Runtime.Feeds;

/// <summary>
/// Thrown when no eligible feed candidate exists for resolving a package request.
/// Replaces the generic <see cref="InvalidOperationException"/> path that was
/// previously thrown when all candidate feeds were unavailable or none were configured.
/// </summary>
public sealed class NoEligibleFeedException(string packageId, string failureReason)
    : InvalidOperationException($"No eligible feed could resolve package '{packageId}': {failureReason}")
{
    /// <summary>Gets the identifier of the package that could not be resolved.</summary>
    public string PackageId { get; } = packageId;

    /// <summary>Gets the reason why no eligible feed was found.</summary>
    public string FailureReason { get; } = failureReason;
}

