namespace Nuplane.Loading;

/// <summary>
/// Tracks whether the current process has refreshed loading state for the active package set.
/// </summary>
public sealed class LoadingCatalogRefreshTracker
{
    private readonly object _gate = new();

    /// <summary>
    /// Gets the UTC time of the last successful current-process refresh.
    /// </summary>
    public DateTimeOffset? RefreshedAtUtc { get; private set; }

    /// <summary>
    /// Gets the correlation identifier of the last refresh.
    /// </summary>
    public string? LastRefreshCorrelationId { get; private set; }

    /// <summary>
    /// Gets whether the current process has refreshed loading state.
    /// </summary>
    public bool HasRefreshed => RefreshedAtUtc.HasValue;

    /// <summary>
    /// Marks loading state as refreshed for the current process.
    /// </summary>
    public void MarkRefreshed(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_gate)
        {
            LastRefreshCorrelationId = correlationId;
            RefreshedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}

