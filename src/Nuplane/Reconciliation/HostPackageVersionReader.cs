using System.Text.Json;
using NuGet.Versioning;

namespace Nuplane.Reconciliation;

/// <summary>
/// Where <see cref="HostPackageVersionReader"/> read the host's package versions from.
/// </summary>
internal enum HostPackageVersionSource
{
    /// <summary>
    /// The deps files the .NET host loaded for this process, from the <c>APP_CONTEXT_DEPS_FILES</c>
    /// runtime property — including one supplied with <c>dotnet exec --depsfile</c>.
    /// </summary>
    LoadedDepsFiles,

    /// <summary>
    /// Every <c>*.deps.json</c> in <see cref="AppContext.BaseDirectory"/>, used only when the host
    /// did not report which deps files it loaded.
    /// </summary>
    BaseDirectoryScan
}

/// <summary>
/// The host's package versions, keyed by package id case-insensitively, and where they came from.
/// </summary>
/// <param name="Versions">The normalized version of each package the deps files list.</param>
/// <param name="Source">Where the deps files were found.</param>
/// <param name="DepsFiles">The deps files that were read.</param>
internal sealed record HostPackageVersionSnapshot(
    IReadOnlyDictionary<string, string> Versions,
    HostPackageVersionSource Source,
    IReadOnlyList<string> DepsFiles);

/// <summary>
/// Reads the versions of the packages the running host carries from the deps files it loaded.
/// </summary>
/// <remarks>
/// <para>
/// The deps files the .NET host actually loaded are what describe the host. A process launched as
/// <c>dotnet exec --runtimeconfig host.runtimeconfig.json --depsfile host.deps.json tool.dll</c>
/// runs the tool against the host's dependency graph, and <c>APP_CONTEXT_DEPS_FILES</c> names the
/// host's deps file there; a scan of a directory would see whatever deps files happen to lie in it.
/// </para>
/// <para>
/// The property is joined with <c>;</c> on every operating system, not with
/// <see cref="Path.PathSeparator"/>: hostpolicy builds it with a literal <c>_X(';')</c>
/// (<c>src/native/corehost/hostpolicy/hostpolicy_context.cpp</c> in dotnet/runtime), and on Unix
/// <see cref="Path.PathSeparator"/> is <c>:</c>.
/// </para>
/// <para>
/// The list includes the framework deps files (<c>Microsoft.NETCore.App.deps.json</c> and any other
/// shared framework's) after the application's own. They are read like any other: every shipped
/// framework deps file lists exactly one library, its own runtime pack
/// (<c>Microsoft.NETCore.App.Runtime.&lt;rid&gt;</c>, <c>Microsoft.AspNetCore.App.Runtime.&lt;rid&gt;</c>),
/// never the framework name <c>Microsoft.NETCore.App</c> and never the packages whose assemblies
/// the framework ships. A runtime pack is selected by a framework reference, never named by a
/// package's nuspec dependency, and the version recorded for it is the one the process loaded, so
/// its entry can neither match a dependency by accident nor misstate the host.
/// </para>
/// <para>
/// When two files list the same package, the higher version wins. Between loaded files that should
/// not happen — a framework-dependent application's deps file does not list the runtime packs, and
/// each framework lists only its own — but where it does, the higher version is also the copy
/// hostpolicy binds when an assembly is in both the application and a framework. Between the files
/// a directory scan finds, which need not belong to one application, it is the rule this reader has
/// always applied.
/// </para>
/// </remarks>
internal static class HostPackageVersionReader
{
    internal const string AppContextDepsFilesKey = "APP_CONTEXT_DEPS_FILES";

    /// <summary>The runtime joins the loaded deps files with this, on every operating system.</summary>
    internal const char AppContextDepsFilesSeparator = ';';

    /// <summary>The running process's host package versions, read once.</summary>
    internal static Lazy<HostPackageVersionSnapshot> Current { get; } =
        new(() => Read(AppContext.GetData, AppContext.BaseDirectory));

    /// <summary>
    /// Reads the host package versions from the deps files <paramref name="getAppContextData"/>
    /// reports as loaded, or, when it reports none, from every <c>*.deps.json</c> in
    /// <paramref name="baseDirectory"/>.
    /// </summary>
    internal static HostPackageVersionSnapshot Read(Func<string, object?> getAppContextData, string baseDirectory)
    {
        var loadedDepsFiles = (getAppContextData(AppContextDepsFilesKey) as string)?
            .Split(AppContextDepsFilesSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        var (source, depsFiles) = loadedDepsFiles.Length > 0
            ? (HostPackageVersionSource.LoadedDepsFiles, loadedDepsFiles.Where(File.Exists).ToArray())
            : (HostPackageVersionSource.BaseDirectoryScan, Directory.EnumerateFiles(baseDirectory, "*.deps.json").ToArray());

        return new(ReadPackageVersions(depsFiles), source, depsFiles);
    }

    private static IReadOnlyDictionary<string, string> ReadPackageVersions(IEnumerable<string> depsFiles)
    {
        var packageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var depsFile in depsFiles)
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
}
