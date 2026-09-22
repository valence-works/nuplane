using Nuplane.Feeds.Credentials;

namespace Nuplane.Feeds.Configuration;

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
    /// <returns>An empty list if the configuration is valid; otherwise a list of error descriptions.</returns>
    public IReadOnlyList<string> Validate(FeedResolutionOptions feedResolution)
    {
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
            var isLocalFeed = feed.ServiceIndex.IsAbsoluteUri && string.Equals(feed.ServiceIndex.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

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
                if (!feed.ServiceIndex.IsAbsoluteUri || feed.ServiceIndex.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add($"Feed '{feed.Name}' service index must be an absolute HTTPS URI.");
                }

                // The shape is checked here, where it is still configuration, so that the resolver
                // never has to throw at acquisition time. The offending value is never echoed: a
                // host that pasted a raw token in place of a reference lands here, and repeating it
                // in a validation message would print the secret.
                if (!string.IsNullOrWhiteSpace(feed.Credentials) && !SecretReference.TryParse(feed.Credentials, out _))
                {
                    errors.Add($"Feed '{feed.Name}' credentials must use a secret reference of the form '{SecretReference.ExpectedShape}'.");
                }
            }
        }

        if (feedResolution.Feeds.Count > 0 && feedResolution is { PolicyMode: FeedResolutionPolicyMode.Strict, StopOnFirstSuccessfulFeed: false })
        {
            errors.Add("Strict mode requires at least one non-untrusted feed to avoid fail-open configuration.");
        }

        return errors;
    }
}

