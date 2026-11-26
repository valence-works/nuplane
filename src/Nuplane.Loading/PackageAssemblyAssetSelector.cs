using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nuplane.Loading;

/// <summary>Chooses package assembly files for the host runtime and framework.</summary>
internal sealed class PackageAssemblyAssetSelector(string? hostTargetFrameworkOverride = null)
{
    private readonly string? _hostTargetFrameworkOverride = string.IsNullOrWhiteSpace(hostTargetFrameworkOverride)
        ? null
        : hostTargetFrameworkOverride.Trim();

    internal IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(string packageId, string installPath)
    {
        var selection = Select(installPath, packageId);
        return selection.AssemblyPaths
            .Select(path => new AssemblyScanCandidate(
                path,
                Path.GetFileName(path),
                TryResolveTargetFrameworkMoniker(path, installPath),
                string.Equals(path, selection.MainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "PrimaryLoadAssembly"
                    : "AdditionalManagedAssembly",
                string.Equals(path, selection.MainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "selected-by-loader"
                    : "co-located-managed-assembly"))
            .ToArray();
    }

    private static string? TryResolveTargetFrameworkMoniker(string assemblyPath, string installPath)
    {
        var relativePath = Path.GetRelativePath(installPath, assemblyPath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var frameworkFolder = segments.Length >= 2 && string.Equals(segments[0], "lib", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : segments.Length >= 4
                && string.Equals(segments[0], "runtimes", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[2], "lib", StringComparison.OrdinalIgnoreCase)
                ? segments[3]
                : null;

        if (frameworkFolder is not null && TryParseFrameworkTarget(frameworkFolder, out var framework))
        {
            return framework.DisplayName;
        }

        return null;
    }

    internal AssemblyAssetSelection Select(string installPath, string packageId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            throw new DirectoryNotFoundException($"Install path '{installPath}' does not exist.");
        }

        if (TryResolveRuntimeSpecificAssemblySelection(installPath, packageId, _hostTargetFrameworkOverride, out var runtimeSpecificSelection))
        {
            return runtimeSpecificSelection;
        }

        var libPath = Path.Combine(installPath, "lib");
        if (Directory.Exists(libPath) && TryResolveFrameworkSpecificAssemblySelection(libPath, installPath, packageId, _hostTargetFrameworkOverride, out var frameworkSpecificSelection))
        {
            return frameworkSpecificSelection;
        }

        var searchRoot = Directory.Exists(libPath) ? libPath : installPath;
        return ResolveAssemblyFromCandidates(
            EnumerateAssemblyCandidatesExcludingNativeAssets(searchRoot, installPath),
            installPath,
            packageId);
    }

    private static bool TryResolveRuntimeSpecificAssemblySelection(
        string installPath,
        string packageId,
        string? hostTargetFrameworkOverride,
        out AssemblyAssetSelection selection)
    {
        var runtimesPath = Path.Combine(installPath, "runtimes");
        if (!Directory.Exists(runtimesPath))
        {
            selection = null!;
            return false;
        }

        var hostFramework = ResolveHostFramework(hostTargetFrameworkOverride);
        if (hostFramework is null)
        {
            throw new InvalidOperationException(
                $"Nuplane could not determine the current host target framework while resolving '{packageId}' from '{installPath}'.");
        }

        foreach (var runtimeIdentifier in PackageGraphLoadContext.ExpandRuntimeIdentifiers(RuntimeInformation.RuntimeIdentifier))
        {
            var libPath = Path.Combine(runtimesPath, runtimeIdentifier, "lib");
            if (!Directory.Exists(libPath))
            {
                continue;
            }

            var frameworkDirectories = GetFrameworkDirectories(libPath);
            var selectedDirectory = SelectBestFrameworkDirectory(hostFramework, frameworkDirectories);
            if (selectedDirectory is null)
            {
                continue;
            }

            var candidatePaths = EnumerateAssemblyCandidatesExcludingNativeAssets(selectedDirectory.Path, installPath).ToArray();
            if (candidatePaths.Length == 0)
            {
                continue;
            }

            selection = ResolveAssemblyFromCandidates(
                candidatePaths,
                installPath,
                packageId);
            return true;
        }

        selection = null!;
        return false;
    }

    private static bool TryResolveFrameworkSpecificAssemblySelection(
        string libPath,
        string installPath,
        string packageId,
        string? hostTargetFrameworkOverride,
        out AssemblyAssetSelection selection)
    {
        var frameworkDirectories = GetFrameworkDirectories(libPath);

        if (frameworkDirectories.Length == 0)
        {
            selection = null!;
            return false;
        }

        var hostFramework = ResolveHostFramework(hostTargetFrameworkOverride);
        if (hostFramework is null)
        {
            throw new InvalidOperationException(
                $"Nuplane could not determine the current host target framework while resolving '{packageId}' from '{installPath}'.");
        }

        var selectedDirectory = SelectBestFrameworkDirectory(hostFramework, frameworkDirectories);
        if (selectedDirectory is null)
        {
            var availableFrameworks = string.Join(", ", frameworkDirectories.Select(candidate => candidate.FolderName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"No compatible target framework assets were found under '{installPath}' for host framework '{GetFrameworkDisplayName(hostFramework)}'. Available frameworks: {availableFrameworks}.");
        }

        selection = ResolveAssemblyFromCandidates(
            EnumerateAssemblyCandidatesExcludingNativeAssets(selectedDirectory.Path, installPath),
            installPath,
            packageId);

        return true;
    }

    private static FrameworkDirectory[] GetFrameworkDirectories(string libPath) => Directory
        .EnumerateDirectories(libPath)
        .Select(FrameworkDirectory.Create)
        .Where(candidate => candidate is not null)
        .Cast<FrameworkDirectory>()
        .ToArray();

    private static AssemblyAssetSelection ResolveAssemblyFromCandidates(
        IEnumerable<string> candidatePaths,
        string installPath,
        string packageId)
    {
        var assemblies = candidatePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (assemblies.Length == 0)
        {
            throw new NoLoadableAssemblyException($"No loadable assembly found under '{installPath}'.");
        }

        if (assemblies.Length == 1)
        {
            return new(assemblies, assemblies[0]);
        }

        var matchingByPackageId = assemblies
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingByPackageId.Length == 1)
        {
            return new(assemblies, matchingByPackageId[0]);
        }

        // A library package may provide several runtime assemblies and no entry assembly.
        // Admit the entire selected asset set without assigning an arbitrary primary assembly.
        return new(assemblies, null);
    }

    private static IEnumerable<string> EnumerateAssemblyCandidatesExcludingNativeAssets(string searchRoot, string installPath) =>
        Directory
            .EnumerateFiles(searchRoot, "*.dll", SearchOption.AllDirectories)
            .Where(path => !IsNativeRuntimeAsset(path, installPath));

    private static bool IsNativeRuntimeAsset(string assemblyPath, string installPath)
    {
        var relativePath = Path.GetRelativePath(installPath, assemblyPath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Length >= 3
            && string.Equals(segments[0], "runtimes", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "native", StringComparison.OrdinalIgnoreCase);
    }

    private static FrameworkDirectory? SelectBestFrameworkDirectory(
        FrameworkTarget hostFramework,
        IReadOnlyList<FrameworkDirectory> frameworkDirectories)
    {
        return hostFramework.Kind switch
        {
            FrameworkKind.NetCoreApp =>
                SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetCoreApp, hostFramework.Version)
                ?? SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, GetCompatibleNetStandardVersion(hostFramework)),
            FrameworkKind.NetStandard => SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, hostFramework.Version),
            FrameworkKind.NetFramework =>
                SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetFramework, hostFramework.Version)
                ?? SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, new Version(2, 0)),
            _ => null
        };
    }

    private static FrameworkDirectory? SelectHighestCompatibleFramework(
        IReadOnlyList<FrameworkDirectory> frameworkDirectories,
        FrameworkKind kind,
        Version maxVersion)
    {
        return frameworkDirectories
            .Where(candidate => candidate.Target.Kind == kind && candidate.Target.Version.CompareTo(maxVersion) <= 0)
            .OrderByDescending(candidate => candidate.Target.Version)
            .FirstOrDefault();
    }

    private static Version GetCompatibleNetStandardVersion(FrameworkTarget hostFramework) => hostFramework.Kind switch
    {
        FrameworkKind.NetCoreApp => new Version(2, 1),
        FrameworkKind.NetFramework => new Version(2, 0),
        FrameworkKind.NetStandard => hostFramework.Version,
        _ => new Version(0, 0)
    };

    private static FrameworkTarget? GetCurrentHostFramework()
    {
        var targetFrameworkName = typeof(PackageAssemblyAssetSelector).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;

        if (string.IsNullOrWhiteSpace(targetFrameworkName))
        {
            targetFrameworkName = AppContext.TargetFrameworkName;
        }

        if (string.IsNullOrWhiteSpace(targetFrameworkName))
        {
            return null;
        }

        var frameworkName = new FrameworkName(targetFrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => new FrameworkTarget(FrameworkKind.NetCoreApp, new Version(frameworkName.Version.Major, frameworkName.Version.Minor), $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}"),
            ".NETStandard" => new FrameworkTarget(FrameworkKind.NetStandard, new Version(frameworkName.Version.Major, frameworkName.Version.Minor), $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}"),
            ".NETFramework" => new FrameworkTarget(FrameworkKind.NetFramework, frameworkName.Version, $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}"),
            _ => null
        };
    }

    private static FrameworkTarget? ResolveHostFramework(string? hostTargetFrameworkOverride)
    {
        if (!string.IsNullOrWhiteSpace(hostTargetFrameworkOverride))
        {
            return TryParseFrameworkTarget(hostTargetFrameworkOverride, out var overriddenFramework)
                ? overriddenFramework
                : null;
        }

        return GetCurrentHostFramework();
    }

    private static string GetFrameworkDisplayName(FrameworkTarget framework) => framework.DisplayName;

    /// <summary>
    /// Determines whether <paramref name="targetFrameworkMoniker"/> is a moniker this loader's asset
    /// selection understands, so a caller supplying a target framework override can be rejected up front
    /// instead of silently resolving assets for no framework at all.
    /// </summary>
    internal static bool IsSupportedTargetFrameworkMoniker(string targetFrameworkMoniker) =>
        !string.IsNullOrWhiteSpace(targetFrameworkMoniker)
        && TryParseFrameworkTarget(targetFrameworkMoniker.Trim(), out _);

    private enum FrameworkKind
    {
        Unknown,
        NetCoreApp,
        NetStandard,
        NetFramework
    }

    private sealed record FrameworkTarget(FrameworkKind Kind, Version Version, string DisplayName);

    private sealed record FrameworkDirectory(string Path, string FolderName, FrameworkTarget Target)
    {
        public static FrameworkDirectory? Create(string path)
        {
            var folderName = System.IO.Path.GetFileName(path);
            return TryParseFrameworkTarget(folderName, out var target)
                ? new FrameworkDirectory(path, folderName, target)
                : null;
        }
    }

    private static bool TryParseFrameworkTarget(string folderName, out FrameworkTarget target)
    {
        if (TryParseNetCoreApp(folderName, out var netCoreApp))
        {
            target = netCoreApp;
            return true;
        }

        if (TryParseNetStandard(folderName, out var netStandard))
        {
            target = netStandard;
            return true;
        }

        if (TryParseNetFramework(folderName, out var netFramework))
        {
            target = netFramework;
            return true;
        }

        target = null!;
        return false;
    }

    private static bool TryParseNetCoreApp(string folderName, out FrameworkTarget target)
    {
        const string prefix = "net";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !folderName.Contains('.'))
        {
            target = null!;
            return false;
        }

        var versionText = folderName[prefix.Length..];
        if (!Version.TryParse(versionText, out var version))
        {
            target = null!;
            return false;
        }

        target = new FrameworkTarget(FrameworkKind.NetCoreApp, new Version(version.Major, version.Minor), $"net{version.Major}.{version.Minor}");
        return true;
    }

    private static bool TryParseNetStandard(string folderName, out FrameworkTarget target)
    {
        const string prefix = "netstandard";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            target = null!;
            return false;
        }

        var versionText = folderName[prefix.Length..];
        if (!Version.TryParse(versionText, out var version))
        {
            target = null!;
            return false;
        }

        target = new FrameworkTarget(FrameworkKind.NetStandard, new Version(version.Major, version.Minor), $"netstandard{version.Major}.{version.Minor}");
        return true;
    }

    private static bool TryParseNetFramework(string folderName, out FrameworkTarget target)
    {
        const string prefix = "net";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folderName.Contains('.'))
        {
            target = null!;
            return false;
        }

        var versionDigits = folderName[prefix.Length..];
        if (versionDigits.Length is < 2 or > 3 || !versionDigits.All(char.IsDigit))
        {
            target = null!;
            return false;
        }

        var major = int.Parse(versionDigits[..1]);
        var minor = int.Parse(versionDigits[1..2]);
        var build = versionDigits.Length == 3 ? int.Parse(versionDigits[2..3]) : -1;
        var version = build >= 0 ? new Version(major, minor, build) : new Version(major, minor);
        target = new FrameworkTarget(FrameworkKind.NetFramework, version, folderName);
        return true;
    }
}

internal sealed record AssemblyAssetSelection(IReadOnlyList<string> AssemblyPaths, string? MainAssemblyPath)
{
    public IReadOnlyList<string> LoadAssemblyPaths => MainAssemblyPath is { } path ? [path] : AssemblyPaths;

    // A secondary component can own a dependency manifest even without a package-ID match.
    // Satellites belong in the culture-aware index, not in the component resolver list.
    public IReadOnlyList<string> ResolverAssemblyPaths => AssemblyPaths
        .Where(path => string.Equals(path, MainAssemblyPath, StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.ChangeExtension(path, ".deps.json")))
        .Where(static path => string.IsNullOrEmpty(PackageAssemblyReadCheckpoint.GetAssemblyName(path).CultureName))
        .ToArray();
}

internal sealed class NoLoadableAssemblyException(string message) : FileNotFoundException(message);
