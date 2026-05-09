using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Maintains framework-visible assembly resolution entries for active host-integrated packages.
/// </summary>
internal sealed class HostIntegratedAssemblyResolutionCatalog
{
    private readonly object gate = new();
    private IReadOnlyList<HostIntegratedAssemblyResolutionEntry> entries = [];
    private long generation;

    /// <summary>
    /// Gets the current published generation.
    /// </summary>
    public long Generation
    {
        get
        {
            lock (gate)
            {
                return generation;
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

        long nextGeneration;
        lock (gate)
        {
            nextGeneration = generation + 1;
        }
        IReadOnlyList<HostIntegratedAssemblyResolutionEntry> snapshot;
        lock (gate)
        {
            snapshot = entries;
        }

        var retained = snapshot
            .Where(entry => !string.Equals(entry.GraphKey, graphKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

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
                retained.Add(new(
                    name.Name ?? Path.GetFileNameWithoutExtension(assembly.Location),
                    assembly.FullName ?? name.FullName,
                    name.Version,
                    assembly.Location,
                    selection.PackageId,
                    selection.Version,
                    graphKey,
                    nextGeneration,
                    assembly));
            }
        }

        ValidateNoConflicts(retained);

        lock (gate)
        {
            entries = retained
                .OrderBy(static entry => entry.AssemblySimpleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Version?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.PackageVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            generation = nextGeneration;
        }
    }

    /// <summary>
    /// Removes all entries associated with the specified package version.
    /// </summary>
    public void RemovePackage(string packageId, string version)
    {
        lock (gate)
        {
            entries = entries
                .Where(entry => !string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.PackageVersion, version, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Attempts to resolve the requested assembly name from active host-integrated entries.
    /// </summary>
    public bool TryResolve(AssemblyName assemblyName, out Assembly? assembly, out HostIntegratedAssemblyResolutionDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        IReadOnlyList<HostIntegratedAssemblyResolutionEntry> snapshot;
        lock (gate)
        {
            snapshot = entries;
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
                .Where(entry => Equals(entry.Version, assemblyName.Version))
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

    private static void ValidateNoConflicts(IEnumerable<HostIntegratedAssemblyResolutionEntry> proposedEntries)
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
                Entries = group.ToArray()
            })
            .FirstOrDefault(group => group.Versions.Length > 1);

        if (conflict is null)
        {
            return;
        }

        var packages = string.Join(", ", conflict.Entries
            .OrderBy(static entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => $"{entry.PackageId}@{entry.PackageVersion} ({entry.Version})"));
        throw new InvalidOperationException(
            $"Host-integrated assembly conflict for '{conflict.SimpleName}': active packages expose multiple versions. Candidates: {packages}.");
    }

    private static string FormatCandidate(HostIntegratedAssemblyResolutionEntry entry) =>
        $"{entry.AssemblyFullName} from {entry.PackageId}@{entry.PackageVersion} at {entry.AssemblyPath}";

    private static string BuildPackageKey(string packageId, string version) => $"{packageId}@{version}";
}
