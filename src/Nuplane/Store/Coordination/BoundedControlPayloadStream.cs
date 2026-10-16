namespace Nuplane.Store.Coordination;

/// <summary>Bounds a serializer's caller-owned staging buffer before native control-file creation.</summary>
internal sealed class BoundedControlPayloadStream(int maximumBytes) : MemoryStream
{
    public override void SetLength(long value)
    {
        RequireLength(value);
        base.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        RequireLength(checked(Position + count));
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireLength(checked(Position + buffer.Length));
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        RequireLength(checked(Position + 1));
        base.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    private void RequireLength(long length)
    {
        if (length < 0 || length > maximumBytes)
            throw new IOException("The coordinated state payload exceeds its bounded control-file limit.");
    }
}
