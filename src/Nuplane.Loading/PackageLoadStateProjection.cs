using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// The single definition of how one active package plus the loader's recorded state for it becomes a
/// <see cref="PackageLoadState"/>. Both the host-facing load-state catalog and the host-free entry
/// point project through here, so the same package in the same loader state is always reported the
/// same way.
/// </summary>
internal static class PackageLoadStateProjection
{
    /// <summary>
    /// Projects the current load state of <paramref name="package"/> from <paramref name="loader"/>:
    /// a loaded session becomes <see cref="PackageLoadStatus.Loaded"/> with its assembly references, a
    /// non-loaded session becomes <see cref="PackageLoadStatus.Failed"/> carrying its reason, a package
    /// the loader evaluated as contributing no assemblies becomes <see cref="PackageLoadStatus.Skipped"/>,
    /// and a package the loader has no record of becomes <see cref="PackageLoadStatus.Stale"/>.
    /// </summary>
    public static PackageLoadState Project(
        ActivePackage package,
        PackageLoader loader,
        AssemblyScanCandidateProjector candidateProjector)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(candidateProjector);

        if (loader.Sessions.TryGetValue($"{package.PackageId}@{package.Version}", out var session))
        {
            return session.IsLoaded
                ? Create(package, PackageLoadStatus.Loaded, session.LoadedAt, [], candidateProjector.Project(package), session)
                : Create(package, PackageLoadStatus.Failed, session.LoadedAt, BuildDiagnostics(session.LastError), [], session);
        }

        if (loader.IsInertPackage(package.PackageId, package.Version))
        {
            // Evaluated and deliberately not loaded because it contributes no assemblies. This is a
            // settled outcome, not missing state, so it must not read as stale or degrade the surface.
            return Create(package, PackageLoadStatus.Skipped, null, ["loading-no-assemblies-to-load"], []);
        }

        return Create(package, PackageLoadStatus.Stale, null, ["loading-state-missing-for-active-package"], []);
    }

    /// <summary>
    /// Creates a load-state record for an active package with an explicit status, used for the states
    /// that are decided without consulting the loader at all.
    /// </summary>
    public static PackageLoadState Create(
        ActivePackage package,
        PackageLoadStatus status,
        DateTimeOffset? loadedAtUtc,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<PackageAssemblyReference> assemblyReferences,
        PackageLoadSession? session = null) =>
        new(
            package.PackageId,
            package.Version,
            status,
            package.InstallPath,
            loadedAtUtc,
            diagnostics,
            assemblyReferences.ToArray(),
            package.Discoverable,
            session?.LoadMode ?? PackageLoadMode.Collectible,
            session?.FrameworkIntegrationSafe ?? false)
        {
            LoadModeDiagnostics = session?.LoadModeDiagnostics ?? []
        };

    private static IReadOnlyList<string> BuildDiagnostics(string? message) =>
        string.IsNullOrWhiteSpace(message) ? ["loading-failed"] : [message.Trim()];
}
