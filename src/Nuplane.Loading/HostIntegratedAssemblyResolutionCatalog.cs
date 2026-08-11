using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Maintains framework-visible assembly resolution entries for active host-integrated packages.
/// </summary>
internal sealed class HostIntegratedAssemblyResolutionCatalog
{
    private readonly object _gate = new();
    private IReadOnlyList<HostIntegratedAssemblyResolutionEntry> _entries = [];
    private long _generation;

    /// <summary>
    /// Gets the current published generation.
    /// </summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// Publishes host-integrated assemblies for a successfully loaded graph.
    /// </summary>
    public void PublishGraph(
        string graphKey,
        IReadOnlyList<PackageLoadModeSelection> selections,
        IReadOnlyDictionary<string, IReadOnlyList<Assembly>> assembliesByPackageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphKey);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(assembliesByPackageKey);

        var additions = new List<HostIntegratedAssemblyResolutionEntry>();

        foreach (var selection in selections.Where(static selection => selection.LoadMode == PackageLoadMode.HostIntegrated))
        {
            var packageKey = BuildPackageKey(selection.PackageId, selection.Version);
            if (!assembliesByPackageKey.TryGetValue(packageKey, out var assemblies))
            {
                continue;
            }

            foreach (var assembly in assemblies.Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location)))
            {
                var name = assembly.GetName();
                additions.Add(new(
                    name.Name ?? Path.GetFileNameWithoutExtension(assembly.Location),
                    assembly.FullName ?? name.FullName,
                    name.Version,
                    assembly.Location,
                    selection.PackageId,
                    selection.Version,
                    graphKey,
                    0,
                    assembly));
            }
        }

        lock (_gate)
        {
            var replacementPackageKeys = additions
                .Select(static entry => BuildPackageKey(entry.PackageId, entry.PackageVersion))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextGeneration = _generation + 1;
            var retained = _entries
                .Where(entry => !string.Equals(entry.GraphKey, graphKey, StringComparison.OrdinalIgnoreCase)
                    && !replacementPackageKeys.Contains(BuildPackageKey(entry.PackageId, entry.PackageVersion)))
                .Concat(additions.Select(entry => entry with { Generation = nextGeneration }))
                .ToArray();

            ValidateNoConflicts(retained);

            _entries = retained
                .OrderBy(static entry => entry.AssemblySimpleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Version?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.PackageVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _generation = nextGeneration;
        }
    }

    /// <summary>
    /// Validates that the specified graph can be published without conflicting with active entries.
    /// </summary>
    public void ValidateCanPublishGraph(
        string graphKey,
        IReadOnlyList<HostIntegratedAssemblyResolutionCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphKey);
        ArgumentNullException.ThrowIfNull(candidates);

        lock (_gate)
        {
            var replacementPackageKeys = candidates
                .Select(static candidate => BuildPackageKey(candidate.PackageId, candidate.PackageVersion))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var proposedEntries = _entries
                .Where(entry => !string.Equals(entry.GraphKey, graphKey, StringComparison.OrdinalIgnoreCase)
                    && !replacementPackageKeys.Contains(BuildPackageKey(entry.PackageId, entry.PackageVersion)))
                .Select(static entry => new ConflictCandidate(
                    entry.AssemblySimpleName,
                    entry.Version,
                    entry.PackageId,
                    entry.PackageVersion))
                .Concat(candidates.Select(static candidate => new ConflictCandidate(
                    candidate.AssemblySimpleName,
                    candidate.Version,
                    candidate.PackageId,
                    candidate.PackageVersion)))
                .ToArray();

            ValidateNoConflicts(proposedEntries);
        }
    }

    /// <summary>
    /// Removes all entries associated with the specified package version.
    /// </summary>
    public void RemovePackage(string packageId, string version)
    {
        lock (_gate)
        {
            var retained = _entries
                .Where(entry => !string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.PackageVersion, version, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retained.Length != _entries.Count)
            {
                _entries = retained;
                _generation++;
            }
        }
    }

    /// <summary>
    /// Attempts to resolve the requested assembly name from active host-integrated entries.
    /// </summary>
    public bool TryResolve(AssemblyName assemblyName, out Assembly? assembly, out HostIntegratedAssemblyResolutionDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        IReadOnlyList<HostIntegratedAssemblyResolutionEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries;
        }

        var candidates = snapshot
            .Where(entry => string.Equals(entry.AssemblySimpleName, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            assembly = null;
            diagnostic = new(assemblyName.FullName, "not-found", null, [], $"Assembly '{assemblyName.FullName}' was not found in active host-integrated packages.");
            return false;
        }

        if (assemblyName.Version is not null)
        {
            candidates = candidates
                .Where(entry => AssemblyNamesMatch(assemblyName, entry.Assembly.GetName()))
                .ToArray();
        }

        if (candidates.Length == 1)
        {
            assembly = candidates[0].Assembly;
            diagnostic = new(assemblyName.FullName, "success", candidates[0].AssemblyPath, [FormatCandidate(candidates[0])], $"Resolved '{assemblyName.FullName}' from '{candidates[0].PackageId}@{candidates[0].PackageVersion}'.");
            return true;
        }

        assembly = null;
        diagnostic = new(
            assemblyName.FullName,
            candidates.Length == 0 ? "not-found" : "ambiguity",
            null,
            candidates.Select(FormatCandidate).ToArray(),
            candidates.Length == 0
                ? $"Assembly '{assemblyName.FullName}' was not found with the requested version in active host-integrated packages."
                : $"Assembly '{assemblyName.FullName}' is ambiguous across active host-integrated packages.");
        return false;
    }

    private static void ValidateNoConflicts(IEnumerable<HostIntegratedAssemblyResolutionEntry> proposedEntries) =>
        ValidateNoConflicts(proposedEntries.Select(static entry => new ConflictCandidate(
            entry.AssemblySimpleName,
            entry.Version,
            entry.PackageId,
            entry.PackageVersion)));

    private static void ValidateNoConflicts(IEnumerable<ConflictCandidate> proposedEntries)
    {
        var conflict = proposedEntries
            .GroupBy(static entry => entry.AssemblySimpleName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                SimpleName = group.Key,
                Versions = group
                    .Select(static entry => entry.Version?.ToString() ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                PackageKeys = group
                    .Select(static entry => BuildPackageKey(entry.PackageId, entry.PackageVersion))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Entries = group.ToArray()
            })
            .FirstOrDefault(group => group.Versions.Length > 1 || group.PackageKeys.Length > 1);

        if (conflict is null)
        {
            return;
        }

        var packages = string.Join(", ", conflict.Entries
            .OrderBy(static entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => $"{entry.PackageId}@{entry.PackageVersion} ({entry.Version})"));
        throw new InvalidOperationException(
            $"Host-integrated assembly conflict for '{conflict.SimpleName}': active packages expose ambiguous assembly identities. Candidates: {packages}.");
    }

    private static string FormatCandidate(HostIntegratedAssemblyResolutionEntry entry) =>
        $"{entry.AssemblyFullName} from {entry.PackageId}@{entry.PackageVersion} at {entry.AssemblyPath}";

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";

    private static bool AssemblyNamesMatch(AssemblyName requested, AssemblyName definition) =>
        string.Equals(requested.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
        && Equals(requested.Version, definition.Version)
        && string.Equals(NormalizeCultureName(requested.CultureName), NormalizeCultureName(definition.CultureName), StringComparison.OrdinalIgnoreCase)
        && PublicKeyTokensMatch(requested.GetPublicKeyToken(), definition.GetPublicKeyToken());

    private static bool PublicKeyTokensMatch(byte[]? requested, byte[]? definition) =>
        (requested ?? []).AsSpan().SequenceEqual(definition ?? []);

    private static string NormalizeCultureName(string? cultureName) =>
        string.IsNullOrWhiteSpace(cultureName) || string.Equals(cultureName, "neutral", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : cultureName;

    private sealed record ConflictCandidate(
        string AssemblySimpleName,
        Version? Version,
        string PackageId,
        string PackageVersion);
}
