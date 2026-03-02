using System;
using System.Collections.Generic;

namespace Nuplane.Runtime.Configuration;

public sealed class SourceTrustOptions
{
    public HashSet<string> AllowedSourceNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> AllowedPackageIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RejectUnallowlistedPackages { get; init; } = true;

    public bool AllowRuntimeCredentialResolution { get; init; } = true;

    public bool IsSourceAllowed(string sourceName)
    {
        if (AllowedSourceNames.Count == 0)
        {
            return true;
        }

        return AllowedSourceNames.Contains(sourceName);
    }

    public bool IsPackageAllowed(string packageId) =>
        !RejectUnallowlistedPackages || AllowedPackageIds.Contains(packageId);
}
