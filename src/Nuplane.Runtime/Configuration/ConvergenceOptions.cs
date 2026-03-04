namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Root configuration object for Phase 4 convergent runtime loading behaviors,
/// including manifest, admin, loader, polling, and retry settings.
/// </summary>
public sealed class ConvergenceOptions
{
    /// <summary>
    /// Gets the manifest reader configuration options.
    /// </summary>
    public ManifestOptions Manifest { get; } = new();

    /// <summary>
    /// Gets the administrative surface configuration options.
    /// </summary>
    public AdminOptions Admin { get; } = new();

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

/// <summary>
/// Configuration options for the desired manifest reader.
/// </summary>
public sealed class ManifestOptions
{
    /// <summary>
    /// Gets or sets whether manifest-driven desired state is enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the file path to the shared desired manifest.
    /// Required when <see cref="Enabled"/> is <see langword="true"/>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the expected schema version for manifest validation.
    /// Defaults to "1.0".
    /// </summary>
    public string SchemaVersion { get; set; } = "1.0";
}

/// <summary>
/// Configuration options for optional administrative surfaces.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>
    /// Gets or sets whether administrative operational surfaces are enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Configuration options for the optional loader boundary.
/// </summary>
public sealed class LoaderBoundaryOptions
{
    /// <summary>
    /// Gets or sets whether the loader boundary is enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Bounded retry/backoff configuration for convergence operations.
/// </summary>
public sealed class ConvergenceRetryOptions
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts.
    /// Defaults to 3.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial delay before the first retry attempt.
    /// Defaults to 2 seconds.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts (caps exponential backoff).
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
