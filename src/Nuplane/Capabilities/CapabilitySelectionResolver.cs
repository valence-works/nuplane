using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Versioning;

namespace Nuplane.Capabilities;

/// <summary>
/// Turns package capability declarations, a host's configured selections, and the desired-state
/// roots a host already named explicitly into the root packages a selection injects and the
/// refusals a bad selection or a bad declaration produces. Pure: no I/O, no clock, no randomness,
/// and — the same inputs given in any order — byte-identical output.
/// </summary>
/// <remarks>
/// <para>
/// Every capability name found in either <c>declarationsByPackage</c> or <c>selections</c> is
/// processed independently, in this order, for each capability:
/// </para>
/// <list type="number">
/// <item><description>
/// Conflicting declarations — a declared option whose <c>packageId</c> disagrees across the
/// declaring packages, or whose declared version disagrees without a host <c>Version</c> override —
/// refuse the capability (<see cref="CapabilityRefusalStage.Conflict"/>) on every package that
/// declares it. Nothing further runs for this capability.
/// </description></item>
/// <item><description>
/// Explicit-root satisfaction — an explicit desired root whose id equals a declared option's
/// <c>packageId</c> satisfies that option, unless its version request does not satisfy the option's
/// effective range, which is also a <see cref="CapabilityRefusalStage.Conflict"/> refusal and also
/// stops the capability.
/// </description></item>
/// <item><description>
/// No selection: satisfied by an explicit root (a diagnostic, no injection) or refused as
/// <see cref="CapabilityRefusalStage.Unselected"/> naming the configuration key.
/// </description></item>
/// <item><description>
/// A selection naming an option the capability does not declare refuses it as
/// <see cref="CapabilityRefusalStage.UnknownOption"/>.
/// </description></item>
/// <item><description>
/// Each selected option not already satisfied by an explicit root is injected as one
/// <see cref="PackageRequest"/>.
/// </description></item>
/// <item><description>
/// When <c>requirePinned</c> and an injection's effective range is not a single pinned version
/// (<see cref="NuGetVersionRequestClassifier"/>), that one injection is dropped and refused as
/// <see cref="CapabilityRefusalStage.Unpinned"/> instead; other options of the same capability are
/// unaffected.
/// </description></item>
/// </list>
/// <para>
/// Determinism does not depend on the order of <c>declarationsByPackage</c>, <c>selections</c>, or
/// <c>explicitRoots</c>: capability and option names are canonicalized to the ordinally-smallest
/// casing observed across every source (a pure function of the set of casings, not of which one was
/// seen first), and every collection this resolver walks is sorted before use.
/// </para>
/// </remarks>
internal static class CapabilitySelectionResolver
{
    internal static CapabilityResolution Resolve(
        IReadOnlyList<CapabilityDeclaringPackage> declarationsByPackage,
        IReadOnlyDictionary<string, CapabilitySelection> selections,
        IReadOnlyList<PackageRequest> explicitRoots,
        bool requirePinned)
    {
        ArgumentNullException.ThrowIfNull(declarationsByPackage);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(explicitRoots);

        var capabilityNameFold = CanonicalizeNames(
            declarationsByPackage.SelectMany(static package => package.Declarations.Select(static d => d.Name))
                .Concat(selections.Keys));

        var declarationsByCapability = GroupDeclarationsByCanonicalCapabilityName(declarationsByPackage, capabilityNameFold);
        var selectionsByCapability = GroupSelectionsByCanonicalCapabilityName(selections, capabilityNameFold);

        var capabilityNames = declarationsByCapability.Keys
            .Concat(selectionsByCapability.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        var injections = new List<PackageRequest>();
        var refusals = new List<CapabilityRefusal>();
        var diagnostics = new List<string>();

        foreach (var capabilityName in capabilityNames)
        {
            ResolveCapability(
                capabilityName,
                declarationsByCapability.GetValueOrDefault(capabilityName, []),
                selectionsByCapability.GetValueOrDefault(capabilityName),
                explicitRoots,
                requirePinned,
                injections,
                refusals,
                diagnostics);
        }

        return new(injections, refusals, diagnostics);
    }

    private static void ResolveCapability(
        string capabilityName,
        IReadOnlyList<(CapabilityDeclaringPackage Package, PackageCapabilityDeclaration Declaration)> declPairs,
        CapabilitySelection? selection,
        IReadOnlyList<PackageRequest> explicitRoots,
        bool requirePinned,
        List<PackageRequest> injections,
        List<CapabilityRefusal> refusals,
        List<string> diagnostics)
    {
        var configurationKey = $"Nuplane:Capabilities:{capabilityName}";

        if (declPairs.Count == 0)
        {
            // A selection that matches no declaration in this cycle: nothing to refuse (no package is
            // broken by it), but a typo in the configured name should still be visible.
            diagnostics.Add($"{configurationKey} selects a capability that no package in this cycle declares.");
            return;
        }

        var orderedDeclPairs = declPairs
            .OrderBy(static p => p.Package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.Package.PackageVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var declaringPackageIds = orderedDeclPairs
            .Select(static p => p.Package.Identity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        var optionNameFold = CanonicalizeNames(orderedDeclPairs.SelectMany(static p => p.Declaration.Options.Select(static o => o.Name)));

        // Rule 1: every declaring package must agree on packageId (always) and version (unless a
        // host Version override replaces it) for each declared option.
        var resolvedOptions = new Dictionary<string, (PackageCapabilityOption Option, List<string> DeclaringPackageIds)>(StringComparer.Ordinal);
        foreach (var optionName in optionNameFold.Values.Distinct(StringComparer.Ordinal).OrderBy(static n => n, StringComparer.Ordinal))
        {
            var declarations = orderedDeclPairs
                .SelectMany(p => p.Declaration.Options
                    .Where(o => string.Equals(optionNameFold.GetValueOrDefault(o.Name, o.Name), optionName, StringComparison.Ordinal))
                    .Select(o => (Option: o, Package: p.Package)))
                .ToArray();

            var distinctPackageIds = declarations.Select(static d => d.Option.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinctPackageIds.Length > 1)
            {
                refusals.Add(new(
                    capabilityName,
                    CapabilityRefusalStage.Conflict,
                    $"Capability '{capabilityName}' option '{optionName}' is declared with conflicting packageId " +
                    $"({string.Join(", ", distinctPackageIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).Select(id => $"'{id}'"))}) " +
                    $"across {string.Join(", ", declaringPackageIds.Select(id => $"'{id}'"))}; " +
                    "a capability option's packageId must agree across every package that declares it.",
                    declaringPackageIds));
                return;
            }

            var distinctVersions = declarations.Select(static d => d.Option.VersionRange).Distinct(StringComparer.Ordinal).ToArray();
            var hasOverride = selection?.Version is not null;
            if (distinctVersions.Length > 1 && !hasOverride)
            {
                refusals.Add(new(
                    capabilityName,
                    CapabilityRefusalStage.Conflict,
                    $"Capability '{capabilityName}' option '{optionName}' is declared with conflicting version " +
                    $"({string.Join(", ", distinctVersions.OrderBy(static v => v, StringComparer.Ordinal).Select(v => $"'{v}'"))}) " +
                    $"across {string.Join(", ", declaringPackageIds.Select(id => $"'{id}'"))}; " +
                    $"set {configurationKey}:Version to override, or align the declared versions.",
                    declaringPackageIds));
                return;
            }

            var representative = declarations[0].Option;
            resolvedOptions[optionName] = (representative, declarations.Select(static d => d.Package.Identity).Distinct(StringComparer.Ordinal).ToList());
        }

        // Rule 2: an explicit root whose id names a declared option's package must satisfy that
        // option's effective range, or the capability is refused rather than silently overridden.
        var satisfiedByExplicitRoot = new Dictionary<string, PackageRequest>(StringComparer.Ordinal);
        foreach (var (optionName, (option, _)) in resolvedOptions.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            var matchingRoot = explicitRoots.FirstOrDefault(root => string.Equals(root.Id, option.PackageId, StringComparison.OrdinalIgnoreCase));
            if (matchingRoot is null)
            {
                continue;
            }

            var effectiveRange = selection?.Version ?? option.VersionRange;
            if (!ExplicitRootSatisfies(effectiveRange, matchingRoot.VersionRange))
            {
                refusals.Add(new(
                    capabilityName,
                    CapabilityRefusalStage.Conflict,
                    $"Explicit root '{option.PackageId} {matchingRoot.VersionRange}' ({matchingRoot.SourceName}) does not satisfy " +
                    $"capability '{capabilityName}' option '{optionName}' '{effectiveRange}' declared by " +
                    $"{string.Join(", ", declaringPackageIds.Select(id => $"'{id}'"))}.",
                    declaringPackageIds));
                return;
            }

            satisfiedByExplicitRoot[optionName] = matchingRoot;
        }

        if (selection is null)
        {
            if (satisfiedByExplicitRoot.Count > 0)
            {
                foreach (var (_, root) in satisfiedByExplicitRoot.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                {
                    diagnostics.Add($"Capability '{capabilityName}' satisfied by explicit root '{root.Id}'.");
                }

                return;
            }

            var declaredOptionNames = resolvedOptions.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToArray();
            var subject = declaringPackageIds.Length == 1
                ? $"Package '{declaringPackageIds[0]}' declares"
                : $"Packages {string.Join(", ", declaringPackageIds.Select(id => $"'{id}'"))} declare";
            refusals.Add(new(
                capabilityName,
                CapabilityRefusalStage.Unselected,
                $"{subject} capability '{capabilityName}' with options {string.Join(", ", declaredOptionNames)}, " +
                "but the host selects none of them and none of their packages is a desired root. " +
                $"Set {configurationKey} to one of those options (or add one of their packages as an explicit root).",
                declaringPackageIds));
            return;
        }

        var selectedOptionNames = selection.Options
            .Select(o => optionNameFold.GetValueOrDefault(o, o))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        var declaredOptionNamesSet = new HashSet<string>(resolvedOptions.Keys, StringComparer.Ordinal);
        var unknownOptionNames = selectedOptionNames.Where(n => !declaredOptionNamesSet.Contains(n)).ToArray();
        if (unknownOptionNames.Length > 0)
        {
            var declaredOptionNames = resolvedOptions.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToArray();
            refusals.Add(new(
                capabilityName,
                CapabilityRefusalStage.UnknownOption,
                $"Capability '{capabilityName}' selection names option(s) {string.Join(", ", unknownOptionNames.Select(n => $"'{n}'"))}, " +
                $"which capability '{capabilityName}' does not declare; declared options are {string.Join(", ", declaredOptionNames)}. " +
                $"Declared by {string.Join(", ", declaringPackageIds.Select(id => $"'{id}'"))}.",
                declaringPackageIds));
            return;
        }

        foreach (var optionName in selectedOptionNames)
        {
            if (satisfiedByExplicitRoot.ContainsKey(optionName))
            {
                continue;
            }

            var (option, optionDeclaringPackageIds) = resolvedOptions[optionName];
            var effectiveRange = selection.Version ?? option.VersionRange;
            var classification = NuGetVersionRequestClassifier.Classify(effectiveRange);

            if (requirePinned && !classification.IsExact)
            {
                refusals.Add(new(
                    capabilityName,
                    CapabilityRefusalStage.Unpinned,
                    $"Capability '{capabilityName}' option '{optionName}' resolves to version range '{effectiveRange}', " +
                    "which is not a single pinned version; restoring with RequirePinnedVersions demands single-point pins. " +
                    $"Declared by {string.Join(", ", optionDeclaringPackageIds.OrderBy(static id => id, StringComparer.Ordinal).Select(id => $"'{id}'"))}.",
                    optionDeclaringPackageIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray()));
                continue;
            }

            injections.Add(new PackageRequest(
                option.PackageId,
                effectiveRange,
                selection.Feed,
                classification.IsExact ? PackageUpdatePolicy.Exact : PackageUpdatePolicy.Range,
                $"capability:{capabilityName}={optionName}"));
        }
    }

    /// <summary>
    /// Whether an explicit root's requested version satisfies <paramref name="effectiveRange"/>. An
    /// explicit request that classifies as an exact version is checked directly; a request this
    /// resolver cannot classify as exact (a range, a floating request, or "latest") is treated as
    /// compatible rather than refused, since satisfiability between two ranges — as opposed to a
    /// concrete version and a range — is not the scenario the design's conflict rule describes.
    /// </summary>
    private static bool ExplicitRootSatisfies(string effectiveRange, string explicitVersionRequest)
    {
        if (!VersionRange.TryParse(effectiveRange, out var effective))
        {
            return true;
        }

        var classified = NuGetVersionRequestClassifier.Classify(explicitVersionRequest);
        if (!classified.IsExact || !NuGetVersion.TryParse(classified.ExactVersion, out var exactVersion))
        {
            return true;
        }

        return effective.Satisfies(exactVersion);
    }

    private static Dictionary<string, List<(CapabilityDeclaringPackage Package, PackageCapabilityDeclaration Declaration)>>
        GroupDeclarationsByCanonicalCapabilityName(
            IReadOnlyList<CapabilityDeclaringPackage> declarationsByPackage,
            IReadOnlyDictionary<string, string> nameFold)
    {
        var groups = new Dictionary<string, List<(CapabilityDeclaringPackage, PackageCapabilityDeclaration)>>(StringComparer.Ordinal);
        foreach (var package in declarationsByPackage)
        {
            foreach (var declaration in package.Declarations)
            {
                var canonical = nameFold[declaration.Name];
                if (!groups.TryGetValue(canonical, out var list))
                {
                    list = [];
                    groups[canonical] = list;
                }

                list.Add((package, declaration));
            }
        }

        return groups;
    }

    private static Dictionary<string, CapabilitySelection> GroupSelectionsByCanonicalCapabilityName(
        IReadOnlyDictionary<string, CapabilitySelection> selections,
        IReadOnlyDictionary<string, string> nameFold)
    {
        var result = new Dictionary<string, CapabilitySelection>(StringComparer.Ordinal);
        foreach (var (name, selection) in selections)
        {
            result[nameFold[name]] = selection;
        }

        return result;
    }

    /// <summary>
    /// Maps every casing variant of every case-insensitively-equal name in <paramref name="names"/>
    /// to one canonical casing: the ordinally-smallest variant observed. This is a pure function of
    /// the set of casings seen, not of which one was encountered first, which is what makes
    /// <see cref="Resolve"/>'s output independent of input order when two sources spell the same
    /// capability or option name differently.
    /// </summary>
    private static Dictionary<string, string> CanonicalizeNames(IEnumerable<string> names)
    {
        var variantsByFold = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!variantsByFold.TryGetValue(name, out var variants))
            {
                variants = new SortedSet<string>(StringComparer.Ordinal);
                variantsByFold[name] = variants;
            }

            variants.Add(name);
        }

        var canonicalByFold = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fold, variants) in variantsByFold)
        {
            canonicalByFold[fold] = variants.Min!;
        }

        return canonicalByFold;
    }
}
