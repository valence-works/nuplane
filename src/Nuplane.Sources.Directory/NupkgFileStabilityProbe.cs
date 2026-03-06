using Microsoft.Extensions.Logging;

namespace Nuplane.Sources.Directory;

/// <summary>
/// Probes whether a <c>.nupkg</c> file is stable (i.e., not currently being written).
/// Uses bounded retries with back-off to avoid consuming partially written artifacts.
/// </summary>
public sealed class NupkgFileStabilityProbe
{
    /// <summary>
    /// Default maximum number of stability check attempts before giving up.
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Default delay between stability check attempts.
    /// </summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<NupkgFileStabilityProbe> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NupkgFileStabilityProbe"/>.
    /// </summary>
    /// <param name="logger">A logger for diagnostic output.</param>
    /// <param name="maxAttempts">Maximum retry attempts (default: 5).</param>
    /// <param name="retryDelay">Delay between retries (default: 200ms).</param>
    public NupkgFileStabilityProbe(
        ILogger<NupkgFileStabilityProbe> logger,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? retryDelay = null)
    {
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this._maxAttempts = maxAttempts > 0
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Max attempts must be positive.");
        this._retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    /// <summary>
    /// Probes the specified file path for stability. A file is considered stable
    /// when it can be opened for read with no sharing violations and has a
    /// consistent size across two consecutive checks.
    /// </summary>
    /// <param name="filePath">The absolute path to the <c>.nupkg</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the file is stable; <see langword="false"/> otherwise.</returns>
    public async Task<bool> IsStableAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        long previousSize = -1;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Try opening with FileShare.Read to detect write locks
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                var currentSize = stream.Length;

                if (previousSize >= 0 && currentSize == previousSize && currentSize > 0)
                {
                    _logger.LogDebug(
                        "File '{FilePath}' is stable after {Attempt} attempt(s) (size: {Size} bytes).",
                        filePath, attempt, currentSize);
                    return true;
                }

                previousSize = currentSize;
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                _logger.LogDebug(
                    ex,
                    "File '{FilePath}' is locked on attempt {Attempt}/{MaxAttempts}.",
                    filePath, attempt, _maxAttempts);
            }
            catch (FileNotFoundException)
            {
                _logger.LogDebug(
                    "File '{FilePath}' not found on attempt {Attempt}/{MaxAttempts}; treating as unstable.",
                    filePath, attempt, _maxAttempts);
                return false;
            }

            if (attempt < _maxAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        _logger.LogWarning(
            "File '{FilePath}' did not stabilize after {MaxAttempts} attempts. Treating as unstable.",
            filePath, _maxAttempts);
        return false;
    }
}
