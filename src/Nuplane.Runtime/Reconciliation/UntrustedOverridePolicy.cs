using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class UntrustedOverridePolicy
{
    public UntrustedFeedOverride? FindOverride(PackageRequest request, FeedTrustPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var item in options.Overrides)
        {
            if (item.Scope == FeedOverrideScope.Package &&
                string.Equals(item.Target, request.Id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (item.Scope == FeedOverrideScope.FeedRule &&
                string.Equals(item.Target, request.SourceName, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }
}
