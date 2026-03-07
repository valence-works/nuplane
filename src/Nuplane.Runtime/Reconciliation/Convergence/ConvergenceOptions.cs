namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Root configuration object for convergent runtime loading behaviors,
/// including manifest, admin, loader, polling, and retry settings.
/// </summary>
public sealed class ConvergenceOptions
{
    /// <summary>
    /// Gets the manifest reader configuration options.
    /// </summary>
    public ManifestOptions Manifest { get; } = new();

    /// <summary>
    /// Gets the optional loader boundary configuration options.
    /// </summary>
    public LoaderBoundaryOptions Loader { get; } = new();

    /// <summary>
    /// Gets or sets the convergence poll interval for periodic reconciliation cycles.
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the bounded retry/backoff configuration for convergence operations.
    /// </summary>
    public ConvergenceRetryOptions Retry { get; } = new();
}