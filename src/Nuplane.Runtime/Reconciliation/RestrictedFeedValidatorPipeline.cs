using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Evaluates whether a package from a restricted feed passes the required validation.
/// </summary>
public sealed class RestrictedFeedValidatorPipeline
{
    /// <summary>
    /// Evaluates the restricted feed validator for the given package request and feed.
    /// </summary>
    /// <param name="request">The package request.</param>
    /// <param name="feed">The feed definition.</param>
    /// <param name="options">The trust policy options.</param>
    /// <param name="validatorPassed">Whether the external validator passed.</param>
    /// <returns><see langword="true"/> if the package is allowed; otherwise <see langword="false"/>.</returns>
    public bool Evaluate(PackageRequest request, FeedDefinition feed, FeedTrustPolicyOptions options, bool validatorPassed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(options);

        if (feed.TrustLevel != FeedTrustLevel.Restricted)
        {
            return true;
        }

        if (!options.DefaultRestrictedValidatorRequired)
        {
            return true;
        }

        return validatorPassed;
    }
}
