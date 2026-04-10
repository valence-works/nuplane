using Nuplane.Loading;

namespace Nuplane.Sample.AspNetCore;

internal static class AssemblyCatalogResponses
{
    public static AssemblyCatalogPackageResponse FromEntry(PackageAssemblyCatalogEntry package) =>
        new(
            package.PackageId,
            package.Version,
            package.Assemblies
                .Select(static assembly => new AssemblyDescriptorResponse(
                    assembly.GetName().Name ?? assembly.FullName ?? "<unknown>",
                    assembly.Location))
                .OrderBy(static assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static assembly => assembly.Name, StringComparer.Ordinal)
                .ToArray(),
            package.ScanCandidates
                .Select(static candidate => candidate.AssemblyFileName)
                .ToArray());

    public static AssemblyCatalogNotFoundResponse MissingPackage(string packageId) =>
        new(packageId, null, "package-not-active-or-not-loaded");

    public static AssemblyCatalogNotFoundResponse MissingPackageVersion(string packageId, string version) =>
        new(packageId, version, "package-not-active-or-not-loaded");
}

internal sealed record AssemblyCatalogPackageResponse(
    string PackageId,
    string Version,
    IReadOnlyList<AssemblyDescriptorResponse> Assemblies,
    IReadOnlyList<string> ScanCandidates);

internal sealed record AssemblyDescriptorResponse(
    string Name,
    string Location);

internal sealed record AssemblyCatalogNotFoundResponse(
    string PackageId,
    string? Version,
    string Reason);

