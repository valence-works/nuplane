using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Store.State;

/// <summary>
/// The file-backed <see cref="IStoreLock"/>: an exclusive handle on a zero-length lock file beside
/// the store's state file.
/// </summary>
/// <remarks>
/// <para>
/// The lock file is the resolved state file path plus <see cref="LockFileSuffix"/>, so it lives in
/// the state directory and never inside an install root. Nothing in Nuplane enumerates the state
/// directory, and every enumeration of an install directory filters by extension
/// (<c>*.dll</c>, <c>*.nuspec</c>, <c>*.nupkg</c>), so the lock file cannot be mistaken for a
/// package, a completion marker, or a state artefact.
/// </para>
/// <para>
/// Exclusion comes from opening the file with <see cref="FileShare.None"/>, which the runtime
/// enforces across processes on Windows and through an advisory <c>flock</c> on Unix. It also
/// excludes a second handle inside one process, so two Nuplane compositions in the same process are
/// serialized the same way two processes are.
/// </para>
/// <para>
/// Acquisition separates "cannot lock" from "someone else holds it" by creating the lock file first
/// and only then locking it: a failure to create is a capability problem and yields
/// <see cref="StoreLockOutcome.NotLockable"/>, while a failure to open the existing file
/// exclusively is contention and yields <see cref="StoreLockOutcome.Unavailable"/>. A permission
/// failure on the second step is a capability problem too, and is classified as such.
/// </para>
/// </remarks>
internal sealed partial class StoreLock : IStoreLock
{
    /// <summary>
    /// The suffix appended to a resolved state file path to name its lock file.
    /// </summary>
    public const string LockFileSuffix = ".lock";

    private readonly string? _lockFilePath;
    private readonly ILogger<StoreLock> _logger;

    /// <summary>
    /// Initializes the store lock for the store the supplied persistence settings resolve to.
    /// </summary>
    /// <param name="persistenceSettings">The resolved persistence settings naming the state file, if any.</param>
    /// <param name="reconciliationOptions">The reconciliation options carrying <see cref="ReconciliationOptions.EnableStoreLock"/>.</param>
    /// <param name="logger">The logger used to report contention and unlockable stores.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is <see langword="null"/>.</exception>
    public StoreLock(
        EffectiveStorePersistenceSettings persistenceSettings,
        IOptions<ReconciliationOptions> reconciliationOptions,
        ILogger<StoreLock> logger)
    {
        ArgumentNullException.ThrowIfNull(persistenceSettings);
        ArgumentNullException.ThrowIfNull(reconciliationOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _lockFilePath = reconciliationOptions.Value.EnableStoreLock
            && persistenceSettings.ResolvedStateFilePath is { } stateFilePath
                ? GetLockFilePath(stateFilePath)
                : null;
    }

    /// <summary>
    /// Names the lock file that guards the store persisted at <paramref name="stateFilePath"/>.
    /// </summary>
    /// <param name="stateFilePath">The resolved state file path.</param>
    /// <returns>The lock file path beside the state file.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stateFilePath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static string GetLockFilePath(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

        return stateFilePath + LockFileSuffix;
    }

    /// <inheritdoc />
    public StoreLockHandle Acquire()
    {
        if (_lockFilePath is null)
        {
            return StoreLockHandle.NotRequired();
        }

        if (!TryEnsureLockFileExists(_lockFilePath, out var createFailure))
        {
            LogStoreNotLockable(_logger, _lockFilePath, createFailure!.Message);
            return new(StoreLockOutcome.NotLockable, _lockFilePath, null);
        }

        try
        {
            return new(
                StoreLockOutcome.Acquired,
                _lockFilePath,
                new FileStream(_lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        catch (UnauthorizedAccessException ex)
        {
            // The lock file exists but this process may not write it. That is a capability problem,
            // not contention, so it must not be reported as "someone else is reconciling".
            LogStoreNotLockable(_logger, _lockFilePath, ex.Message);
            return new(StoreLockOutcome.NotLockable, _lockFilePath, null);
        }
        catch (IOException)
        {
            LogStoreLockHeldElsewhere(_logger, _lockFilePath);
            return new(StoreLockOutcome.Unavailable, _lockFilePath, null);
        }
    }

    /// <summary>
    /// Creates the lock file and its directory when they are absent, leaving an existing file
    /// untouched so that probing never disturbs a holder. Returns <see langword="false"/> only when
    /// the file genuinely could not be brought into existence.
    /// </summary>
    private static bool TryEnsureLockFileExists(string lockFilePath, out Exception? failure)
    {
        failure = null;
        if (File.Exists(lockFilePath))
        {
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(lockFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var _ = new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Another process may have created — and locked — the file between the probe and the
            // create, which is not a failure to create at all.
            if (File.Exists(lockFilePath))
            {
                return true;
            }

            failure = ex;
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Another process is reconciling the store guarded by {LockFilePath}; this cycle did nothing.")]
    private static partial void LogStoreLockHeldElsewhere(ILogger logger, string lockFilePath);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The store lock file {LockFilePath} could not be created or opened ({Reason}); reconciling without cross-process protection.")]
    private static partial void LogStoreNotLockable(ILogger logger, string lockFilePath, string reason);
}
