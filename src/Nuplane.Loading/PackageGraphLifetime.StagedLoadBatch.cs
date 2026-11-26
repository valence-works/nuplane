using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using static Nuplane.Loading.PackageProjection;

namespace Nuplane.Loading;

internal sealed partial class PackageGraphLifetime
{
    internal static string? GetGraphConflict(IEnumerable<string> packageKeys, IReadOnlyDictionary<string, string> conflicts)
    {
        var errors = packageKeys.Where(conflicts.ContainsKey).Select(key => conflicts[key]).Distinct(StringComparer.Ordinal).ToArray();
        return errors.Length == 0 ? null : string.Join(" ", errors);
    }

    internal sealed class StagedLoadBatch : IDisposable
    {
        private readonly PackageGraphLifetime _lifetime;
        private readonly long _originalVersion;
        private readonly List<AssemblyLoadContext> _retiredContexts = [];
        private readonly HashSet<AssemblyLoadContext> _publishedContexts = [];
        private CommitProjection? _projection;
        private bool _committed;
        private bool _disposed;

        internal StagedLoadBatch(PackageGraphLifetime lifetime, long originalVersion)
        {
            _lifetime = lifetime;
            _originalVersion = originalVersion;
        }

        internal List<PackageLoadSession> Loaded { get; } = [];
        private Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<StagedGraph> Graphs { get; } = [];
        internal List<AssemblyLoadContext> StagedContexts { get; } = [];

        internal PackageLoadResult Result => new(Loaded, Failures);

        internal void RecordFailure(IReadOnlyList<ResolvedPackage> packages, string message)
        {
            foreach (var package in packages)
                Failures[package.Id] = message;
        }

        internal IReadOnlyDictionary<string, string> ValidateForCommit(
            IReadOnlyList<ActivePackage> activePackages,
            IReadOnlySet<string> rejectedPackageIds)
        {
            ArgumentNullException.ThrowIfNull(activePackages);
            ArgumentNullException.ThrowIfNull(rejectedPackageIds);
            ObjectDisposedException.ThrowIf(_disposed, this);

            var active = activePackages.ToDictionary(
                static package => BuildKey(package.PackageId, package.Version), StringComparer.OrdinalIgnoreCase);
            var acceptedGraphs = Graphs.Where(graph => graph.Packages.All(projection =>
                    Matches(projection, active) && !rejectedPackageIds.Contains(active[projection.PackageKey].PackageId)))
                .ToArray();
            var replacedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var graph in acceptedGraphs)
            {
                foreach (var package in graph.Packages)
                {
                    if (!replacedKeys.Add(package.PackageKey))
                        throw new InvalidOperationException($"Package '{package.PackageKey}' is activated by more than one staged graph.");
                }
            }

