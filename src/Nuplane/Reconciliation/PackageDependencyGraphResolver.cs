using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>
/// Resolves the dependency closure for desired root packages from installed NuGet metadata.
/// </summary>
public sealed class PackageDependencyGraphResolver
{
    /// <summary>
    /// The stage a package is refused under when it depends on a declared host-provided package the
    /// host carries at a version outside the range the dependency requires.
    /// </summary>
    internal const string HostVersionUnsatisfiedStage = "host-version-unsatisfied";

    private readonly IPackageResolver _packageResolver;
    private readonly IReconciliationRetryPolicy _retryPolicy;
    private readonly HostProvidedPackageDeclarationIndex _hostProvidedPackageDeclarations;
    private readonly IReadOnlyDictionary<string, string>? _hostPackageVersionsOverride;

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageDependencyGraphResolver"/> class.
    /// </summary>
    /// <param name="packageResolver">The resolver used to acquire dependency packages.</param>
    /// <param name="retryPolicy">The retry policy applied to each dependency acquisition.</param>
    /// <param name="hostProvidedPackagesOptions">The host-declared package ids and prefixes, or <see langword="null"/> for the defaults.</param>
    public PackageDependencyGraphResolver(
        IPackageResolver packageResolver,
        IReconciliationRetryPolicy retryPolicy,
        HostProvidedPackagesOptions? hostProvidedPackagesOptions = null)
        : this(packageResolver, retryPolicy, hostProvidedPackagesOptions, hostPackageVersions: null)
    {
    }

    /// <summary>
    /// Initializes a resolver that reads the host's package versions from
    /// <paramref name="hostPackageVersions"/> instead of the process's own <c>*.deps.json</c>, so a
    /// test can state what the host carries rather than depend on what the test host happens to.
    /// </summary>
    internal PackageDependencyGraphResolver(
        IPackageResolver packageResolver,
        IReconciliationRetryPolicy retryPolicy,
        HostProvidedPackagesOptions? hostProvidedPackagesOptions,
        IReadOnlyDictionary<string, string>? hostPackageVersions)
    {
        _packageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _hostProvidedPackageDeclarations =
            HostProvidedPackageDeclarationIndex.Build((IEnumerable<string>?)hostProvidedPackagesOptions?.Entries ?? HostProvidedPackagesOptions.DefaultEntries);
        _hostPackageVersionsOverride = hostPackageVersions;
    }

    private IReadOnlyDictionary<string, string> HostPackageVersionsForResolution =>
        _hostPackageVersionsOverride ?? HostPackageVersions.Value;

    /// <summary>
    /// Resolves desired roots and their package dependencies into deterministic graph records.
    /// </summary>
    /// <param name="desiredRequests">The desired root package requests.</param>
    /// <param name="resolveRootAsync">The callback used to resolve each root package.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved packages and graph metadata.</returns>
    public async Task<PackageDependencyGraphResolutionResult> ResolveAsync(
        IReadOnlyList<PackageRequest> desiredRequests,
        Func<PackageRequest, CancellationToken, Task<ResolvedPackage>> resolveRootAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredRequests);
        ArgumentNullException.ThrowIfNull(resolveRootAsync);

        var resolvedPackages = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var rootPackages = new List<ResolvedPackage>();
        var discoveredEdges = new List<DiscoveredDependencyEdge>();
        var generationId = Guid.NewGuid().ToString("N");

