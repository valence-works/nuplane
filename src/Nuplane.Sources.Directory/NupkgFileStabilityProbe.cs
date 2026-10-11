using Microsoft.Extensions.Logging;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Sources.Directory;

/// <summary>
/// Probes whether a <c>.nupkg</c> file is stable (i.e., not currently being written).
/// Uses bounded retries with back-off to avoid consuming partially written artifacts.
/// </summary>
public sealed class NupkgFileStabilityProbe
{
    /// <summary>Default maximum number of stability check attempts before giving up.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Default delay between stability check attempts.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<NupkgFileStabilityProbe> _logger;
    private readonly Func<int, CancellationToken, Task>? _onBeforeRetryAsync;

    /// <summary>Initializes a new instance of <see cref="NupkgFileStabilityProbe"/>.</summary>
    /// <param name="logger">A logger for diagnostic output.</param>
    /// <param name="maxAttempts">Maximum retry attempts (default: 5).</param>
    /// <param name="retryDelay">Delay between retries (default: 200ms).</param>
    /// <param name="onBeforeRetryAsync">Optional callback before each retry; primarily useful for deterministic coordination in tests.</param>
    public NupkgFileStabilityProbe(
        ILogger<NupkgFileStabilityProbe> logger,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? retryDelay = null,
        Func<int, CancellationToken, Task>? onBeforeRetryAsync = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxAttempts = maxAttempts > 0
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Max attempts must be positive.");
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _onBeforeRetryAsync = onBeforeRetryAsync;
    }

    /// <summary>Probes one exact package-file basename through a positively Unenrolled native parent directory.</summary>
    /// <param name="filePath">The path to the <c>.nupkg</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the file is stable; otherwise <see langword="false"/>.</returns>
    public Task<bool> IsStableAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var (parentPath, basename) = SplitParentAndBasename(filePath);
        return DesiredSourceDirectoryAccess.WithDirectoryAsync(
            parentPath,
            borrow: null,
            session => IsStableAsync(session, basename, filePath, cancellationToken),
            cancellationToken);
    }

    internal async Task<bool> IsStableAsync(
        DesiredSourceDirectorySession session,
        string basename,
        string displayPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        PhysicalStoreNames.ValidateSingleComponent(basename);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.IsMissing)
            return false;

        long previousLength = -1;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Revalidate();
            var sample = session.SamplePackageCandidate(basename);
            switch (sample.Kind)
            {
                case PhysicalStoreDirectoryCandidateKind.Missing:
                    _logger.LogDebug(
                        "File '{FilePath}' not found on attempt {Attempt}/{MaxAttempts}; treating as unstable.",
                        displayPath, attempt, _maxAttempts);
                    return false;
                case PhysicalStoreDirectoryCandidateKind.Directory:
                    return false;
                case PhysicalStoreDirectoryCandidateKind.Busy:
                    _logger.LogDebug(
                        "File '{FilePath}' is locked on attempt {Attempt}/{MaxAttempts}.",
                        displayPath, attempt, _maxAttempts);
                    break;
                case PhysicalStoreDirectoryCandidateKind.RegularFile:
                    if (previousLength >= 0 && sample.Length == previousLength && sample.Length > 0)
                    {
                        _logger.LogDebug(
                            "File '{FilePath}' is stable after {Attempt} attempt(s) (size: {Size} bytes).",
                            displayPath, attempt, sample.Length);
                        return true;
                    }

                    previousLength = sample.Length;
                    break;
                default:
                    throw new InvalidOperationException("The native candidate sampler returned an undefined result.");
            }

            if (attempt == _maxAttempts)
                break;

            if (_onBeforeRetryAsync is not null)
            {
                await _onBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                session.Revalidate();
            }

            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            session.Revalidate();
        }

        _logger.LogWarning(
            "File '{FilePath}' did not stabilize after {MaxAttempts} attempts. Treating as unstable.",
            displayPath, _maxAttempts);
        return false;
    }

    private static (string ParentPath, string Basename) SplitParentAndBasename(string filePath)
    {
        var separator = Math.Max(
            filePath.LastIndexOf(Path.DirectorySeparatorChar),
            filePath.LastIndexOf(Path.AltDirectorySeparatorChar));
        var basename = separator < 0 ? filePath : filePath[(separator + 1)..];
        if (basename.Length == 0)
            throw new ArgumentException("The file path must end in one file name.", nameof(filePath));

        string parentPath;
        if (separator < 0)
        {
            parentPath = ".";
        }
        else if (separator == 0)
        {
            parentPath = filePath[..1];
        }
        else if (OperatingSystem.IsWindows() && separator == 2 && filePath.Length >= 3 && filePath[1] == ':')
        {
            parentPath = filePath[..3];
        }
        else
        {
            parentPath = filePath[..separator];
        }

        return (parentPath, basename);
    }
}
