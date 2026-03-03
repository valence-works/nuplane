using Nuplane.Abstractions;

namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Configuration options governing how feed trust levels are evaluated, including
/// untrusted feed overrides and restricted-feed validation requirements.
/// </summary>
public sealed class FeedTrustPolicyOptions
{
    /// <summary>
    /// Gets or sets whether restricted feeds require their validator to pass.
    /// </summary>
    public bool DefaultRestrictedValidatorRequired { get; set; } = true;

    /// <summary>
    /// Gets or sets whether packages from untrusted feeds can be allowed via scoped overrides.
    /// </summary>
    public bool AllowUntrustedWithScopedOverride { get; set; } = false;

    /// <summary>
    /// Gets or sets whether a justification reason is required on untrusted feed overrides.
    /// </summary>
    public bool RequireOverrideReason { get; set; } = true;

    /// <summary>
    /// Gets the list of explicitly configured untrusted feed overrides.
    /// </summary>
    public List<UntrustedFeedOverride> Overrides { get; } = [];

    /// <summary>
    /// Validates that the feed trust policy options are internally consistent.
    /// </summary>
    /// <returns><see langword="true"/> if the options are valid; otherwise <see langword="false"/>.</returns>
    public bool IsValid()
    {
        if (!RequireOverrideReason)
        {
            return true;
        }

        foreach (var item in Overrides)
        {
            if (item.Scope != FeedOverrideScope.None && string.IsNullOrWhiteSpace(item.Reason))
            {
                return false;
            }
        }

        return true;
    }
}
