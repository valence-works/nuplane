using System.Runtime.InteropServices;

namespace Nuplane.Loading;

/// <summary>Owns each native library handle returned for paths in one package graph context.</summary>
internal sealed class NativeLibraryHandlePool : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IntPtr> _handles = new(StringComparer.Ordinal);
    private readonly Func<string, IntPtr> _load;
    private readonly Action<IntPtr> _free;
    private bool _releaseAttempted;
    private Exception? _releaseFailure;

    internal NativeLibraryHandlePool()
        : this(static path => NativeLibrary.Load(path), static handle => NativeLibrary.Free(handle))
    {
    }

    internal NativeLibraryHandlePool(Func<string, IntPtr> load, Action<IntPtr> free)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _free = free ?? throw new ArgumentNullException(nameof(free));
    }

    internal IntPtr Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            if (_releaseAttempted)
                throw new ObjectDisposedException(nameof(NativeLibraryHandlePool));
            if (_handles.TryGetValue(path, out var existing))
                return existing;

            var handle = _load(path);
            if (handle == IntPtr.Zero)
                throw new DllNotFoundException($"The native library at '{path}' did not produce a valid handle.");
            _handles.Add(path, handle);
            return handle;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_releaseAttempted)
                return _releaseFailure is null
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(_releaseFailure);

            _releaseAttempted = true;
            try
            {
                foreach (var handle in _handles.Values)
                    _free(handle);
                _handles.Clear();
                return ValueTask.CompletedTask;
            }
            catch (Exception exception)
            {
                // Free has no reliable rollback contract. Keep the pool strongly retained and
                // never retry an uncertain handle release.
                _releaseFailure = exception;
                return ValueTask.FromException(exception);
            }
        }
    }
}
