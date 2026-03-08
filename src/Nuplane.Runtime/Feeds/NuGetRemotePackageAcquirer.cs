using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Runtime.Feeds;

/// <summary>
/// Downloads and prepares a concrete package version from a feed.
/// </summary>
public interface IRemotePackageAcquirer
{
    /// <summary>
    /// Acquires the requested package version from the specified feed.
    /// </summary>
    /// <param name="feed">The feed to download from.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The concrete package version.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The prepared installation directory path.</returns>
    Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken);
}

internal sealed class NuGetRemotePackageAcquirer(IOptions<FeedResolutionOptions> options) : IRemotePackageAcquirer
{
    private static readonly HttpClient HttpClient = new();
    private readonly FeedResolutionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public async Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(feed.Credentials))
        {
            throw new NotSupportedException(
                $"Feed '{feed.Name}' configures credentials, but remote credential resolution is not implemented yet.");
        }

        var installRoot = ResolveInstallRoot();
        var installDirectory = Path.Combine(
            installRoot,
            SanitizePathSegment(feed.Name),
            SanitizePathSegment(packageId),
            SanitizePathSegment(version));
        var completionMarkerPath = Path.Combine(installDirectory, ".nuplane-ready");

        if (File.Exists(completionMarkerPath))
        {
            return installDirectory;
        }

        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        var tempRoot = Path.Combine(installRoot, ".tmp");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);

        var tempNupkgPath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.nupkg");
        var tempExtractDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadPackageAsync(feed, packageId, version, tempNupkgPath, cancellationToken);

            Directory.CreateDirectory(tempExtractDirectory);
            ZipFile.ExtractToDirectory(tempNupkgPath, tempExtractDirectory, overwriteFiles: true);
            await File.WriteAllTextAsync(Path.Combine(tempExtractDirectory, ".nuplane-ready"), string.Empty, cancellationToken);
            Directory.Move(tempExtractDirectory, installDirectory);
            tempExtractDirectory = string.Empty;

            return installDirectory;
        }
        finally
        {
            if (File.Exists(tempNupkgPath))
            {
                File.Delete(tempNupkgPath);
            }

            if (!string.IsNullOrEmpty(tempExtractDirectory) && Directory.Exists(tempExtractDirectory))
            {
                Directory.Delete(tempExtractDirectory, recursive: true);
            }
        }
    }

    private static async Task DownloadPackageAsync(
        FeedDefinition feed,
        string packageId,
        string version,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var packageBaseAddress = await ResolvePackageBaseAddressAsync(feed.ServiceIndex, cancellationToken);
        var lowerPackageId = packageId.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var packageUri = new Uri(packageBaseAddress, $"{lowerPackageId}/{lowerVersion}/{lowerPackageId}.{lowerVersion}.nupkg");

        using var response = await HttpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"Package '{packageId}' version '{version}' was not found on remote feed '{feed.Name}'.",
                destinationPath);
        }

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<Uri> ResolvePackageBaseAddressAsync(Uri serviceIndex, CancellationToken cancellationToken)
    {
        await using var stream = await HttpClient.GetStreamAsync(serviceIndex, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"NuGet service index '{serviceIndex}' does not contain a resources array.");
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (!resource.TryGetProperty("@type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeProperty.GetString();
            if (type is null || !type.StartsWith("PackageBaseAddress/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resource.TryGetProperty("@id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idProperty.GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                return new(id, UriKind.Absolute);
            }
        }

        throw new InvalidOperationException(
            $"NuGet service index '{serviceIndex}' does not expose a PackageBaseAddress resource.");
    }

    private string ResolveInstallRoot() =>
        !string.IsNullOrWhiteSpace(_options.PackageInstallRoot)
            ? Path.GetFullPath(_options.PackageInstallRoot)
            : Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages");

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var buffer = value.Select(ch => invalidCharacters.Contains(ch) ? '_' : ch).ToArray();
        return new(buffer);
    }
}
