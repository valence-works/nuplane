using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Owns one provider-created, locked control-file handle and its held-parent binding.</summary>
/// <remarks>
/// This is an ephemeral capability. Removal consumes the file handle after the native mutation attempt;
/// ordinary disposal unlocks and closes it without removing the entry. A held parent lease remains until
/// disposal so the provider can positively verify absence after removal.
/// </remarks>
internal sealed class PhysicalStoreLockedControlFile : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _creatorToken;
    private readonly PhysicalStoreFileHandle _file;
    private readonly PhysicalStoreSafeHandleLease _fileLease;
    private readonly PhysicalStoreSafeHandleLease _parentLease;
    private readonly Action<IntPtr> _unlock;
    private readonly PhysicalStoreCanonicalName _canonicalName;
    private LockedControlFileState _state = LockedControlFileState.Active;
    private bool _fileClosed;

    internal PhysicalStoreLockedControlFile(
        object creatorToken,
        PhysicalStoreFileHandle file,
        PhysicalStoreSafeHandleLease fileLease,
        PhysicalStoreSafeHandleLease parentLease,
        PhysicalStoreCanonicalName canonicalName,
        Action<IntPtr> unlock)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fileLease);
        ArgumentNullException.ThrowIfNull(parentLease);
        ArgumentNullException.ThrowIfNull(canonicalName);
        ArgumentNullException.ThrowIfNull(unlock);

        _creatorToken = creatorToken;
        _file = file;
        _fileLease = fileLease;
        _parentLease = parentLease;
        _canonicalName = canonicalName;
        _unlock = unlock;
    }

    internal PhysicalStoreCanonicalName CanonicalName => _canonicalName;

    /// <summary>
    /// Runs final replay and the native removal attempt atomically with respect to token disposal.
    /// The callback must call its mutation marker immediately before its native
    /// disposition/unlink call. Once marked, the file is closed directly after the callback, even if native
    /// code throws because its side effect may already have occurred. When supplied, <paramref name="afterClose"/>
    /// runs with the held parent lease after a successful mutation and direct file close, still under the same
    /// token gate. It does not run after an uncertain mutation error or a failed direct close.
    /// </summary>
    internal TResult WithRemovalAttempt<TResult>(
        object creatorToken,
        Func<IntPtr, IntPtr, Action, TResult> operation,
        Action<IntPtr>? afterClose = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            ValidateOwner(creatorToken);
            ValidateActive();
            var mutationAttempted = false;
            Action markMutationAttempted = () =>
            {
                if (mutationAttempted)
                    throw new InvalidOperationException("A locked control-file token supports one native removal attempt.");
                mutationAttempted = true;
                _state = LockedControlFileState.Consuming;
            };

            TResult result = default!;
            Exception? operationFailure = null;
            try
            {
                result = operation(_parentLease.DangerousHandle, _fileLease.DangerousHandle, markMutationAttempted);
            }
            catch (Exception exception)
            {
                operationFailure = exception;
            }

            if (mutationAttempted)
            {
                _state = LockedControlFileState.Consumed;
                try
                {
                    CloseFileDirectly();
                }
                catch (Exception closeFailure)
                {
                    if (operationFailure is not null)
                    {
                        throw new AggregateException(
                            "The native control-file removal attempt and direct handle close both failed.",
                            operationFailure,
                            closeFailure);
                    }

                    throw;
                }
            }

            if (operationFailure is not null)
                ExceptionDispatchInfo.Capture(operationFailure).Throw();

            if (mutationAttempted)
                afterClose?.Invoke(_parentLease.DangerousHandle);

            return result;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        lock (_gate)
        {
            if (_state == LockedControlFileState.Disposed)
                return ValueTask.CompletedTask;

            if (_state == LockedControlFileState.Active)
            {
                try
                {
                    _unlock(_fileLease.DangerousHandle);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (!_fileClosed)
            {
                try
                {
                    CloseFileDirectly();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            _state = LockedControlFileState.Disposed;
            try
            {
                _parentLease.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is null)
            return ValueTask.CompletedTask;
        if (failures.Count == 1)
            return ValueTask.FromException(failures[0]);
        return ValueTask.FromException(new AggregateException("The locked control-file capability could not release all owned resources.", failures));
    }

    private void CloseFileDirectly()
    {
        // Dispose the owner first. Releasing the dangerous reference below performs the actual
        // CloseHandle/close without issuing another file operation on the consumed native handle.
        List<Exception>? failures = null;
        try
        {
            _file.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _fileLease.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is null)
        {
            _fileClosed = true;
            return;
        }

        if (failures.Count == 1)
            throw failures[0];
        throw new AggregateException("The locked control-file handle could not be closed directly.", failures);
    }

    private void ValidateOwner(object creatorToken)
    {
        ArgumentNullException.ThrowIfNull(creatorToken);
        if (!ReferenceEquals(_creatorToken, creatorToken))
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The locked control-file capability belongs to a different filesystem provider.");
    }

    private void ValidateActive()
    {
        if (_state != LockedControlFileState.Active)
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.ExpiredScope,
                "The locked control-file capability has already been consumed or disposed.");
    }

    private enum LockedControlFileState
    {
        Active,
        Consuming,
        Consumed,
        Disposed
    }
}
