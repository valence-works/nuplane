namespace Nuplane.Loading;

/// <summary>
/// Non-collectible package graph context for host-integrated package assemblies.
/// </summary>
internal sealed class HostIntegratedPackageGraphLoadContext(
    string graphKey,
    IReadOnlyList<string> mainAssemblyPaths,
    IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
    SharedAssemblyPolicyMatcher matcher)
    : PackageGraphLoadContext(graphKey, mainAssemblyPaths, sharedPolicy, matcher, isCollectible: false);
