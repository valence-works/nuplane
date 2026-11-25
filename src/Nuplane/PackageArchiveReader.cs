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
    private readonly Dictionary<CacheKey, Task<PackageArchive>> _loading = new();
    private readonly Dictionary<CacheKey, (PackageArchiveException Error, long ExpiresAt)> _refusals = new();
    private static readonly TimeSpan RefusalRetention = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
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
        Task<PackageArchive> load;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Archive;
            }

            // A refused archive is not read, hashed or logged again for a few seconds.
            if (_refusals.TryGetValue(key, out var refusal))
            {
                if (Environment.TickCount64 < refusal.ExpiresAt)
                    throw refusal.Error;
                _refusals.Remove(key);
            }

            // Concurrent opens of one archive share a single read and verification. The shared load does not
            // take any caller's token, so one caller cancelling cannot fail the others.
            if (!_loading.TryGetValue(key, out load!))
            {
                load = LoadAsync(package, archivePath, recordedHash, key);
                _loading[key] = load;
            }
        }

        return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveArchivePath(string installPath) =>
        Path.GetFullPath(PackageInstallStore.GetArchivePath(installPath));

    private async Task<PackageArchive> LoadAsync(ActivePackage package, string archivePath, string recordedHash, CacheKey key)
    {
        try
        {
            await Task.Yield();
            var bytes = await ReadArchiveAsync(package, archivePath).ConfigureAwait(false);
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
        catch (PackageArchiveException refusal)
        {
            lock (_sync)
                _refusals[key] = (refusal, Environment.TickCount64 + (long)RefusalRetention.TotalMilliseconds);
            throw;
        }
        finally
        {
            lock (_sync)
                _loading.Remove(key);
        }
    }

    private async Task<byte[]> ReadArchiveAsync(ActivePackage package, string archivePath)
    {
        try
        {
            // Opening a FIFO or device blocks or never ends. A kept archive is a non-empty regular file, so
            // refuse anything else before opening it, and again from the opened handle.
            if (Directory.Exists(archivePath))
                throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                    $"'{archivePath}' is a directory, not an archive file.");
            var info = new FileInfo(archivePath);
            if (info.LinkTarget is not null)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                    $"'{archivePath}' is a link; a kept archive is a regular file.");
            if (!info.Exists)
                throw new FileNotFoundException("The kept package archive does not exist.", archivePath);
            if (info.Length == 0)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                    $"'{archivePath}' is empty or is not a regular file.");

            await using var stream = await OpenBoundedAsync(package, archivePath).ConfigureAwait(false);
            if (!stream.CanSeek || stream.Length == 0)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                    $"'{archivePath}' is empty or is not a regular file.");

            var length = stream.Length;
            if (length > _options.MaximumArchiveBytes)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveTooLarge,
                    $"'{archivePath}' is {length} bytes; the limit is {_options.MaximumArchiveBytes} (Nuplane:PackageArchives:MaximumArchiveBytes).");

            var bytes = new byte[length];
            try
            {
                await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
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

    // The checks above cannot stop a file from being swapped for a FIFO just before it is opened, and opening a
    // FIFO blocks until a writer appears. Bound the open so such a swap is refused instead of hanging the load.
    private async Task<FileStream> OpenBoundedAsync(ActivePackage package, string archivePath)
    {
        var open = Task.Run(() => new FileStream(archivePath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        try
        {
            return await open.WaitAsync(OpenTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            _ = open.ContinueWith(static task => task.Result.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                $"'{archivePath}' did not open within {OpenTimeout.TotalSeconds:0} seconds; it is not a regular file.", exception);
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
