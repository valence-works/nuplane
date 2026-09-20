using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Store.State;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Support for the host-free loading tests.
/// <para>
/// A host-integrated load is process-global and irreversible: its assemblies go into non-collectible
/// load contexts and the resolving hook is never removed. Tests therefore must not share package
/// identities or assembly identities, or one test would observe another test's load. Every package
/// here is built around a freshly emitted assembly with a unique simple name, so a test can assert
/// both that its type was invisible before the load and that it is visible after.
/// </para>
/// </summary>
internal static class HostFreeLoadTestSupport
{
    /// <summary>
    /// Emits a package whose single assembly sits directly in the install directory, the layout the
    /// loader treats as a flat package payload.
    /// </summary>
    public static EmittedPackage EmitPackage(DirectoryInfo root, string prefix)
    {
        var package = CreatePackageIdentity(root, prefix);
        EmitAssembly(package.AssemblyName, new Version(1, 0, 0, 0), Path.Combine(package.InstallPath, $"{package.AssemblyName}.dll"));

        return package;
    }

    /// <summary>
    /// Emits a package carrying one asset per target framework folder, each stamped with its own
    /// assembly version so a test can tell which framework's asset the loader selected.
    /// </summary>
    public static EmittedPackage EmitMultiTargetPackage(
        DirectoryInfo root,
        string prefix,
        params (string TargetFramework, Version AssemblyVersion)[] frameworks)
    {
        var package = CreatePackageIdentity(root, prefix);
        foreach (var (targetFramework, assemblyVersion) in frameworks)
        {
            var frameworkDirectory = Directory.CreateDirectory(Path.Combine(package.InstallPath, "lib", targetFramework));
            EmitAssembly(package.AssemblyName, assemblyVersion, Path.Combine(frameworkDirectory.FullName, $"{package.AssemblyName}.dll"));
        }

        return package;
    }

    /// <summary>
    /// Copies a fixture assembly that the test project deliberately does not reference, so it is absent
    /// from the default load context until a host-free load makes it resolvable.
    /// </summary>
    public static string CreateFixtureInstallDirectory(DirectoryInfo root, string projectDirectoryName, string assemblyFileName)
    {
        var installDirectory = root.CreateSubdirectory($"{Path.GetFileNameWithoutExtension(assemblyFileName)}-{Guid.NewGuid():N}");
        File.Copy(
            TestFixtureAssemblyPaths.FindProjectAssembly(projectDirectoryName, assemblyFileName),
            Path.Combine(installDirectory.FullName, assemblyFileName));

        return installDirectory.FullName;
    }

    /// <summary>
    /// Builds the active-package model a store reports for a package, defaulting every package into its
    /// own graph generation unless the caller groups several packages into one.
    /// </summary>
    public static ActivePackage ActivePackage(
        string packageId,
        string installPath,
        string version = "1.0.0",
        string? graphGenerationId = null) =>
        new(
            packageId,
            version,
            "feed-a",
            "source-a",
            installPath,
            DateTimeOffset.UtcNow,
            "corr-host-free",
            GraphId: graphGenerationId ?? packageId,
            GraphGenerationId: graphGenerationId ?? $"generation-{packageId}");

    /// <summary>
    /// Counts the non-collectible load contexts this process holds for the package graph the supplied
    /// packages form. It is the observable form of "the same graph was loaded exactly once".
    /// </summary>
    public static int CountGraphLoadContexts(params ActivePackage[] packages)
    {
        var graphKey = PackageLoader.BuildGraphKey(packages
            .Select(static package => new ResolvedPackage(
                package.PackageId,
                package.Version,
                package.FeedName ?? string.Empty,
                package.InstallPath,
                package.ActivatedAtUtc,
                package.SourceName ?? string.Empty))
            .ToArray());

        return AssemblyLoadContext.All.Count(context =>
            !context.IsCollectible && string.Equals(context.Name, graphKey, StringComparison.Ordinal));
    }

    private static EmittedPackage CreatePackageIdentity(DirectoryInfo root, string prefix)
    {
        var assemblyName = $"{prefix}.N{Guid.NewGuid():N}";

        return new(assemblyName, root.CreateSubdirectory(assemblyName).FullName);
    }

    private static void EmitAssembly(string assemblyName, Version assemblyVersion, string assemblyPath)
    {
        var assemblyBuilder = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName) { Version = assemblyVersion },
            typeof(object).Assembly);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName);
        moduleBuilder
            .DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();

        assemblyBuilder.Save(assemblyPath);
    }
}

/// <summary>
/// A package whose payload assembly was emitted for one test, with the identities that test asserts on.
/// </summary>
internal sealed record EmittedPackage(string AssemblyName, string InstallPath)
{
    /// <summary>Gets the package identifier, which matches the assembly's simple name.</summary>
    public string PackageId => AssemblyName;

    /// <summary>Gets the assembly-qualified name of the emitted marker type.</summary>
    public string MarkerTypeName => $"{AssemblyName}.Marker, {AssemblyName}";

    /// <summary>Gets the active-package model for this package.</summary>
    public ActivePackage AsActivePackage(string? graphGenerationId = null) =>
        HostFreeLoadTestSupport.ActivePackage(PackageId, InstallPath, graphGenerationId: graphGenerationId);
}

/// <summary>
/// A real dependency-injection-composed Nuplane host with host-integrated loading, driven the way the
/// reconciliation pipeline and last-known-good startup recovery drive it.
/// </summary>
internal sealed class NuplaneHostFixture : IAsyncDisposable
{
    private const string CorrelationId = "corr-composed-host";

    private readonly ServiceProvider _provider;

    public NuplaneHostFixture(string? stateFilePath = null, bool withLoading = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            if (stateFilePath is null)
            {
                nuplane.UseInMemoryStore();
            }
            else
            {
                nuplane.WithStateFile(stateFilePath);
            }

            if (withLoading)
            {
                nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.HostIntegrated));
            }
        });

        _provider = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    /// <summary>
    /// Persists the packages as the active set and, when this host composes loading, drives the
    /// reconciled-observer callback that both reconciliation and startup recovery drive.
    /// </summary>
    public async Task ActivateAsync(params ActivePackage[] packages)
    {
        await _provider.GetRequiredService<IStoreRegistry>().PersistActiveVersionsAsync(
            packages.ToDictionary(static package => package.PackageId, static package => package.Version, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(static package => package.PackageId, static package => package.Version, StringComparer.OrdinalIgnoreCase),
            CorrelationId,
            CancellationToken.None,
            packages.ToDictionary(static package => package.PackageId, static package => package.ToDescriptor(), StringComparer.OrdinalIgnoreCase));

        await _provider.GetRequiredService<IObserverEventDispatcher>().PublishReconciledAsync(
            new PackageChangeSet([], [], [], CorrelationId, DateTimeOffset.UtcNow),
            packages
                .Select(static package => new ResolvedPackage(
                    package.PackageId,
                    package.Version,
                    package.FeedName ?? string.Empty,
                    package.InstallPath,
                    package.ActivatedAtUtc,
                    package.SourceName ?? string.Empty))
                .ToArray(),
            CancellationToken.None);
    }

    public Task<PackageLoadStateSnapshot> ReadLoadStateAsync() =>
        _provider.GetRequiredService<IPackageLoadStateCatalog>().GetLoadStateAsync(CancellationToken.None);
}
