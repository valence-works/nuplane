using Nuplane.Abstractions;

namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Validates feed credential and trust configuration across feed resolution, trust policy,
/// and source trust options to ensure consistency and supply-chain safety.
/// </summary>
public sealed class FeedCredentialOptionsValidator
{
    /// <summary>
    /// Validates the combined feed configuration and returns a list of validation errors.
    /// </summary>
    /// <param name="feedResolution">The feed resolution options to validate.</param>
    /// <param name="trustPolicy">The feed trust policy options to validate.</param>
    /// <param name="sourceTrust">The source trust options to validate.</param>
    /// <returns>An empty list if the configuration is valid; otherwise a list of error descriptions.</returns>
    public IReadOnlyList<string> Validate(
        FeedResolutionOptions feedResolution,
        FeedTrustPolicyOptions trustPolicy,
        SourceTrustOptions sourceTrust)
    {
        ArgumentNullException.ThrowIfNull(feedResolution);
        ArgumentNullException.ThrowIfNull(trustPolicy);
        ArgumentNullException.ThrowIfNull(sourceTrust);

        var errors = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in feedResolution.Feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Name))
            {
                errors.Add("Feed name is required.");
                continue;
            }

            if (!seenNames.Add(feed.Name))
            {
                errors.Add($"Duplicate feed name '{feed.Name}'.");
            }

            // Local directory feeds use file:// URIs; they must not have credentials.
            var isLocalFeed = feed.ServiceIndex is not null
                && feed.ServiceIndex.IsAbsoluteUri
                && string.Equals(feed.ServiceIndex.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

            if (isLocalFeed)
            {
                if (!string.IsNullOrWhiteSpace(feed.Credentials))
                {
                    errors.Add($"Feed '{feed.Name}' uses a file:// URI and must not configure credentials.");
                }
                // Skip HTTPS enforcement for file:// feeds.
            }
            else
            {
                if (feed.ServiceIndex is null || !feed.ServiceIndex.IsAbsoluteUri || feed.ServiceIndex.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add($"Feed '{feed.Name}' service index must be an absolute HTTPS URI.");
                }

                if (!string.IsNullOrWhiteSpace(feed.Credentials))
                {
                    if (!sourceTrust.AllowRuntimeCredentialResolution)
                    {
                        errors.Add($"Feed '{feed.Name}' configures credentials but runtime credential resolution is disabled.");
                    }

                    if (!feed.Credentials.StartsWith("secrets://", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Feed '{feed.Name}' credentials must use a secret reference (secrets://...).");
                    }
                }
            }

            if (feed.TrustLevel == FeedTrustLevel.Untrusted && !trustPolicy.AllowUntrustedWithScopedOverride)
            {
                errors.Add($"Feed '{feed.Name}' is untrusted but untrusted scoped overrides are disabled.");
            }
        }

        foreach (var item in trustPolicy.Overrides)
        {
            if (item.Scope == FeedOverrideScope.None)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Target))
            {
                errors.Add("Override target is required when scope is package or feed-rule.");
            }

            if (trustPolicy.RequireOverrideReason && string.IsNullOrWhiteSpace(item.Reason))
            {
                errors.Add($"Override reason is required for override target '{item.Target}'.");
            }
        }

        if (feedResolution.Feeds.Count > 0 &&
            feedResolution.PolicyMode == FeedResolutionPolicyMode.Strict &&
            !feedResolution.StopOnFirstSuccessfulFeed &&
            feedResolution.Feeds.All(x => x.TrustLevel == FeedTrustLevel.Untrusted))
        {
            errors.Add("Strict mode requires at least one non-untrusted feed to avoid fail-open configuration.");
        }

        return errors;
    }
}
