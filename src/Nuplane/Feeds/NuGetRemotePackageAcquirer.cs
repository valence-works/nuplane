using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Feeds;

/// <inheritdoc />
public sealed class NuGetRemotePackageAcquirer(IOptions<FeedResolutionOptions> options) : IRemotePackageAcquirer
{
    private static readonly HttpClient HttpClient = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPackageBaseAddress>>> _packageBaseAddressCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly FeedResolutionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
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

        var installRoot = PackageInstallStore.ResolveInstallRoot(_options);
        var installDirectory = PackageInstallStore.GetInstallDirectory(installRoot, feed.Name, packageId, version);

        if (PackageInstallStore.IsInstalled(installDirectory))
        {
            return installDirectory;
        }

        var stagedNupkgPath = PackageInstallStore.CreateStagingPath(installRoot, ".nupkg");

        try
        {
            await DownloadPackageAsync(
                feed,
                packageId,
                version,
                stagedNupkgPath,
                _options.PackageBaseAddressCacheTtl,
                cancellationToken);

            await PackageInstallStore.InstallAsync(installRoot, installDirectory, stagedNupkgPath, cancellationToken);

            return installDirectory;
        }
        finally
        {
            if (File.Exists(stagedNupkgPath))
            {
                File.Delete(stagedNupkgPath);
            }
        }
    }

    private async Task DownloadPackageAsync(
        FeedDefinition feed,
        string packageId,
        string version,
        string destinationPath,
        TimeSpan packageBaseAddressCacheTtl,
        CancellationToken cancellationToken)
    {
        var packageBaseAddress = await GetPackageBaseAddressAsync(
            feed.ServiceIndex,
            packageBaseAddressCacheTtl,
            cancellationToken);
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

    private async Task<Uri> GetPackageBaseAddressAsync(
        Uri serviceIndex,
        TimeSpan cacheTtl,
        CancellationToken cancellationToken)
    {
        if (cacheTtl == TimeSpan.Zero)
        {
            return await ResolvePackageBaseAddressAsync(serviceIndex, cancellationToken);
        }

        var key = serviceIndex.AbsoluteUri;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var pending = _packageBaseAddressCache.GetOrAdd(
                key,
                static (_, state) => new(
                    () => ResolvePackageBaseAddressCacheEntryAsync(
                        state.ServiceIndex,
                        state.ExpiresAt,
                        CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                (ServiceIndex: serviceIndex, ExpiresAt: now.Add(cacheTtl)));

            CachedPackageBaseAddress entry;
            try
            {
                entry = await pending.Value.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _packageBaseAddressCache.TryRemove(new KeyValuePair<string, Lazy<Task<CachedPackageBaseAddress>>>(key, pending));
                throw;
            }

            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return entry.Uri;
            }

            _packageBaseAddressCache.TryRemove(new KeyValuePair<string, Lazy<Task<CachedPackageBaseAddress>>>(key, pending));
        }
    }

    private static async Task<CachedPackageBaseAddress> ResolvePackageBaseAddressCacheEntryAsync(
        Uri serviceIndex,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken) =>
        new(await ResolvePackageBaseAddressAsync(serviceIndex, cancellationToken), expiresAt);

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
                return EnsureTrailingSlash(new(id, UriKind.Absolute));
            }
        }

        throw new InvalidOperationException(
            $"NuGet service index '{serviceIndex}' does not expose a PackageBaseAddress resource.");
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(value + "/", UriKind.Absolute);
    }

    private sealed record CachedPackageBaseAddress(Uri Uri, DateTimeOffset ExpiresAt);
}
