using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Desired;

/// <summary>
/// An <see cref="IDesiredPackageSource"/> that reads desired package requests from a shared
/// manifest file. Projects manifest entries into <see cref="PackageRequest"/> instances with
/// <see cref="PackageUpdatePolicy.Exact"/> to ensure deterministic convergence.
/// </summary>
public sealed class DesiredManifestPackageSource : IDesiredPackageSource
{
    private readonly DesiredManifestReader _reader;
    private readonly ConvergenceOptions _options;
    private readonly ReconciliationMetrics? _metrics;
    private DesiredManifestReadResult? _lastReadResult;

    /// <summary>
    /// Gets the result of the last manifest read operation,
    /// or <see langword="null"/> if no read has been performed.
    /// </summary>
    public DesiredManifestReadResult? LastReadResult => _lastReadResult;

    /// <summary>
    /// Initializes a new instance of <see cref="DesiredManifestPackageSource"/>.
    /// </summary>
    /// <param name="reader">The manifest reader.</param>
    /// <param name="options">The convergence options containing manifest configuration.</param>
    /// <param name="metrics">Optional reconciliation metrics to record manifest read outcomes.</param>
    public DesiredManifestPackageSource(DesiredManifestReader reader, ConvergenceOptions options, ReconciliationMetrics? metrics = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metrics = metrics;
    }

    /// <inheritdoc />
    public override string ToString() => $"manifest:{_options.Manifest.Path}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        if (!_options.Manifest.Enabled || string.IsNullOrWhiteSpace(_options.Manifest.Path))
        {
            return [];
        }

        var correlationId = string.IsNullOrEmpty(CorrelationContext.Current)
            ? Guid.NewGuid().ToString("N")
            : CorrelationContext.Current;

        var result = await _reader.ReadAsync(
            _options.Manifest.Path,
            correlationId,
            ct,
            _options.Manifest.SchemaVersion);
        _lastReadResult = result;

        if (result.Status != ManifestReadStatus.Succeeded || result.Manifest is null)
        {
            _metrics?.RecordManifestFailed();
            throw new InvalidOperationException(
                $"Failed to read desired manifest from '{_options.Manifest.Path}' " +
                $"(status: {result.Status}, correlationId: {correlationId}).");
        }

        _metrics?.RecordManifestSucceeded();

        return result.Manifest.Packages
            .Select(entry => new PackageRequest(
                entry.Id,
                entry.Version,
                entry.SourceHint,
                PackageUpdatePolicy.Exact,
                ToString()))
            .ToList();
    }
}
