using System.Diagnostics;

namespace Nuplane.Observability;

/// <summary>
/// Provides ambient correlation identity for reconciliation cycles.
/// Delegates to <see cref="Activity.Current"/> when available, falling back
/// to an <see cref="AsyncLocal{T}"/>-based scope for environments that do not
/// use the <see cref="System.Diagnostics"/> tracing infrastructure.
/// </summary>
public static class CorrelationContext
{
    /// <summary>
    /// The <see cref="ActivitySource"/> used to create reconciliation activities.
    /// Listeners (e.g., OpenTelemetry) can subscribe to this source to capture traces.
    /// </summary>
    public static readonly ActivitySource Source = new("Nuplane.Runtime", "0.1.0");

    private static readonly AsyncLocal<string?> FallbackCorrelationId = new();

    /// <summary>
    /// Gets the current correlation identifier.
    /// Returns <see cref="Activity.Current"/>.<see cref="Activity.Id"/> when an activity
    /// is in progress; otherwise returns the fallback <see cref="AsyncLocal{T}"/> value.
    /// </summary>
    public static string Current => Activity.Current?.Id ?? FallbackCorrelationId.Value ?? string.Empty;

    /// <summary>
    /// Begins a new correlation scope. If the <see cref="ActivitySource"/> has active listeners,
    /// an <see cref="Activity"/> named <c>reconciliation.cycle</c> is started; otherwise the
    /// correlation identifier is stored in an <see cref="AsyncLocal{T}"/>.
    /// </summary>
    /// <param name="correlationId">The correlation identifier to associate with the scope.</param>
    /// <returns>An <see cref="IDisposable"/> that ends the scope when disposed.</returns>
    public static IDisposable BeginScope(string correlationId)
    {
        var activity = Source.StartActivity("reconciliation.cycle", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("nuplane.correlation_id", correlationId);
            return new ActivityScope(activity);
        }

        // Fallback to AsyncLocal when no activity listeners are registered
        var previous = FallbackCorrelationId.Value;
        FallbackCorrelationId.Value = correlationId;
        return new AsyncLocalScope(() => FallbackCorrelationId.Value = previous);
    }

    /// <summary>
    /// Creates a new unique correlation identifier.
    /// </summary>
    /// <returns>A new GUID-based correlation identifier without hyphens.</returns>
    public static string CreateNew() => Guid.NewGuid().ToString("N");

    private sealed class ActivityScope(Activity activity) : IDisposable
    {
        public void Dispose()
        {
            activity.Dispose();
        }
    }

    private sealed class AsyncLocalScope(Action restore) : IDisposable
    {
        public void Dispose()
        {
            restore();
        }
    }
}


