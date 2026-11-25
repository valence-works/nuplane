using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;

namespace Nuplane;

/// <summary>
/// Default <see cref="IPackageArchiveReader"/>: reads the archive kept in the install directory with ordinary
/// file access, verifies it against the recorded hash, and keeps verified archives in a bounded memory cache.
/// </summary>
internal sealed partial class PackageArchiveReader : IPackageArchiveReader
{
    private readonly PackageArchiveReaderOptions _options;
    private readonly ILogger<PackageArchiveReader> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _recency = new();
    private long _cachedBytes;

    public PackageArchiveReader(IOptions<PackageArchiveReaderOptions> options, ILogger<PackageArchiveReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PackageArchive> OpenAsync(ActivePackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(package.InstallPath, nameof(package));
        cancellationToken.ThrowIfCancellationRequested();

        if (package.ArchiveHash is not { } recordedHash || PackageInstallStore.ParseCanonicalContentHash(recordedHash) is null)
            throw Refuse(package, PackageArchiveFailureReason.HashNotRecorded,
                "the active package record has no canonical sha512 archive hash; reinstall the package with this Nuplane version.");

        var archivePath = ResolveArchivePath(package.InstallPath);
        var key = new CacheKey(package.PackageId.ToUpperInvariant(), package.Version.ToUpperInvariant(), archivePath, recordedHash);
        if (TryGetCached(key) is { } cached)
            return cached;

        var bytes = await ReadArchiveAsync(package, archivePath, cancellationToken).ConfigureAwait(false);
        var actualHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(bytes));
        if (!string.Equals(actualHash, recordedHash, StringComparison.Ordinal))
            throw Refuse(package, PackageArchiveFailureReason.HashMismatch,
                $"'{archivePath}' does not match the sha512 recorded at acquisition; it was modified or replaced.");

        PackageArchive archive;
        try
        {
            archive = PackageArchive.Load(package.PackageId, package.Version, recordedHash, bytes);
        }
        catch (InvalidDataException exception)
        {
            throw Refuse(package, PackageArchiveFailureReason.InvalidArchive,
                $"'{archivePath}' matches its recorded hash but is not a readable zip archive.", exception);
        }

        ArchiveVerified(_logger, package.PackageId, package.Version, bytes.LongLength);
        AddToCache(key, archive);
        return archive;
    }

    private static string ResolveArchivePath(string installPath) =>
        Path.GetFullPath(PackageInstallStore.GetArchivePath(installPath));

    private async Task<byte[]> ReadArchiveAsync(ActivePackage package, string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, useAsync: true);
            var length = stream.Length;
            if (length > _options.MaximumArchiveBytes)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveTooLarge,
                    $"'{archivePath}' is {length} bytes; the limit is {_options.MaximumArchiveBytes} (Nuplane:PackageArchives:MaximumArchiveBytes).");

            var bytes = new byte[length];
            try
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw Refuse(package, PackageArchiveFailureReason.HashMismatch,
                    $"'{archivePath}' was truncated while it was read.", exception);
            }

            return bytes;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Refuse(package, PackageArchiveFailureReason.ArchiveMissing,
                $"'{archivePath}' does not exist. Packages installed by a Nuplane version before archive retention have no kept archive; reinstall the package.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                $"'{archivePath}' could not be read: {exception.Message}", exception);
        }
    }

    private PackageArchive? TryGetCached(CacheKey key)
    {
        lock (_sync)
        {
            if (!_cache.TryGetValue(key, out var node))
                return null;
            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.Archive;
        }
    }

    private void AddToCache(CacheKey key, PackageArchive archive)
    {
        if (archive.Length > _options.CacheBytes)
            return;

        lock (_sync)
        {
            if (_cache.ContainsKey(key))
                return;

            while (_cachedBytes + archive.Length > _options.CacheBytes && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                _cachedBytes -= oldest.Value.Archive.Length;
            }

            _cache[key] = _recency.AddFirst(new CacheEntry(key, archive));
            _cachedBytes += archive.Length;
        }
    }

    private PackageArchiveException Refuse(
        ActivePackage package,
        PackageArchiveFailureReason reason,
        string detail,
        Exception? innerException = null)
    {
        ArchiveRefused(_logger, package.PackageId, package.Version, reason, detail);
        return new PackageArchiveException(package.PackageId, package.Version, reason, detail, innerException);
    }

    [LoggerMessage(EventId = 4501, Level = LogLevel.Warning,
        Message = "Package archive refused [PackageId={PackageId}, Version={Version}, Reason={Reason}]: {Detail}")]
    private static partial void ArchiveRefused(ILogger logger, string packageId, string version,
        PackageArchiveFailureReason reason, string detail);

    [LoggerMessage(EventId = 4502, Level = LogLevel.Debug,
        Message = "Verified package archive [PackageId={PackageId}, Version={Version}, Bytes={Bytes}]")]
    private static partial void ArchiveVerified(ILogger logger, string packageId, string version, long bytes);

    private readonly record struct CacheKey(string PackageId, string Version, string ArchivePath, string ArchiveHash);

    private sealed record CacheEntry(CacheKey Key, PackageArchive Archive);
}
