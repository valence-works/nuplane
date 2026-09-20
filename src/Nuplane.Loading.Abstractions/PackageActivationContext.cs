using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Describes the resolved package graph Nuplane is about to activate, for evaluation by an
/// <see cref="IPackageActivationGate"/>.
/// </summary>
/// <param name="GraphKey">
/// The deterministic loading graph key of the graph being activated. It is also the load-context key
/// the graph would receive, so it is stable for the same set of package identities.
/// </param>
/// <param name="LoadMode">
/// The load mode selected for the graph. A graph loads in exactly one mode, so this is the effective
/// mode of every package in <paramref name="Packages"/>.
/// </param>
/// <param name="Packages">
/// Every package in the graph, ordered by package identifier then version. Each entry carries the
/// identifier, version, and install path, which is enough to read package-authored metadata from disk
/// without loading or instantiating any package assembly.
/// </param>
/// <remarks>
/// Nuplane does not distinguish graph roots from their dependencies at this seam: the loader is handed
/// the members of a graph, not the dependency edges between them. A gate that must treat roots
/// differently has to derive that from package metadata it reads itself.
/// </remarks>
public sealed record PackageActivationContext(
    string GraphKey,
    PackageLoadMode LoadMode,
    IReadOnlyList<ResolvedPackage> Packages);
