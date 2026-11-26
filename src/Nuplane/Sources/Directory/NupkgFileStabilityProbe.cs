using Microsoft.Extensions.Logging;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Sources.Directory;

/// <summary>
/// Probes whether a <c>.nupkg</c> file is stable (i.e., not currently being written).
/// Uses bounded retries to avoid consuming partially written artifacts.
/// </summary>
internal sealed class NupkgFileStabilityProbe
{
    /// <summary>Default maximum number of stability check attempts before giving up.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Default delay between stability check attempts.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(200);

    // Bound the number of Windows identity pins held across a retry interval.
    internal const int BatchSize = 32;

    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly ILogger<NupkgFileStabilityProbe> _logger;
    private readonly Func<int, CancellationToken, Task>? _onBeforeRetryAsync;

    /// <summary>Initializes a new instance of <see cref="NupkgFileStabilityProbe"/>.</summary>
    /// <param name="logger">A logger for diagnostic output.</param>
    /// <param name="maxAttempts">Maximum retry attempts (default: 5).</param>
    /// <param name="retryDelay">Delay between retries (default: 200ms).</param>
    /// <param name="onBeforeRetryAsync">Optional callback before each retry interval, shared by candidates in a discovery batch.</param>
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

    /// <summary>Probes the file using the ordinary filesystem's open/share and size-stability behavior.</summary>
    /// <param name="filePath">The path to the <c>.nupkg</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the file is stable; otherwise <see langword="false"/>.</returns>
    public Task<bool> IsStableAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return IsStableCoreAsync(filePath, cancellationToken);
    }

    internal Task<bool> IsStableGuardedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var (parentPath, basename) = SplitParentAndBasename(filePath);
        return DesiredSourceDirectoryAccess.WithDirectoryAsync(
            parentPath,
            borrow: null,
            session => IsStableCoreAsync(basename, cancellationToken, session, parentPath),
            cancellationToken);
    }

    private async Task<bool> IsStableCoreAsync(
        string candidate,
        CancellationToken cancellationToken,
        DesiredSourceDirectorySession? session = null,
        string? directoryPath = null)
        => (await ProbeFilesAsync([candidate], cancellationToken, session, directoryPath, releaseCompletedCandidates: false)
            .ConfigureAwait(false)).Count != 0;

    internal Task<IReadOnlyList<string>> GetStableFilesAsync(
        IEnumerable<string> filePaths, CancellationToken cancellationToken)
        => ProbeFilesAsync(filePaths, cancellationToken, session: null, directoryPath: null, releaseCompletedCandidates: true);

    internal Task<IReadOnlyList<string>> GetStableFilesAsync(
        DesiredSourceDirectorySession session,
        string directoryPath,
        IEnumerable<string> basenames,
        CancellationToken cancellationToken)
        => ProbeFilesAsync(basenames, cancellationToken, session, directoryPath, releaseCompletedCandidates: true);

    private async Task<IReadOnlyList<string>> ProbeFilesAsync(
        IEnumerable<string> candidates,
        CancellationToken cancellationToken,
        DesiredSourceDirectorySession? session,
        string? directoryPath,
        bool releaseCompletedCandidates)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is { IsMissing: true })
            return [];

        var stableFiles = new List<string>();
        foreach (var names in candidates.Chunk(BatchSize))
        {
            var batch = names.Select(name => new Candidate(
                name, directoryPath is null ? name : Path.Combine(directoryPath, name))).ToArray();
            var remaining = batch.Length;

            // Sample sequentially to keep data handles and access to the native session serialized.
            // Wait after the entire round so every pending candidate gets the full retry interval.
            for (var attempt = 1; remaining > 0; attempt++)
            {
                foreach (var candidate in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidate.IsStable is not null)
                        continue;

                    candidate.IsStable = ObserveCandidate(candidate, attempt, session);
                    if (candidate.IsStable is null && attempt == _maxAttempts)
                    {
                        _logger.LogWarning(
                            "File '{FilePath}' did not stabilize after {MaxAttempts} attempts. Treating as unstable.",
                            candidate.DisplayPath, _maxAttempts);
                        candidate.IsStable = false;
                    }

                    if (candidate.IsStable is not null)
                    {
                        remaining--;
                        // A standalone guarded probe keeps its pin through the final directory replay.
                        if (releaseCompletedCandidates)
                            session?.ReleasePackageCandidate(candidate.Name);
                    }
                }

                if (remaining == 0)
                    break;

                if (_onBeforeRetryAsync is not null)
                {
                    await _onBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    session?.Revalidate();
                }

                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                session?.Revalidate();
            }

            // Keep enumeration order for the existing version-selection tie break.
            foreach (var candidate in batch)
            {
                if (candidate.IsStable == true)
                    stableFiles.Add(candidate.Name);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return stableFiles;
    }

    private bool? ObserveCandidate(Candidate candidate, int attempt, DesiredSourceDirectorySession? session)
    {
        var (kind, length) = SampleCandidate(candidate, attempt, session);
        switch (kind)
        {
            case PhysicalStoreDirectoryCandidateKind.Missing:
                _logger.LogDebug(
                    "File '{FilePath}' not found on attempt {Attempt}/{MaxAttempts}; treating as unstable.",
                    candidate.DisplayPath, attempt, _maxAttempts);
                return false;
            case PhysicalStoreDirectoryCandidateKind.Directory:
                return false;
            case PhysicalStoreDirectoryCandidateKind.Busy:
                return null;
            case PhysicalStoreDirectoryCandidateKind.RegularFile:
                if (length > 0 && length == candidate.PreviousLength)
                {
                    _logger.LogDebug(
                        "File '{FilePath}' is stable after {Attempt} attempt(s) (size: {Size} bytes).",
                        candidate.DisplayPath, attempt, length);
                    return true;
                }

                candidate.PreviousLength = length;
                return null;
            default:
                throw new InvalidOperationException("The candidate sampler returned an undefined result.");
        }
    }

    private (PhysicalStoreDirectoryCandidateKind Kind, long Length) SampleCandidate(
        Candidate candidate, int attempt, DesiredSourceDirectorySession? session)
    {
        var filePath = candidate.DisplayPath;
        if (session is not null)
        {
            var sample = session.SamplePackageCandidate(candidate.Name);
            if (sample.Kind == PhysicalStoreDirectoryCandidateKind.Busy)
            {
                _logger.LogDebug(
                    "File '{FilePath}' is locked on attempt {Attempt}/{MaxAttempts}.",
                    filePath, attempt, _maxAttempts);
            }

            return (sample.Kind, sample.Length);
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (PhysicalStoreDirectoryCandidateKind.RegularFile, stream.Length);
        }
        catch (FileNotFoundException)
        {
            return (PhysicalStoreDirectoryCandidateKind.Missing, 0);
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception,
                "File '{FilePath}' is locked on attempt {Attempt}/{MaxAttempts}.",
                filePath, attempt, _maxAttempts);
            return (PhysicalStoreDirectoryCandidateKind.Busy, 0);
        }
    }

    private sealed class Candidate(string name, string displayPath)
    {
        internal string Name { get; } = name;
        internal string DisplayPath { get; } = displayPath;
        internal long PreviousLength { get; set; } = -1;
        internal bool? IsStable { get; set; }
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
