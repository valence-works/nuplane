using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;

namespace Nuplane.PackageArchives;

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
    private readonly TimeSpan _refusalRetention;
    private readonly TimeSpan _openTimeout;
    private readonly Func<string, FileStream> _openArchive;
    // Bounds the archive bytes held by loads in progress: at most this many archives are read at once.
    private readonly SemaphoreSlim _loadSlots = new(MaximumConcurrentLoads, MaximumConcurrentLoads);
    private const int MaximumConcurrentLoads = 4;
    private long _cachedBytes;

    public PackageArchiveReader(IOptions<PackageArchiveReaderOptions> options, ILogger<PackageArchiveReader> logger)
        : this(options, logger, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), openArchive: null)
    {
    }

    /// <summary>Test seam for the refusal retention, the open bound and the archive open itself.</summary>
    internal PackageArchiveReader(
        IOptions<PackageArchiveReaderOptions> options,
        ILogger<PackageArchiveReader> logger,
        TimeSpan refusalRetention,
        TimeSpan openTimeout,
        Func<string, FileStream>? openArchive)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _refusalRetention = refusalRetention;
        _openTimeout = openTimeout;
        _openArchive = openArchive ?? (static path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 81920, useAsync: true));
    }

    /// <inheritdoc />
    public async ValueTask<PackageArchive> OpenAsync(ActivePackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(package.InstallPath, nameof(package));
        cancellationToken.ThrowIfCancellationRequested();

        var recordedHash = package.ArchiveHash is { } hash && PackageInstallStore.ParseCanonicalContentHash(hash) is not null
            ? hash
            : null;
        var archivePath = ResolveArchivePath(package.InstallPath);
        var key = new CacheKey(package.PackageId.ToUpperInvariant(), package.Version.ToUpperInvariant(), archivePath,
            recordedHash ?? string.Empty);
        Task<PackageArchive>? load = null;
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
                    throw refusal.Error.Repeat();
                _refusals.Remove(key);
            }

            if (recordedHash is not null && !_loading.TryGetValue(key, out load))
            {
                // Task.Run starts the load on the thread pool, away from any caller SynchronizationContext.
                // Its cleanup takes _sync, so it cannot remove the entry before it is added here.
                load = Task.Run(() => LoadAsync(package, archivePath, recordedHash, key));
                _loading[key] = load;
                // Observe a failure even when every waiter has cancelled.
                _ = load.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        if (recordedHash is null)
        {
            // Built and logged outside the lock; a concurrent duplicate is harmless.
            var unrecorded = Refuse(package, PackageArchiveFailureReason.HashNotRecorded,
                "the active package record has no canonical sha512 archive hash; reconcile again, or reinstall the package with this Nuplane version.");
            lock (_sync)
                RememberRefusal(key, unrecorded);
            throw unrecorded;
        }

        return await load!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveArchivePath(string installPath) =>
        Path.GetFullPath(PackageInstallStore.GetArchivePath(installPath));

    private async Task<PackageArchive> LoadAsync(ActivePackage package, string archivePath, string recordedHash, CacheKey key)
    {
        try
        {
            byte[] bytes;
            string actualHash;
            await _loadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await ReadArchiveAsync(package, archivePath).ConfigureAwait(false);
                actualHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(bytes));
            }
            finally
            {
                _loadSlots.Release();
            }

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
                RememberRefusal(key, refusal);
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
                    $"'{archivePath}' is a directory; a kept archive is a regular file.");
            var info = new FileInfo(archivePath);
            if (info.LinkTarget is not null)
                throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                    $"'{archivePath}' is a link; a kept archive is a regular file.");
            // A path that cannot be inspected still goes to the open, which tells missing and denied apart.
            if (info.Exists && info.Length == 0)
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
        var open = Task.Run(() => _openArchive(archivePath));
        try
        {
            return await open.WaitAsync(_openTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            _ = open.ContinueWith(static task => task.Result.Dispose(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw Refuse(package, PackageArchiveFailureReason.ArchiveUnreadable,
                $"'{archivePath}' did not open within {_openTimeout.TotalSeconds:0} seconds; it is not a regular file.", exception);
        }
    }

    // Called under _sync. Expired refusals are swept here, so keys that are never retried do not accumulate.
    private void RememberRefusal(CacheKey key, PackageArchiveException refusal)
    {
        var now = Environment.TickCount64;
        foreach (var expired in _refusals.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _refusals.Remove(expired);
        _refusals[key] = (refusal, now + (long)_refusalRetention.TotalMilliseconds);
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
