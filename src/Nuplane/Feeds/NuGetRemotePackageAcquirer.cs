using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;

namespace Nuplane.Feeds;

/// <inheritdoc />
/// <remarks>
/// A feed that declares <c>Credentials</c> is authenticated per request through
/// <paramref name="secretReferenceResolver"/>; one whose reference no registered provider resolves is
/// refused by name rather than contacted anonymously. The resolver parameter is optional so that a
/// hand-composed acquirer still works — with no resolver, every credentialed feed is refused, which
/// is what such a feed did before credentials could be resolved at all.
/// </remarks>
public sealed class NuGetRemotePackageAcquirer(
    IOptions<FeedResolutionOptions> options,
    ISecretReferenceResolver? secretReferenceResolver = null) : IRemotePackageAcquirer
{
    private static readonly HttpClient HttpClient = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPackageBaseAddress>>> _packageBaseAddressCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly FeedResolutionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly ISecretReferenceResolver? _secretReferenceResolver = secretReferenceResolver;

    /// <inheritdoc />
    /// <exception cref="FeedCredentialUnavailableException">Thrown when the feed declares credentials that could not be resolved. The feed is refused before anything is read from the install store or the network, so a package already installed from it is not served either.</exception>
    public async Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        // Ahead of the install-store short-circuit on purpose: a refused feed must not serve even a
        // package an earlier, authenticated run left on disk, because the refusal is about the feed's
        // eligibility, not about this one download.
        var credentials = await FeedCredentials.ResolveAsync(_secretReferenceResolver, feed, cancellationToken).ConfigureAwait(false);
        if (credentials.IsRefused)
        {
            throw new FeedCredentialUnavailableException(feed.Name);
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
                credentials.Credential,
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
        FeedCredential? credential,
        CancellationToken cancellationToken)
    {
        var packageBaseAddress = await GetPackageBaseAddressAsync(
            feed.ServiceIndex,
            packageBaseAddressCacheTtl,
            credential,
            cancellationToken);
        var lowerPackageId = packageId.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var packageUri = new Uri(packageBaseAddress, $"{lowerPackageId}/{lowerVersion}/{lowerPackageId}.{lowerVersion}.nupkg");

        using var request = CreateRequest(packageUri, credential);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        FeedCredential? credential,
        CancellationToken cancellationToken)
    {
        if (cacheTtl == TimeSpan.Zero)
        {
            return await ResolvePackageBaseAddressAsync(serviceIndex, credential, cancellationToken);
        }

        var key = serviceIndex.AbsoluteUri;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            // The cached entry holds the discovered base address and nothing else: the credential is
            // captured only by the in-flight fetch, which Lazy releases once it has produced a value.
            var pending = _packageBaseAddressCache.GetOrAdd(
                key,
                static (_, state) => new(
                    () => ResolvePackageBaseAddressCacheEntryAsync(
                        state.ServiceIndex,
                        state.ExpiresAt,
                        state.Credential,
                        CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                (ServiceIndex: serviceIndex, ExpiresAt: now.Add(cacheTtl), Credential: credential));

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
        FeedCredential? credential,
        CancellationToken cancellationToken) =>
        new(await ResolvePackageBaseAddressAsync(serviceIndex, credential, cancellationToken), expiresAt);

    private static async Task<Uri> ResolvePackageBaseAddressAsync(
        Uri serviceIndex,
        FeedCredential? credential,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(serviceIndex, credential);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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

    /// <summary>
    /// Builds one request carrying this feed's credential. The <c>Authorization</c> header goes on the
    /// request, never on the shared client's default headers, so one feed's secret is never sent to
    /// another feed.
    /// </summary>
    private static HttpRequestMessage CreateRequest(Uri uri, FeedCredential? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (credential is not null)
        {
            request.Headers.Authorization = credential.ToBasicAuthenticationHeader();
        }

        return request;
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
