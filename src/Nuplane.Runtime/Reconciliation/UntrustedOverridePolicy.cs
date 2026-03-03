using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Finds matching untrusted feed overrides for a package request based on package-scoped
/// or feed-rule-scoped override entries.
/// </summary>
public sealed class UntrustedOverridePolicy
{
    /// <summary>
    /// Finds a matching override for the specified package request.
    /// </summary>
    /// <param name="request">The package request to match.</param>
    /// <param name="options">The trust policy options containing override entries.</param>
    /// <returns>The matching override, or <see langword="null"/> if no override applies.</returns>
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
