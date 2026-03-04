using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane;

/// <summary>
/// A background service that periodically triggers reconciliation cycles.
/// Uses <see cref="ConvergenceOptions.PollInterval"/> when convergence is configured,
/// otherwise falls back to <see cref="ReconciliationOptions.PollInterval"/>.
/// Registered automatically when <see cref="ReconciliationOptions.EnableAutomaticReconciliation"/> is <see langword="true"/>.
/// </summary>
public sealed class ReconciliationHostedService : BackgroundService
{
    private readonly IReconciliationService _reconciliationService;
    private readonly ReconciliationOptions _options;
    private readonly ConvergenceOptions _convergenceOptions;
    private readonly ILogger<ReconciliationHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ReconciliationHostedService"/>.
    /// </summary>
    public ReconciliationHostedService(
        IReconciliationService reconciliationService,
        ReconciliationOptions options,
        ConvergenceOptions convergenceOptions,
        ILogger<ReconciliationHostedService> logger)
    {
        _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _convergenceOptions = convergenceOptions ?? throw new ArgumentNullException(nameof(convergenceOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effectivePollInterval = _convergenceOptions.Manifest.Enabled
            ? _convergenceOptions.PollInterval
            : _options.PollInterval;

        _logger.LogInformation("Nuplane automatic reconciliation started with poll interval {PollInterval}", effectivePollInterval);

        using var timer = new PeriodicTimer(effectivePollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var result = await _reconciliationService.TriggerManualAsync(stoppingToken);

                if (result.Skipped)
                {
                    _logger.LogDebug("Reconciliation cycle skipped (single-flight active)");
                }
                else if (result.IsDegraded)
                {
                    _logger.LogWarning("Reconciliation cycle completed in degraded state. FailedPackages={Count}", result.FailedPackages.Count);
                }
                else
                {
                    _logger.LogDebug("Reconciliation cycle completed. Added={Added}, Updated={Updated}, Removed={Removed}",
                        result.ChangeSet.Added.Count, result.ChangeSet.Updated.Count, result.ChangeSet.Removed.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation cycle failed with unhandled exception");
            }
        }

        _logger.LogInformation("Nuplane automatic reconciliation stopped");
    }
}

