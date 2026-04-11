using Nuplane.Abstractions;
using System.Runtime.Versioning;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderCatalogCandidateTests
{
    [Fact]
    public void BuildScanCandidates_ReturnsDeterministicPrimaryAssemblyFirstByKind()
    {
        var loader = new PackageLoader();
        var package = CreateResolvedPackage("pkg-catalog", "1.0.0");

        var candidates = loader.BuildScanCandidates(package.Id, package.InstallPath);

        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, candidate => candidate.CandidateKind == "PrimaryLoadAssembly");
        Assert.Equal(
            candidates.OrderBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase).Select(candidate => candidate.AssemblyPath),
            candidates.Select(candidate => candidate.AssemblyPath));
    }

    [Fact]
    public void BuildScanCandidates_ForMultiTargetPackage_ReturnsOnlySelectedFrameworkAssetsWithPerCandidateFrameworkMetadata()
    {
        var loader = new PackageLoader();
        var packageId = "pkg-multi-target";
        var selectedFramework = GetHostFrameworkFolderName();
        var otherFramework = GetAlternateFrameworkFolderName(selectedFramework);
        var package = CreateResolvedPackage(
            packageId,
            "1.0.0",
            [
                (selectedFramework, [packageId, "host-helper"]),
                (otherFramework, [packageId, "other-helper"])
            ]);

        var candidates = loader.BuildScanCandidates(package.Id, package.InstallPath);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{selectedFramework}{Path.DirectorySeparatorChar}", candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{otherFramework}{Path.DirectorySeparatorChar}", candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(selectedFramework, candidate.TargetFrameworkMoniker);
        });

        var primary = Assert.Single(candidates, candidate => candidate.CandidateKind == "PrimaryLoadAssembly");
        Assert.Equal($"{packageId}.dll", primary.AssemblyFileName);
        Assert.Equal("selected-by-loader", primary.SelectionReason);
    }

    [Fact]
    public void BuildScanCandidates_ForNonFrameworkLibSubdirectory_DoesNotInferBogusTargetFrameworkMetadata()
    {
        var loader = new PackageLoader();
        var packageId = "pkg-non-framework";
        var package = CreateResolvedPackage(
            packageId,
            "1.0.0",
            [("common", [packageId, "helper"])]);

        var candidates = loader.BuildScanCandidates(package.Id, package.InstallPath);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Null(candidate.TargetFrameworkMoniker));
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loader-candidate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private static ResolvedPackage CreateResolvedPackage(
        string id,
        string version,
        IReadOnlyList<(string Folder, IReadOnlyList<string> AssemblyNames)> folders)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loader-candidate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var (folder, assemblyNames) in folders)
        {
            var targetDirectory = Directory.CreateDirectory(Path.Combine(root, "lib", folder));
            foreach (var assemblyName in assemblyNames)
            {
                CopyAssembly(targetDirectory.FullName, assemblyName);
            }
        }

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private static void CopyAssembly(string destinationDirectory, string assemblyName)
    {
        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(destinationDirectory, $"{assemblyName}.dll"), overwrite: true);
    }

    private static string GetHostFrameworkFolderName()
    {
        var attribute = typeof(PackageLoaderCatalogCandidateTests).Assembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .OfType<TargetFrameworkAttribute>()
            .Single();

        var frameworkName = new FrameworkName(attribute.FrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETStandard" => $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETFramework" => $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}",
            _ => throw new InvalidOperationException($"Unsupported test host framework '{attribute.FrameworkName}'.")
        };
    }

    private static string GetAlternateFrameworkFolderName(string hostFrameworkFolder)
    {
        if (hostFrameworkFolder.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(hostFrameworkFolder, "netstandard2.0", StringComparison.OrdinalIgnoreCase)
                ? "netstandard1.0"
                : "netstandard2.0";
        }

        if (hostFrameworkFolder.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && hostFrameworkFolder.Contains('.', StringComparison.Ordinal))
        {
            var versionText = hostFrameworkFolder[3..];
            if (Version.TryParse(versionText, out var version))
            {
                var alternateMajor = version.Major > 1 ? version.Major - 1 : version.Major + 1;
                return $"net{alternateMajor}.{version.Minor}";
            }
        }

        return string.Equals(hostFrameworkFolder, "net472", StringComparison.OrdinalIgnoreCase)
            ? "net45"
            : "net472";
    }
}

