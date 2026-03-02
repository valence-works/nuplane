using System;
using System.Threading;

namespace Nuplane.Runtime.Observability;

public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CorrelationId = new();

    public static string Current => CorrelationId.Value ?? string.Empty;

    public static IDisposable BeginScope(string correlationId)
    {
        var previous = CorrelationId.Value;
        CorrelationId.Value = correlationId;
        return new Scope(() => CorrelationId.Value = previous);
    }

    public static string CreateNew() => Guid.NewGuid().ToString("N");

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose()
        {
            restore();
        }
    }
}
