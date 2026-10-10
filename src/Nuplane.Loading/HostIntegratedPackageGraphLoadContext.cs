using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

/// <summary>
/// Non-collectible package graph context for host-integrated package assemblies.
/// </summary>
internal sealed class HostIntegratedPackageGraphLoadContext(
    string graphKey,
    IReadOnlyList<string> mainAssemblyPaths,
    IReadOnlyList<string> packageInstallPaths,
    IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
    SharedAssemblyPolicyMatcher matcher,
    PackageGraphUseLeaseOwner? graphLeaseOwner = null)
    : PackageGraphLoadContext(graphKey, mainAssemblyPaths, packageInstallPaths, sharedPolicy, matcher,
        isCollectible: false, graphLeaseOwner: graphLeaseOwner);