            lock (_lifetime.SyncRoot)
            {
                if (_committed || _lifetime._activationProjectionVersion != _originalVersion ||
                    _lifetime._validatedActivationBatch is not null)
                {
                    throw new InvalidOperationException("The loaded package projection changed while activation was staged.");
                }

                var retainedKeys = _lifetime._loadedPackageProjections.Values.Where(ShouldRetain)
                    .Select(static projection => projection.PackageKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var invalidKeys = _lifetime._loadedPackageProjections.Values
                    .Concat(_lifetime._inertPackageProjections.Values)
                    .Where(projection => !ShouldRetain(projection))
                    .Select(static projection => projection.PackageKey)
                    .Concat(_lifetime._sessions.Keys.Where(key => !active.ContainsKey(key)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var catalog = _lifetime._resolutionCatalog.PrepareActiveProjection(retainedKeys,
                    acceptedGraphs.SelectMany(static graph => graph.CatalogEntries).ToArray(), out var conflicts);
                var rejectedGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (catalog is null)
                {
                    foreach (var graph in acceptedGraphs)
                    {
                        var packageKeys = graph.Packages.Select(static package => package.PackageKey);
                        var message = GetGraphConflict(packageKeys, conflicts);
                        if (message is null)
                            continue;

                        foreach (var package in graph.Packages)
                        {
                            var packageId = active[package.PackageKey].PackageId;
                            Failures[packageId] = message;
                            rejectedGraphs[packageId] = message;
                        }
                    }
                }
                else
                {
                    _projection = new(acceptedGraphs, invalidKeys, catalog);
                    _lifetime._validatedActivationBatch = this;
                }

                return rejectedGraphs;
            }

            bool ShouldRetain(PackageProjection projection) =>
                !replacedKeys.Contains(projection.PackageKey) && Matches(projection, active);
        }

        private static bool Matches(PackageProjection projection, IReadOnlyDictionary<string, ActivePackage> active) =>
            active.TryGetValue(projection.PackageKey, out var package) &&
            string.Equals(projection.InstallPath, package.InstallPath, StringComparison.Ordinal) &&
            (package.ArchiveHash is null ||
                string.Equals(projection.PackageContentHash, package.ArchiveHash, StringComparison.Ordinal));

        internal void Commit()
        {
            lock (_lifetime.SyncRoot)
            {
                if (!ReferenceEquals(_lifetime._validatedActivationBatch, this) || _projection is null)
                    throw new InvalidOperationException("Validate the staged package activation before committing it.");

                foreach (var key in _projection.InvalidKeys)
                {
                    if (_lifetime._contexts.TryRemove(key, out var context))
                        _retiredContexts.Add(context);
                    _lifetime._sessions.TryRemove(key, out _);
                    _lifetime._loadedPackageProjections.TryRemove(key, out _);
                    _lifetime.ClearInert(key);
                    _lifetime.RemoveKeyFromLoadedGraphs(key);
                }

                foreach (var graph in _projection.Graphs)
                {
                    foreach (var inert in graph.Inert)
                        _lifetime.MarkInert(inert);

                    if (graph.Context is null)
                        continue;

                    _publishedContexts.Add(graph.Context);
                    foreach (var session in graph.Sessions)
                    {
                        var key = BuildKey(session.PackageId, session.Version);
                        _lifetime._contexts[key] = graph.Context;
                        _lifetime._loadedPackageProjections[key] = graph.Packages.Single(projection =>
                            string.Equals(projection.PackageKey, key, StringComparison.OrdinalIgnoreCase));
                        _lifetime._sessions[key] = session;
                    }

                    if (graph.CacheEntry is not null)
                        _lifetime._loadedGraphs[graph.GraphKey] = graph.CacheEntry;
                }

                _lifetime._resolutionCatalog.CommitActiveProjection(_projection.Catalog);
                _committed = true;
                _lifetime._activationProjectionVersion++;
                _lifetime._validatedActivationBatch = null;
            }
        }

        public void Dispose()
        {
            AssemblyLoadContext[] unload;
            lock (_lifetime.SyncRoot)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (ReferenceEquals(_lifetime._validatedActivationBatch, this))
                    _lifetime._validatedActivationBatch = null;

                unload = _retiredContexts.Concat(StagedContexts.Where(context => !_publishedContexts.Contains(context)))
                    .Distinct().Where(context => context.IsCollectible && !_lifetime._contexts.Values.Contains(context))
                    .ToArray();
                _projection = null;
                Graphs.Clear();
                StagedContexts.Clear();
                _retiredContexts.Clear();
                _publishedContexts.Clear();
            }

            // Collectible context unload can invoke application handlers. The store and loader
            // authority locks must already be released before cleanup runs.
            foreach (var context in unload)
            {
                try
                {
                    context.Unload();
                }
                catch (Exception exception)
                {
                    StagedContextUnloadFailed(_lifetime._logger, context.Name, exception);
                }
            }
        }

        private sealed record CommitProjection(
            IReadOnlyList<StagedGraph> Graphs,
            IReadOnlyList<string> InvalidKeys,
            HostIntegratedAssemblyResolutionCatalog.ActiveProjection Catalog);
    }

    internal sealed record StagedGraph(
        string GraphKey,
        IReadOnlyList<PackageProjection> Packages,
        IReadOnlyList<PackageProjection> Inert,
        AssemblyLoadContext? Context,
        IReadOnlyList<PackageLoadSession> Sessions,
        LoadedGraphCacheEntry? CacheEntry,
        IReadOnlyList<HostIntegratedAssemblyResolutionEntry> CatalogEntries);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Unable to unload package activation context {ContextName}.")]
    private static partial void StagedContextUnloadFailed(ILogger logger, string? contextName, Exception exception);
}
