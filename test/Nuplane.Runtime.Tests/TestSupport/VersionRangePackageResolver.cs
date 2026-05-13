using Nuplane.Abstractions;
using NuGet.Versioning;

namespace Nuplane.Runtime.Tests.TestSupport;

internal sealed class VersionRangePackageResolver(IReadOnlyDictionary<string, IReadOnlyList<ResolvedPackage>> packages) : IPackageResolver
{
    public List<PackageRequest> Requests { get; } = [];

    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!packages.TryGetValue(request.Id, out var candidates))
        {
            return Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' was not configured."));
        }

        if (NuGetVersion.TryParse(request.VersionRange, out _) &&
            !request.VersionRange.StartsWith('[') &&
            !request.VersionRange.StartsWith('('))
        {
            return Task.FromException<ResolvedPackage>(new InvalidOperationException(
                $"Version range '{request.VersionRange}' must be normalized by the caller before using {nameof(VersionRangePackageResolver)}."));
        }

        if (!VersionRange.TryParse(request.VersionRange, out var range))
        {
            return Task.FromException<ResolvedPackage>(new InvalidOperationException($"Version range '{request.VersionRange}' is invalid."));
        }

        var match = candidates
            .Select(package => new
            {
                Package = package,
                Version = NuGetVersion.Parse(package.Version)
            })
            .Where(candidate => range.Satisfies(candidate.Version))
            .OrderBy(candidate => candidate.Version)
            .FirstOrDefault();

        return match is null
            ? Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' had no candidate for '{request.VersionRange}'."))
            : Task.FromResult(match.Package);
    }
}
