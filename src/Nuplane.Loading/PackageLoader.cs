using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Versioning;
using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Loads package assemblies into isolated collectible load contexts, tracking sessions
/// and providing context removal for unloading. Resolves the main assembly within
/// each package's install directory.
/// </summary>
internal sealed class PackageLoader : IPackageLoader
{
    private readonly SharedAssemblyPolicyMatcher _matcher;
    private readonly ConcurrentDictionary<string, PackageAssemblyLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageLoadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="PackageLoader"/> with an optional shared assembly policy matcher.
    /// </summary>
    public PackageLoader(SharedAssemblyPolicyMatcher? matcher = null)
    {
        _matcher = matcher ?? new SharedAssemblyPolicyMatcher();
    }

    /// <summary>
    /// Gets the active load sessions keyed by package-version key.
    /// </summary>
    public IReadOnlyDictionary<string, PackageLoadSession> Sessions => _sessions;

    /// <summary>
    /// Builds deterministic assembly scan candidates for the specified active package install path.
    /// </summary>
    public IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(string packageId, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var mainAssemblyPath = ResolveMainAssemblyPath(installPath, packageId);
        var targetFrameworkMoniker = TryResolveTargetFrameworkMoniker(mainAssemblyPath, installPath);
        var assemblyPaths = Directory
            .EnumerateFiles(installPath, "*.dll", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return assemblyPaths
            .Select(path => new AssemblyScanCandidate(
                path,
                Path.GetFileName(path),
                targetFrameworkMoniker,
                string.Equals(path, mainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "PrimaryLoadAssembly"
                    : "AdditionalManagedAssembly",
                string.Equals(path, mainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "selected-by-loader"
                    : "co-located-managed-assembly"))
            .ToArray();
    }

    /// <inheritdoc />
    public Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(package.Id, package.Version);
            if (_sessions.TryGetValue(key, out var existing) && existing.IsLoaded)
            {
                loaded.Add(existing);
                continue;
            }

            try
            {
                var mainAssemblyPath = ResolveMainAssemblyPath(package.InstallPath, package.Id);
                var context = new PackageAssemblyLoadContext(mainAssemblyPath, sharedPolicy, _matcher);
                var assemblyName = AssemblyName.GetAssemblyName(mainAssemblyPath);
                context.LoadFromAssemblyName(assemblyName);

                _contexts[key] = context;

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null);

                _sessions[key] = session;
                loaded.Add(session);
            }
            catch (Exception ex)
            {
                failed[package.Id] = ex.Message;
                _sessions[key] = new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: false,
                    LastError: ex.Message);
            }
        }

        return Task.FromResult<PackageLoadResult>(new(loaded, failed));
    }

    /// <inheritdoc />
    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        _sessions.TryRemove(key, out _);

        if (_contexts.TryRemove(key, out var removed))
        {
            context = new(key, removed);
            return true;
        }

        context = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        if (_contexts.TryGetValue(key, out var existing))
        {
            context = new(key, existing);
            return true;
        }

        context = null;
        return false;
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    private static string? TryResolveTargetFrameworkMoniker(string assemblyPath, string installPath)
    {
        var relativePath = Path.GetRelativePath(installPath, assemblyPath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length >= 3 && string.Equals(segments[0], "lib", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1];
        }

        return null;
    }

    private static string ResolveMainAssemblyPath(string installPath, string packageId) =>
        ResolveMainAssemblyPath(installPath, packageId, hostTargetFrameworkOverride: null);

    private static string ResolveMainAssemblyPath(string installPath, string packageId, string? hostTargetFrameworkOverride)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            throw new DirectoryNotFoundException($"Install path '{installPath}' does not exist.");
        }

        var libPath = Path.Combine(installPath, "lib");
        if (Directory.Exists(libPath) && TryResolveFrameworkSpecificAssemblyPath(libPath, installPath, packageId, hostTargetFrameworkOverride, out var frameworkSpecificAssemblyPath))
        {
            return frameworkSpecificAssemblyPath;
        }

        var searchRoot = Directory.Exists(libPath) ? libPath : installPath;
        return ResolveAssemblyFromCandidates(
            Directory.EnumerateFiles(searchRoot, "*.dll", SearchOption.AllDirectories),
            installPath,
            packageId,
            selectedFramework: null);
    }

    private static bool TryResolveFrameworkSpecificAssemblyPath(
        string libPath,
        string installPath,
        string packageId,
        string? hostTargetFrameworkOverride,
        out string assemblyPath)
    {
        var frameworkDirectories = Directory
            .EnumerateDirectories(libPath)
            .Select(FrameworkDirectory.Create)
            .Where(candidate => candidate is not null)
            .Cast<FrameworkDirectory>()
            .ToArray();

        if (frameworkDirectories.Length == 0)
        {
            assemblyPath = string.Empty;
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

        assemblyPath = ResolveAssemblyFromCandidates(
            Directory.EnumerateFiles(selectedDirectory.Path, "*.dll", SearchOption.AllDirectories),
            installPath,
            packageId,
            selectedDirectory.FolderName);

        return true;
    }

    private static string ResolveAssemblyFromCandidates(
        IEnumerable<string> candidatePaths,
        string installPath,
        string packageId,
        string? selectedFramework)
    {
        var assemblies = candidatePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (assemblies.Length == 0)
        {
            throw new FileNotFoundException($"No loadable assembly found under '{installPath}'.");
        }

        if (assemblies.Length == 1)
        {
            return assemblies[0];
        }

        var matchingByPackageId = assemblies
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingByPackageId.Length == 1)
        {
            return matchingByPackageId[0];
        }

        var frameworkSuffix = selectedFramework is null ? string.Empty : $" for target framework '{selectedFramework}'";
        throw new InvalidOperationException(
            $"Multiple assemblies were found under '{installPath}'{frameworkSuffix}, and a main assembly could not be determined for package '{packageId}'. " +
            "Ensure the package contains a single loadable assembly for the selected target framework or that one assembly name matches the package ID.");
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
        var targetFrameworkName = typeof(PackageLoader).Assembly
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
