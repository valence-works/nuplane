using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Selects effective package load modes from loading options.
/// </summary>
internal sealed class PackageLoadModeSelector
{
    private readonly IReadOnlyList<IPackageLoadModeAdvisor> advisors;
    private readonly ILogger<PackageLoadModeSelector> logger;

    public PackageLoadModeSelector(
        IEnumerable<IPackageLoadModeAdvisor>? advisors = null,
        ILogger<PackageLoadModeSelector>? logger = null)
    {
        this.advisors = advisors?.ToArray() ?? [];
        this.logger = logger ?? NullLogger<PackageLoadModeSelector>.Instance;
    }

    /// <summary>
    /// Selects the effective load mode for the specified package.
    /// </summary>
    public PackageLoadModeSelection Select(ResolvedPackage package, LoadingOptions options, string graphKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphKey);

        var packageOverride = options.PackageLoadModes
            .FirstOrDefault(candidate => string.Equals(candidate.PackageId, package.Id, StringComparison.OrdinalIgnoreCase));

        return packageOverride is null
            ? new(package.Id, package.Version, options.DefaultLoadMode, "default", graphKey)
            : new(package.Id, package.Version, packageOverride.LoadMode, "package-override", graphKey);
    }

    public async ValueTask<PackageGraphLoadModeDecision> SelectGraphAsync(
        IReadOnlyList<ResolvedPackage> packages,
        LoadingOptions options,
        string graphKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphKey);

        var packageOverrides = options.PackageLoadModes
            .GroupBy(static packageOverride => packageOverride.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().LoadMode, StringComparer.OrdinalIgnoreCase);

        var advisorResults = new List<LoadModeAdvisorResult>();
        if (options.LoadModeSelectionPolicy == PackageLoadModeSelectionPolicy.Automatic)
        {
            var context = new LoadModeAdvisorContext(
                graphKey,
                packages
                    .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                options.LoadModeSelectionPolicy,
                options.DefaultLoadMode,
                packageOverrides);

            foreach (var advisor in advisors.OrderBy(static advisor => advisor.Name, StringComparer.OrdinalIgnoreCase))
            {
                var results = await advisor.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                advisorResults.AddRange(results);
                logger.LoadModeAdvisorEvaluated(advisor.Name, graphKey, results.Count);
            }
        }

        var resultsByPackage = advisorResults
            .GroupBy(static result => BuildKey(result.PackageId, result.Version), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var decisions = new List<PackageLoadModeDecision>(packages.Count);
        foreach (var package in packages.OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase).ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(package.Id, package.Version);
            resultsByPackage.TryGetValue(key, out var packageResults);
            packageResults ??= [];

            decisions.Add(packageOverrides.TryGetValue(package.Id, out var overrideMode)
                ? SelectOverride(package, graphKey, overrideMode, packageResults)
                : SelectFromAdvisorsOrDefault(package, graphKey, options, packageResults));
        }

        var hasHostMetadata = decisions.Any(static decision =>
            decision.Diagnostics.Any(static diagnostic =>
                diagnostic.ReasonCode == LoadModeReasonCodes.PackageMetadata
                && diagnostic.EffectivePackageLoadMode == PackageLoadMode.HostIntegrated));
        var hasCollectibleMetadata = decisions.Any(static decision =>
            decision.Diagnostics.Any(static diagnostic =>
                diagnostic.ReasonCode == LoadModeReasonCodes.PackageMetadata
                && diagnostic.EffectivePackageLoadMode == PackageLoadMode.Collectible));
        var metadataConflict = hasHostMetadata && hasCollectibleMetadata;

        var graphLoadMode = decisions.Any(static decision => decision.Selection.LoadMode == PackageLoadMode.HostIntegrated)
            ? PackageLoadMode.HostIntegrated
            : PackageLoadMode.Collectible;

        if (metadataConflict)
        {
            graphLoadMode = PackageLoadMode.HostIntegrated;
            logger.PackageLoadMetadataConflict(graphKey, graphLoadMode);
        }

        IReadOnlySet<string> conflictingMetadataKeys = metadataConflict
            ? decisions
                .Where(static decision => decision.Diagnostics.Any(static diagnostic =>
                    diagnostic.ReasonCode == LoadModeReasonCodes.PackageMetadata
                    && (diagnostic.EffectivePackageLoadMode == PackageLoadMode.HostIntegrated
                        || diagnostic.EffectivePackageLoadMode == PackageLoadMode.Collectible)))
                .Select(static decision => BuildKey(decision.Selection.PackageId, decision.Selection.Version))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var promotedDecisions = decisions
            .Select(decision => PromoteDecision(decision, graphKey, graphLoadMode, conflictingMetadataKeys))
            .ToArray();

        logger.GraphLoadModeSelected(graphKey, graphLoadMode);

        return new(
            graphKey,
            graphLoadMode,
            promotedDecisions.Select(static decision => decision.Selection).ToArray(),
            promotedDecisions.ToDictionary(
                static decision => BuildKey(decision.Selection.PackageId, decision.Selection.Version),
                static decision => decision.Diagnostics,
                StringComparer.OrdinalIgnoreCase));
    }

    private PackageLoadModeDecision SelectOverride(
        ResolvedPackage package,
        string graphKey,
        PackageLoadMode loadMode,
        IReadOnlyList<LoadModeAdvisorResult> packageResults)
    {
        var diagnostics = new List<LoadModeDecisionDiagnostic>
        {
            new(
                graphKey,
                loadMode,
                loadMode,
                LoadModeReasonCodes.PackageOverride,
                package.Id,
                package.Version,
                Message: $"Package '{package.Id}@{package.Version}' uses explicit package load mode override.")
        };

        foreach (var result in packageResults)
        {
            if (!result.IsValid)
            {
                diagnostics.Add(CreateAdvisorDiagnostic(graphKey, loadMode, loadMode, result));
                logger.InvalidPackageLoadModeAdvisorResult(package.Id, package.Version, graphKey, result.Diagnostic ?? "Package load-mode advisor result is invalid.");
                continue;
            }

            diagnostics.Add(CreateSuppressedAdvisorDiagnostic(
                graphKey,
                loadMode,
                loadMode,
                result,
                "Package load-mode advisor result was suppressed by an explicit package load mode override."));
            logger.PackageLoadModeAdvisorResultSuppressed(package.Id, package.Version, graphKey);
        }

        return new(new(package.Id, package.Version, loadMode, LoadModeReasonCodes.PackageOverride, graphKey), diagnostics);
    }

    private PackageLoadModeDecision SelectFromAdvisorsOrDefault(
        ResolvedPackage package,
        string graphKey,
        LoadingOptions options,
        IReadOnlyList<LoadModeAdvisorResult> packageResults)
    {
        var diagnostics = new List<LoadModeDecisionDiagnostic>();
        foreach (var invalidResult in packageResults.Where(static result => !result.IsValid))
        {
            diagnostics.Add(CreateAdvisorDiagnostic(graphKey, options.DefaultLoadMode, options.DefaultLoadMode, invalidResult));
            logger.InvalidPackageLoadModeAdvisorResult(package.Id, package.Version, graphKey, invalidResult.Diagnostic ?? "Package load-mode advisor result is invalid.");
        }

        var validResults = packageResults
            .Where(static result => result.IsValid)
            .OrderByDescending(static result => result.RequestedLoadMode == PackageLoadMode.HostIntegrated)
            .ThenBy(static result => result.AdvisorName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.Scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Nuplane currently loads a resolved graph in one mode. Scope is preserved in
        // diagnostics for future policy expansion, while any HostIntegrated metadata
        // requirement promotes the graph to keep activation deterministic.
        var hostIntegratedRequirement = validResults.FirstOrDefault(static result => result.RequestedLoadMode == PackageLoadMode.HostIntegrated);
        if (hostIntegratedRequirement is not null)
        {
            diagnostics.Add(CreateAdvisorDiagnostic(graphKey, PackageLoadMode.HostIntegrated, PackageLoadMode.HostIntegrated, hostIntegratedRequirement));
            return new(
                new(package.Id, package.Version, PackageLoadMode.HostIntegrated, hostIntegratedRequirement.ReasonCode, graphKey),
                diagnostics);
        }

        var collectiblePreference = validResults.FirstOrDefault(static result => result.RequestedLoadMode == PackageLoadMode.Collectible);
        if (collectiblePreference is not null)
        {
            if (options.DefaultLoadMode == PackageLoadMode.Collectible)
            {
                diagnostics.Add(CreateAdvisorDiagnostic(graphKey, PackageLoadMode.Collectible, PackageLoadMode.Collectible, collectiblePreference));
                return new(
                    new(package.Id, package.Version, PackageLoadMode.Collectible, collectiblePreference.ReasonCode, graphKey),
                    diagnostics);
            }

            diagnostics.Add(CreateSuppressedAdvisorDiagnostic(
                graphKey,
                options.DefaultLoadMode,
                options.DefaultLoadMode,
                collectiblePreference,
                "Package metadata requested Collectible but the configured default load mode takes precedence."));
            logger.PackageLoadModeAdvisorResultSuppressed(package.Id, package.Version, graphKey);
        }

        diagnostics.Add(new(
            graphKey,
            options.DefaultLoadMode,
            options.DefaultLoadMode,
            options.LoadModeSelectionPolicy == PackageLoadModeSelectionPolicy.ExplicitOnly
                ? LoadModeReasonCodes.AdvisorsDisabled
                : LoadModeReasonCodes.Default,
            package.Id,
            package.Version,
            Message: options.LoadModeSelectionPolicy == PackageLoadModeSelectionPolicy.ExplicitOnly
                ? "Load mode advisors were disabled by policy."
                : "No advisor or package override selected a load mode."));

        return new(
            new(package.Id, package.Version, options.DefaultLoadMode, LoadModeReasonCodes.Default, graphKey),
            diagnostics);
    }

    private static PackageLoadModeDecision PromoteDecision(
        PackageLoadModeDecision decision,
        string graphKey,
        PackageLoadMode graphLoadMode,
        IReadOnlySet<string> conflictingMetadataKeys)
    {
        var diagnostics = decision.Diagnostics.ToList();
        var selection = decision.Selection;
        if (graphLoadMode == PackageLoadMode.HostIntegrated && selection.LoadMode != PackageLoadMode.HostIntegrated)
        {
            selection = selection with
            {
                LoadMode = PackageLoadMode.HostIntegrated,
                SelectionReason = LoadModeReasonCodes.DependencyClosure
            };

            diagnostics.Add(new(
                graphKey,
                graphLoadMode,
                PackageLoadMode.HostIntegrated,
                LoadModeReasonCodes.DependencyClosure,
                selection.PackageId,
                selection.Version,
                Message: "Package was promoted because another package in the graph required host-integrated loading."));
        }

        if (conflictingMetadataKeys.Contains(BuildKey(selection.PackageId, selection.Version)))
        {
            diagnostics.Add(new(
                graphKey,
                graphLoadMode,
                selection.LoadMode,
                LoadModeReasonCodes.MetadataConflict,
                selection.PackageId,
                selection.Version,
                Message: "Conflicting package metadata was resolved by selecting HostIntegrated for the graph."));
        }

        return decision with
        {
            Selection = selection,
            Diagnostics = diagnostics
                .Select(diagnostic => diagnostic with
                {
                    EffectiveGraphLoadMode = graphLoadMode,
                    EffectivePackageLoadMode = selection.LoadMode
                })
                .ToList()
        };
    }

    private static LoadModeDecisionDiagnostic CreateAdvisorDiagnostic(
        string graphKey,
        PackageLoadMode graphLoadMode,
        PackageLoadMode packageLoadMode,
        LoadModeAdvisorResult result) =>
        new(
            graphKey,
            graphLoadMode,
            packageLoadMode,
            result.ReasonCode,
            result.PackageId,
            result.Version,
            result.Scope,
            result.AdvisorName,
            result.Diagnostic ?? result.Reason);

    private static LoadModeDecisionDiagnostic CreateSuppressedAdvisorDiagnostic(
        string graphKey,
        PackageLoadMode graphLoadMode,
        PackageLoadMode packageLoadMode,
        LoadModeAdvisorResult result,
        string message) =>
        new(
            graphKey,
            graphLoadMode,
            packageLoadMode,
            result.ReasonCode == LoadModeReasonCodes.PackageMetadata
                ? LoadModeReasonCodes.MetadataSuppressed
                : LoadModeReasonCodes.AdvisorSuppressed,
            result.PackageId,
            result.Version,
            result.Scope,
            result.AdvisorName,
            result.ReasonCode == LoadModeReasonCodes.PackageMetadata
                ? message
                : "Package load-mode advisor result was suppressed by a higher-precedence load-mode policy.");

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";
}
