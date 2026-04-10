using Nuplane.Abstractions;

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

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loader-candidate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }
}

