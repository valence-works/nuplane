namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Runs bounded stream operations through provider-held parent and file handle leases.</summary>
internal sealed class BoundedPhysicalStoreFileStream : Stream
{
    private const int MaximumIoChunkBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly PhysicalStoreSafeHandleLease _parentLease;
    private readonly PhysicalStoreSafeHandleLease _fileLease;
    private readonly long _maximumBytes;
    private readonly long _readLength;
    private readonly Action<long> _validateBinding;
    private readonly Func<long, byte[], int, int, int> _readAt;
    private readonly Func<long, byte[], int, int, int> _writeAt;
    private readonly Action _flush;
    private readonly bool _writable;
    private long _position;
    private bool _disposed;

    internal BoundedPhysicalStoreFileStream(
        PhysicalStoreSafeHandleLease parentLease,
        PhysicalStoreSafeHandleLease fileLease,
        long maximumBytes,
        long readLength,
        bool writable,
        Action<long> validateBinding,
        Func<long, byte[], int, int, int> readAt,
        Func<long, byte[], int, int, int> writeAt,
        Action flush)
    {
        ArgumentNullException.ThrowIfNull(parentLease);
        ArgumentNullException.ThrowIfNull(fileLease);
        ArgumentNullException.ThrowIfNull(validateBinding);
        ArgumentNullException.ThrowIfNull(readAt);
        ArgumentNullException.ThrowIfNull(writeAt);
        ArgumentNullException.ThrowIfNull(flush);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (readLength < 0 || readLength > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(readLength));

        _parentLease = parentLease;
        _fileLease = fileLease;
        _maximumBytes = maximumBytes;
        _readLength = readLength;
        _writable = writable;
        _validateBinding = validateBinding;
        _readAt = readAt;
        _writeAt = writeAt;
        _flush = flush;
    }

    ~BoundedPhysicalStoreFileStream()
    {
        try
        {
            Dispose(disposing: false);
        }
        catch
        {
            // Finalizer cleanup must not terminate the process if native lease release fails.
        }
    }

    public override bool CanRead => !_disposed && !_writable;
    public override bool CanSeek => !_disposed && !_writable;
    public override bool CanWrite => !_disposed && _writable;

    public override long Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _writable ? _position : _readLength;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _position;
            }
        }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_writable)
                    throw new NotSupportedException("Package-file write streams are sequential and cannot be repositioned.");
                if (value < 0 || value > _readLength)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _validateBinding(_readLength);
                _position = value;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The buffer offset and count exceed the array length.");

        lock (_gate)
        {
            EnsureReadable();
            _validateBinding(_readLength);
            if (count == 0 || _position == _readLength)
                return 0;

            var requested = checked((int)Math.Min(Math.Min(count, MaximumIoChunkBytes), _readLength - _position));
            var read = _readAt(_position, buffer, offset, requested);
            if (read <= 0 || read > requested)
                throw new IOException("The package archive ended before its verified length or returned an invalid byte count.");

            _position += read;
            _validateBinding(_readLength);
            return read;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return Read(Array.Empty<byte>(), 0, 0);
        var temporary = new byte[Math.Min(buffer.Length, MaximumIoChunkBytes)];
        var read = Read(temporary, 0, temporary.Length);
        temporary.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment) && segment.Array is not null)
            return ValueTask.FromResult(Read(segment.Array, segment.Offset, segment.Count));

        var temporary = new byte[Math.Min(buffer.Length, MaximumIoChunkBytes)];
        var read = Read(temporary, 0, temporary.Length);
        temporary.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteCore(buffer, offset, count, CancellationToken.None);

    private void WriteCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The buffer offset and count exceed the array length.");

        lock (_gate)
        {
            EnsureWritable();
            if (count > _maximumBytes - _position)
                throw new IOException("The package-file stream exceeded its explicit byte quota.");
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = Math.Min(count, MaximumIoChunkBytes);
                WriteChunk(buffer, offset, chunk, cancellationToken);
                offset += chunk;
                count -= chunk;
            }
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (buffer.Length > _maximumBytes - _position)
                throw new IOException("The package-file stream exceeded its explicit byte quota.");
            while (!buffer.IsEmpty)
            {
                var chunk = Math.Min(buffer.Length, MaximumIoChunkBytes);
                var bytes = buffer[..chunk].ToArray();
                WriteChunk(bytes, 0, bytes.Length, CancellationToken.None);
                buffer = buffer[chunk..];
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        WriteCore(buffer, offset, count, cancellationToken);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array is not null)
        {
            WriteCore(segment.Array, segment.Offset, segment.Count, cancellationToken);
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            EnsureWritable();
            if (buffer.Length > _maximumBytes - _position)
                throw new IOException("The package-file stream exceeded its explicit byte quota.");
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = Math.Min(buffer.Length, MaximumIoChunkBytes);
                var bytes = buffer[..chunk].ToArray();
                WriteChunk(bytes, 0, bytes.Length, cancellationToken);
                buffer = buffer[chunk..];
            }
        }
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_writable)
                throw new NotSupportedException("Package-file write streams are sequential and cannot be repositioned.");

            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_readLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0 || next > _readLength)
                throw new IOException("The requested archive seek is outside its verified length.");

            _validateBinding(_readLength);
            _position = next;
            return _position;
        }
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("Physical package streams do not permit truncation or resizing.");

    public override void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_writable)
                FlushCore();
            else
                _validateBinding(_readLength);
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    _fileLease.Dispose();
                }
                finally
                {
                    _parentLease.Dispose();
                }
            }
            base.Dispose(disposing);
            return;
        }

        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            try
            {
                if (_writable)
                    FlushCore();
                else
                    _validateBinding(_readLength);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            _disposed = true;
            try
            {
                _fileLease.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            try
            {
                _parentLease.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        GC.SuppressFinalize(this);
        base.Dispose(disposing);
        if (failure is not null)
            throw failure;
    }

    private void WriteChunk(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnsureWritable();
        if (count > _maximumBytes - _position)
            throw new IOException("The package-file stream exceeded its explicit byte quota.");

        cancellationToken.ThrowIfCancellationRequested();
        _validateBinding(_position);
        cancellationToken.ThrowIfCancellationRequested();
        var written = _writeAt(_position, buffer, offset, count);
        if (written <= 0 || written > count)
            throw new IOException("The package-file write made no progress or returned an invalid byte count.");

        _position += written;
        _validateBinding(_position);
        if (written != count)
        {
            var remainingOffset = offset + written;
            var remaining = count - written;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remaining > _maximumBytes - _position)
                    throw new IOException("The package-file stream exceeded its explicit byte quota.");
                _validateBinding(_position);
                cancellationToken.ThrowIfCancellationRequested();
                written = _writeAt(_position, buffer, remainingOffset, remaining);
                if (written <= 0 || written > remaining)
                    throw new IOException("The package-file write made no progress or returned an invalid byte count.");
                _position += written;
                remainingOffset += written;
                remaining -= written;
                _validateBinding(_position);
            }
        }
    }

    private void FlushCore()
    {
        _validateBinding(_position);
        _flush();
        _validateBinding(_position);
    }

    private void EnsureReadable()
    {
        ThrowIfDisposed();
        if (_writable)
            throw new NotSupportedException("The package-file stream is write-only.");
    }

    private void EnsureWritable()
    {
        ThrowIfDisposed();
        if (!_writable)
            throw new NotSupportedException("The package archive stream is read-only.");
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
