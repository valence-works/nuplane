using System;
using System.Collections.Generic;
using Nuplane.Abstractions;

namespace Nuplane.Runtime.Configuration;

public sealed class FeedTrustPolicyOptions
{
    public bool DefaultRestrictedValidatorRequired { get; set; } = true;

    public bool AllowUntrustedWithScopedOverride { get; set; } = false;

    public bool RequireOverrideReason { get; set; } = true;

    public List<UntrustedFeedOverride> Overrides { get; } = [];

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
