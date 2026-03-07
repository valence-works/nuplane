using Nuplane.Abstractions;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Feeds;

/// <summary>
/// Resolves package requests by extracting version information from NuGet version range strings
/// and producing resolved package records with synthetic install paths.
/// </summary>
public sealed class NuGetPackageResolver : INuGetPackageResolver
{
    /// <inheritdoc />
    public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedVersion = NuGetVersionRangeParser.SelectVersion(request.VersionRange);
        var resolved = new ResolvedPackage(
            request.Id,
            selectedVersion,
            request.FeedName ?? "default",
            $"/packages/{request.Id}/{selectedVersion}",
            DateTimeOffset.UtcNow,
            request.SourceName);

        return Task.FromResult(resolved);
    }
}

