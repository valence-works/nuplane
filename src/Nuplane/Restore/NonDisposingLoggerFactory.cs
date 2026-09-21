using Microsoft.Extensions.Logging;

namespace Nuplane.Restore;

/// <summary>
/// Wraps a caller-supplied <see cref="ILoggerFactory"/> so the throwaway composition can register it
/// without owning it. A <c>ServiceProvider</c> disposes the singleton instances it holds, and the
/// caller's factory outlives the restore.
/// </summary>
internal sealed class NonDisposingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
    }
}
