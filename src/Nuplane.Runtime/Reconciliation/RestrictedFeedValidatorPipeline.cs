using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class RestrictedFeedValidatorPipeline
{
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