        foreach (var request in desiredRequests.OrderBy(static request => request.Id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootPackage = await resolveRootAsync(request, cancellationToken);
            resolvedPackages[BuildPackageKey(rootPackage.Id, rootPackage.Version)] = rootPackage;
            rootPackages.Add(rootPackage);
        }

        if (rootPackages.Count == 0)
        {
            return new([], []);
        }

        var queue = new Queue<(ResolvedPackage Parent, PackageDependencyMetadata Dependency, IReadOnlyList<string> Path)>();
        var expandedPackageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyKeysByParentKey = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unsatisfiedHostDependencies = new List<(string DependentKey, HostProvidedDependency Dependency)>();
        var unverifiedHostDependencies = new List<HostProvidedDependency>();
        foreach (var rootPackage in rootPackages)
        {
            var rootKey = BuildPackageKey(rootPackage.Id, rootPackage.Version);
            foreach (var dependency in ReadDependencyMetadata(rootPackage))
            {
                queue.Enqueue((rootPackage, dependency, [rootKey]));
            }
        }

        while (queue.Count > 0)
        {
            var (parent, dependency, path) = queue.Dequeue();
            var hostProvision = ClassifyHostProvision(dependency.PackageId, dependency.VersionRange, out var hostVersion);
            if (hostProvision != HostProvision.NotHostProvided)
            {
                // A host-provided dependency is never acquired: either the host's copy satisfies
                // it, or — for a declared package the host carries at an unsatisfying version — the
                // dependent is refused, because the host has said it supplies that package.
                var hostDependency = new HostProvidedDependency(parent.Id, parent.Version, dependency.PackageId, dependency.VersionRange, hostVersion);
                if (hostProvision == HostProvision.Unsatisfied)
                {
                    unsatisfiedHostDependencies.Add((BuildPackageKey(parent.Id, parent.Version), hostDependency));
                }
                else if (hostProvision == HostProvision.Unverified)
                {
                    unverifiedHostDependencies.Add(hostDependency);
                }

                continue;
            }

            var dependencyPackage = FindExistingSatisfyingPackage(resolvedPackages.Values, dependency);
            if (dependencyPackage is null)
            {
                var dependencyRequest = new PackageRequest(
                    dependency.PackageId,
                    dependency.VersionRange,
                    FeedName: null,
                    PackageUpdatePolicy.Exact,
                    $"dependency-of:{parent.Id}");

                dependencyPackage = await _retryPolicy.ExecuteAsync(
                    ct => _packageResolver.ResolveAsync(dependencyRequest, ct),
                    cancellationToken);
                resolvedPackages[BuildPackageKey(dependencyPackage.Id, dependencyPackage.Version)] = dependencyPackage;
            }

            var dependencyKey = BuildPackageKey(dependencyPackage.Id, dependencyPackage.Version);
            if (path.Contains(dependencyKey, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Dependency cycle detected: {FormatCyclePath(path, dependencyKey)}.");
            }

            discoveredEdges.Add(new(parent, dependency));
            AddDependencyKey(dependencyKeysByParentKey, BuildPackageKey(parent.Id, parent.Version), dependencyKey);

            if (!expandedPackageKeys.Add(dependencyKey))
            {
                continue;
            }

            foreach (var transitiveDependency in ReadDependencyMetadata(dependencyPackage))
            {
                queue.Enqueue((dependencyPackage, transitiveDependency, path.Concat([dependencyKey]).ToArray()));
            }
        }

        var selectedPackages = SelectNuGetResolvedPackages(rootPackages, resolvedPackages.Values, cancellationToken);
        var selectedKeys = selectedPackages
            .Select(package => BuildPackageKey(package.Id, package.Version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootKeys = rootPackages
            .Select(package => BuildPackageKey(package.Id, package.Version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nodes = selectedPackages
            .Select(package => CreateNode(package, DetermineNodeRole(package, rootKeys, selectedKeys, discoveredEdges)))
            .OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rootNodes = nodes
            .Where(static node => node.Role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency)
            .OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var orderedEdges = discoveredEdges
            .Where(edge => selectedKeys.Contains(BuildPackageKey(edge.Parent.Id, edge.Parent.Version)))
            .Select(edge => CreateSelectedDependencyEdge(edge, selectedPackages))
            .Distinct()
            .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.SelectedVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var graphId = ResolvedPackageGraph.CreateGraphId(
            TargetFrameworkMonikerProvider.Current,
            rootNodes,
            nodes,
            orderedEdges,
            []);
        var graph = new ResolvedPackageGraph(
            graphId,
            generationId,
            TargetFrameworkMonikerProvider.Current,
            rootNodes,
            nodes,
            orderedEdges,
            [],
            DateTimeOffset.UtcNow);

        return new PackageDependencyGraphResolutionResult(
            selectedPackages
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            [graph])
        {
            HostVersionRefusals = CreateHostVersionRefusals(rootPackages, dependencyKeysByParentKey, unsatisfiedHostDependencies),
            UnverifiedHostProvidedDependencies = unverifiedHostDependencies
                .Distinct()
                .OrderBy(static dependency => dependency.DependentPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static dependency => dependency.DependencyId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void AddDependencyKey(Dictionary<string, HashSet<string>> dependencyKeysByParentKey, string parentKey, string dependencyKey)
    {
        if (!dependencyKeysByParentKey.TryGetValue(parentKey, out var dependencyKeys))
        {
            dependencyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dependencyKeysByParentKey[parentKey] = dependencyKeys;
        }

        dependencyKeys.Add(dependencyKey);
    }

    // Each unsatisfied host dependency refuses the package that declares it and every root whose
    // closure reaches that package: the graph is all-or-nothing at activation time, so a root that
    // needs a refused package cannot be applied either. Reachability is read from the edges the
    // expansion discovered rather than from the path the refusal was found on, because a package
    // is expanded once however many roots reach it.
    private static IReadOnlyList<HostVersionRefusal> CreateHostVersionRefusals(
        IReadOnlyList<ResolvedPackage> rootPackages,
        IReadOnlyDictionary<string, HashSet<string>> dependencyKeysByParentKey,
        IReadOnlyList<(string DependentKey, HostProvidedDependency Dependency)> unsatisfiedHostDependencies)
    {
        if (unsatisfiedHostDependencies.Count == 0)
        {
            return [];
        }

        var reachableKeysByRoot = rootPackages
            .Select(root => (root.Id, ReachableKeys: CollectReachableKeys(BuildPackageKey(root.Id, root.Version), dependencyKeysByParentKey)))
            .ToArray();

        return unsatisfiedHostDependencies
            .Distinct()
            .Select(unsatisfied => new HostVersionRefusal(
                unsatisfied.Dependency,
                reachableKeysByRoot
                    .Where(root => root.ReachableKeys.Contains(unsatisfied.DependentKey))
                    .Select(static root => root.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(static refusal => refusal.Dependency.DependentPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static refusal => refusal.Dependency.DependencyId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> CollectReachableKeys(string rootKey, IReadOnlyDictionary<string, HashSet<string>> dependencyKeysByParentKey)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootKey };
        var pending = new Stack<string>([rootKey]);
        while (pending.TryPop(out var key))
        {
            if (!dependencyKeysByParentKey.TryGetValue(key, out var dependencyKeys))
            {
                continue;
            }

            foreach (var dependencyKey in dependencyKeys.Where(reachable.Add))
            {
                pending.Push(dependencyKey);
            }
        }

        return reachable;
    }

    private IReadOnlyList<ResolvedPackage> SelectNuGetResolvedPackages(
        IReadOnlyList<ResolvedPackage> rootPackages,
        IEnumerable<ResolvedPackage> candidatePackages,
        CancellationToken cancellationToken)
    {
        var source = new SourceRepository(
            new PackageSource("https://nuplane.local/aggregate", "nuplane-aggregate"),
            Enumerable.Empty<INuGetResourceProvider>());
        var orderedCandidatePackages = candidatePackages
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => TryParsePackageVersion(package.Version, out var version) ? version : NuGetVersion.Parse("0.0.0"))
            .ThenBy(static package => package.FeedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packagesByKey = orderedCandidatePackages
            .GroupBy(static package => BuildNormalizedPackageKey(package.Id, package.Version), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var availablePackages = orderedCandidatePackages
            .GroupBy(static package => BuildNormalizedPackageKey(package.Id, package.Version), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(package => new SourcePackageDependencyInfo(
                package.Id,
                ParsePackageVersion(package),
                ReadDependencyMetadata(package)
                    .Where(dependency => ClassifyHostProvision(dependency.PackageId, dependency.VersionRange, out _) == HostProvision.NotHostProvided)
                    .Select(static dependency => TryCreatePackageDependency(dependency))
                    .OfType<PackageDependency>()
                    .ToArray(),
                listed: true,
                source))
            .ToArray();
        var targetIds = rootPackages
            .Select(static package => package.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preferredVersions = rootPackages
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => TryParsePackageVersion(package.Version, out var version) ? version : NuGetVersion.Parse("0.0.0"))
            .Select(static package => new PackageIdentity(package.Id, ParsePackageVersion(package)))
            .ToArray();
        var context = new PackageResolverContext(
            DependencyBehavior.Lowest,
            targetIds,
            requiredPackageIds: targetIds,
            packagesConfig: [],
            preferredVersions,
            availablePackages,
            [source.PackageSource],
            NullLogger.Instance);

        return new PackageResolver()
            .Resolve(context, cancellationToken)
            .Select(identity => packagesByKey.TryGetValue(BuildNormalizedPackageKey(identity.Id, identity.Version.ToNormalizedString()), out var package)
                ? package
                : throw new InvalidOperationException($"NuGet selected package '{identity.Id}@{identity.Version.ToNormalizedString()}' but no resolved package candidate was available."))
            .ToArray();
    }

    private static NuGetVersion ParsePackageVersion(ResolvedPackage package) =>
        TryParsePackageVersion(package.Version, out var version)
            ? version
            : throw new InvalidOperationException($"Resolved package '{package.Id}' has invalid NuGet version '{package.Version}'.");

    private static bool TryParsePackageVersion(string version, out NuGetVersion parsedVersion) =>
        NuGetVersion.TryParse(version, out parsedVersion!);

    private static string BuildNormalizedPackageKey(string packageId, string version) =>
        BuildPackageKey(packageId, TryParsePackageVersion(version, out var parsedVersion) ? parsedVersion.ToNormalizedString() : version);

    private static PackageDependency? TryCreatePackageDependency(PackageDependencyMetadata dependency) =>
        VersionRange.TryParse(dependency.VersionRange, out var range)
            ? new PackageDependency(dependency.PackageId, range)
            : null;

    private static PackageNodeRole DetermineNodeRole(
        ResolvedPackage package,
        ISet<string> rootKeys,
        ISet<string> selectedKeys,
        IReadOnlyList<DiscoveredDependencyEdge> discoveredEdges)
    {
        var key = BuildPackageKey(package.Id, package.Version);
        var root = rootKeys.Contains(key);
        var dependency = discoveredEdges.Any(edge =>
            selectedKeys.Contains(BuildPackageKey(edge.Parent.Id, edge.Parent.Version)) &&
            string.Equals(edge.Dependency.PackageId, package.Id, StringComparison.OrdinalIgnoreCase) &&
            VersionSatisfiesRange(package.Version, edge.Dependency.VersionRange));

        return root && dependency
            ? PackageNodeRole.RootAndDependency
            : root
                ? PackageNodeRole.Root
                : PackageNodeRole.Dependency;
    }

    private static DependencyEdge CreateSelectedDependencyEdge(
        DiscoveredDependencyEdge edge,
        IReadOnlyList<ResolvedPackage> selectedPackages)
    {
        if (!VersionRange.TryParse(edge.Dependency.VersionRange, out var requestedRange))
        {
            throw new InvalidOperationException(
                $"Dependency '{edge.Parent.Id}@{edge.Parent.Version}' declares invalid version range '{edge.Dependency.VersionRange}' for '{edge.Dependency.PackageId}'.");
        }

        var selectedDependency = selectedPackages
            .Where(package => string.Equals(package.Id, edge.Dependency.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(package => new
            {
                Package = package,
                Version = NuGetVersion.Parse(package.Version)
            })
            .Where(candidate => requestedRange.Satisfies(candidate.Version))
            .OrderBy(static candidate => candidate.Version)
            .FirstOrDefault();

        if (selectedDependency is null)
        {
            throw new InvalidOperationException(
                $"Resolved graph did not contain a selected package for dependency '{edge.Parent.Id}@{edge.Parent.Version}' -> '{edge.Dependency.PackageId} {edge.Dependency.VersionRange}'.");
        }

        return new DependencyEdge(
            edge.Parent.Id,
            edge.Parent.Version,
            selectedDependency.Package.Id,
            edge.Dependency.VersionRange,
            selectedDependency.Package.Version,
            edge.Dependency.TargetFramework ?? string.Empty,
            Optional: false);
    }

    private static ResolvedPackageNode CreateNode(
        ResolvedPackage package,
        PackageNodeRole role) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.RemoteFeed,
            string.IsNullOrWhiteSpace(package.SourceName) ? package.FeedName : package.SourceName,
            PackageContentHash: null,
            RuntimeAssets: ResolveRuntimeAssets(package.InstallPath),
            DiscoverableAssets: role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency ? ResolveRuntimeAssets(package.InstallPath) : [],
            SupportAssets: role is PackageNodeRole.Dependency ? ResolveRuntimeAssets(package.InstallPath) : []);

    private static IReadOnlyList<string> ResolveRuntimeAssets(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(installPath, "*.dll", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(installPath, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PackageDependencyMetadata> ReadDependencyMetadata(ResolvedPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.InstallPath) || !Directory.Exists(package.InstallPath))
        {
            return [];
        }

        var nuspecPath = Directory
            .EnumerateFiles(package.InstallPath, "*.nuspec", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (nuspecPath is null)
        {
            return [];
        }

        using var stream = File.OpenRead(nuspecPath);
        var document = XDocument.Load(stream);

        var groups = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "group" && element.Parent?.Name.LocalName == "dependencies")
            .Select(static group => new DependencyGroupMetadata(
                group.Attribute("targetFramework")?.Value,
                group.Elements()
                    .Where(static element => element.Name.LocalName == "dependency")
                    .Select(element => new PackageDependencyMetadata(
                        element.Attribute("id")?.Value ?? string.Empty,
                        NormalizeDependencyVersionRange(element.Attribute("version")?.Value ?? string.Empty),
                        group.Attribute("targetFramework")?.Value))
                    .ToArray()))
            .ToArray();

        var selectedGroups = SelectDependencyGroups(groups);
        if (selectedGroups.Count > 0)
        {
            return selectedGroups
                .SelectMany(static group => group.Dependencies)
                .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                    && !string.IsNullOrWhiteSpace(dependency.VersionRange))
                .OrderBy(static dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return document
            .Descendants()
            .Where(static element => element.Name.LocalName == "dependency" && element.Parent?.Name.LocalName == "dependencies")
            .Select(static element => new PackageDependencyMetadata(
                element.Attribute("id")?.Value ?? string.Empty,
                NormalizeDependencyVersionRange(element.Attribute("version")?.Value ?? string.Empty),
                TargetFramework: null))
            .Where(static dependency => !string.IsNullOrWhiteSpace(dependency.PackageId)
                && !string.IsNullOrWhiteSpace(dependency.VersionRange))
            .OrderBy(static dependency => dependency.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDependencyVersionRange(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return versionRange;
        }

        var trimmed = versionRange.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
        {
            return trimmed;
        }

        return NuGetVersion.TryParse(trimmed, out var version)
            ? $"[{version.ToNormalizedString()},)"
            : trimmed;
    }

    private static ResolvedPackage? FindExistingSatisfyingPackage(
        IEnumerable<ResolvedPackage> packages,
        PackageDependencyMetadata dependency)
    {
        foreach (var package in packages.Where(package => string.Equals(package.Id, dependency.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            if (VersionSatisfiesRange(package.Version, dependency.VersionRange))
            {
                return package;
            }
        }

        return null;
    }

    private static IReadOnlyList<DependencyGroupMetadata> SelectDependencyGroups(IReadOnlyList<DependencyGroupMetadata> groups)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        if (!TryParseNuGetFramework(TargetFrameworkMonikerProvider.Current, out var hostTarget))
        {
            return groups.Where(static group => string.IsNullOrWhiteSpace(group.TargetFramework)).ToArray();
        }

        var parsedGroups = groups
            .Select(group => new
            {
                Group = group,
                Parsed = TryParseNuGetFramework(group.TargetFramework, out var target) ? target : null
            })
            .Where(static candidate => candidate.Parsed is not null)
            .ToArray();

        var nearest = new FrameworkReducer().GetNearest(hostTarget, parsedGroups.Select(static candidate => candidate.Parsed!));
        if (nearest is not null)
        {
            return parsedGroups
                .Where(candidate => NuGetFrameworkFullComparer.Instance.Equals(candidate.Parsed, nearest))
                .Select(static candidate => candidate.Group)
                .Take(1)
                .ToArray();
        }

        return groups.Where(static group => string.IsNullOrWhiteSpace(group.TargetFramework)).ToArray();
    }

    private static bool TryParseNuGetFramework(string? value, out NuGetFramework framework)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            framework = null!;
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Contains(",Version=", StringComparison.OrdinalIgnoreCase))
        {
            framework = NuGetFramework.ParseFrameworkName(normalized, DefaultFrameworkNameProvider.Instance);
            return !framework.IsUnsupported;
        }

        framework = NuGetFramework.ParseFolder(NormalizeFrameworkFolderName(normalized));
        return !framework.IsUnsupported;
    }

    private static string NormalizeFrameworkFolderName(string value)
    {
        if (value.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            return "netstandard" + value[".NETStandard".Length..];
        }

        if (value.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            return "netcoreapp" + value[".NETCoreApp".Length..];
        }

        return value;
    }

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";

    private static string FormatCyclePath(IReadOnlyList<string> path, string repeatedKey)
    {
        var cycleStartIndex = path
            .Select((key, index) => new { key, index })
            .First(item => string.Equals(item.key, repeatedKey, StringComparison.OrdinalIgnoreCase))
            .index;

        return string.Join(" -> ", path.Skip(cycleStartIndex).Concat([repeatedKey]));
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> HostPackageVersions = new(LoadHostPackageVersions);

    // A dependency is host-provided when the host's *.deps.json carries it at a satisfying version,
    // or when the host declares it. Only a declared package is held to the host's version: the
    // declaration says the host supplies it, so a host version outside the range refuses the
    // dependent, and a declared package the host's version map does not carry has nothing to check
    // against and is trusted, which the caller reports rather than leaves silent. An undeclared
    // package the host carries at an unsatisfying version is not host-provided at all: it is
    // acquired like any other dependency, and isolated loading gives the package a private copy.
    private HostProvision ClassifyHostProvision(string packageId, string versionRange, out string? hostVersion)
    {
        hostVersion = null;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return HostProvision.NotHostProvided;
        }

        var declared = _hostProvidedPackageDeclarations.Matches(packageId);
        if (HostPackageVersionsForResolution.TryGetValue(packageId, out hostVersion))
        {
            if (VersionSatisfiesRange(hostVersion, versionRange))
            {
                return HostProvision.Satisfied;
            }

            return declared ? HostProvision.Unsatisfied : HostProvision.NotHostProvided;
        }

        return declared ? HostProvision.Unverified : HostProvision.NotHostProvided;
    }

    private static bool VersionSatisfiesRange(string version, string versionRange)
    {
        if (!NuGetVersion.TryParse(version, out var parsedVersion))
        {
            return false;
        }

        if (NuGetVersion.TryParse(versionRange, out _) &&
            !versionRange.StartsWith('[') &&
            !versionRange.StartsWith('('))
        {
            versionRange = $"[{versionRange}]";
        }

        return VersionRange.TryParse(versionRange, out var range) && range.Satisfies(parsedVersion);
    }

    private static IReadOnlyDictionary<string, string> LoadHostPackageVersions()
    {
        var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var depsFile in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.deps.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(depsFile));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                var separatorIndex = library.Name.LastIndexOf('/');
                if (separatorIndex <= 0 || separatorIndex == library.Name.Length - 1)
                {
                    continue;
                }

                var packageId = library.Name[..separatorIndex];
                var version = library.Name[(separatorIndex + 1)..];
                if (!NuGetVersion.TryParse(version, out var parsedVersion))
                {
                    continue;
                }

                if (!packageVersions.TryGetValue(packageId, out var existingVersion) ||
                    !NuGetVersion.TryParse(existingVersion, out var parsedExistingVersion) ||
                    parsedVersion > parsedExistingVersion)
                {
                    packageVersions[packageId] = parsedVersion.ToNormalizedString();
                }
            }
        }

        return packageVersions;
    }

    private enum HostProvision
    {
        NotHostProvided,
        Satisfied,
        Unsatisfied,
        Unverified
    }

    private sealed record DiscoveredDependencyEdge(ResolvedPackage Parent, PackageDependencyMetadata Dependency);

    private sealed record DependencyGroupMetadata(string? TargetFramework, IReadOnlyList<PackageDependencyMetadata> Dependencies);

    private sealed record PackageDependencyMetadata(string PackageId, string VersionRange, string? TargetFramework);

    /// <summary>
    /// A matcher built once from <see cref="HostProvidedPackagesOptions.Entries"/>: exact package ids
    /// and <c>Prefix.</c>-style prefixes, both matched case-insensitively. Duplicate entries collapse
    /// to one, so a host that lists the same id or prefix twice sees no different a result than
    /// listing it once.
    /// </summary>
    private sealed class HostProvidedPackageDeclarationIndex
    {
        private readonly IReadOnlySet<string> _exactIds;
        private readonly IReadOnlyList<string> _prefixes;

        private HostProvidedPackageDeclarationIndex(IReadOnlySet<string> exactIds, IReadOnlyList<string> prefixes)
        {
            _exactIds = exactIds;
            _prefixes = prefixes;
        }

        internal static HostProvidedPackageDeclarationIndex Build(IEnumerable<string> entries)
        {
            var exactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefixes = new List<string>();
            var seenPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var trimmed = entry.Trim();
                if (trimmed.EndsWith('.'))
                {
                    if (seenPrefixes.Add(trimmed))
                    {
                        prefixes.Add(trimmed);
                    }
                }
                else
                {
                    exactIds.Add(trimmed);
                }
            }

            return new(exactIds, prefixes);
        }

        internal bool Matches(string packageId) =>
            _exactIds.Contains(packageId) ||
            _prefixes.Any(prefix => packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Contains dependency-graph resolution output for a reconciliation cycle.
/// </summary>
/// <param name="ResolvedPackages">All resolved graph packages.</param>
/// <param name="ResolvedGraphs">The resolved dependency graphs.</param>
public sealed record PackageDependencyGraphResolutionResult(
    IReadOnlyList<ResolvedPackage> ResolvedPackages,
    IReadOnlyList<ResolvedPackageGraph> ResolvedGraphs)
{
    /// <summary>
    /// The dependencies on a declared host-provided package whose host version does not satisfy the
    /// range they require, each with the roots that cannot be applied because of it.
    /// </summary>
    internal IReadOnlyList<HostVersionRefusal> HostVersionRefusals { get; init; } = [];

    /// <summary>
    /// The dependencies on a declared host-provided package whose version the host's package map
    /// does not carry, trusted as satisfied because there is nothing to compare their range with.
    /// </summary>
    internal IReadOnlyList<HostProvidedDependency> UnverifiedHostProvidedDependencies { get; init; } = [];
}

/// <summary>
/// One dependency edge onto a package the host provides.
/// </summary>
/// <param name="DependentPackageId">The identifier of the package that declares the dependency.</param>
/// <param name="DependentPackageVersion">The version of the package that declares the dependency.</param>
/// <param name="DependencyId">The host-provided package's identifier.</param>
/// <param name="RequiredRange">The version range the dependency requires.</param>
/// <param name="HostVersion">The version the host carries, or <see langword="null"/> when it is not known.</param>
internal sealed record HostProvidedDependency(
    string DependentPackageId,
    string DependentPackageVersion,
    string DependencyId,
    string RequiredRange,
    string? HostVersion);

/// <summary>
/// A dependency on a declared host-provided package the host carries at a version outside the
/// range it requires, and the roots that cannot be applied because their closure contains its dependent.
/// </summary>
/// <param name="Dependency">The unsatisfied dependency.</param>
/// <param name="RootPackageIds">The identifiers of the roots whose closure contains the dependent package, which includes the dependent itself when it is a root.</param>
internal sealed record HostVersionRefusal(HostProvidedDependency Dependency, IReadOnlyList<string> RootPackageIds)
{
    /// <summary>
    /// The refusal message: the dependent package, the dependency, the range it requires, and the
    /// version the host actually carries.
    /// </summary>
    public string Message =>
        $"Package '{Dependency.DependentPackageId}@{Dependency.DependentPackageVersion}' requires '{Dependency.DependencyId} {Dependency.RequiredRange}', " +
        $"but the host provides '{Dependency.DependencyId} {Dependency.HostVersion}'. A package the host declares it provides is never acquired, " +
        "so the dependent is refused rather than loaded against a version it was not built for.";
}

internal static class TargetFrameworkMonikerProvider
{
    public static string Current { get; } = typeof(PackageDependencyGraphResolver).Assembly
        .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
        .OfType<TargetFrameworkAttribute>()
        .FirstOrDefault()
        ?.FrameworkName ?? ".NETCoreApp";
}
