using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// A background service that queues periodic reconciliation triggers.
/// Uses <see cref="ConvergenceOptions.PollInterval"/> when convergence is configured,
/// otherwise falls back to <see cref="ReconciliationOptions.PollInterval"/>.
/// Registered automatically when <see cref="ReconciliationOptions.EnableAutomaticReconciliation"/> is <see langword="true"/>.
/// </summary>
/// <remarks>
/// The startup reconciliation trigger is handled by <see cref="NuplaneStartupHostedService"/>.
/// </remarks>
internal sealed class ReconciliationHostedService(
    IReconciliationTriggerIngress triggerSink,
    IOptions<ReconciliationOptions> options,
    IOptions<ConvergenceOptions> convergenceOptions,
    ILogger<ReconciliationHostedService> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var effectivePollInterval = convergenceOptions.Value.Manifest.Enabled
            ? convergenceOptions.Value.PollInterval
            : options.Value.PollInterval;

        logger.LogInformation("Nuplane automatic reconciliation started with poll interval {PollInterval}", effectivePollInterval);

        // Note: The startup reconciliation trigger is handled by NuplaneStartupHostedService,
        // which blocks until the initial reconciliation completes. This service only handles
        // periodic scheduled triggers.

        using var timer = new PeriodicTimer(effectivePollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                triggerSink.Enqueue(ReconciliationTrigger.Scheduled());
                logger.LogDebug("Scheduled reconciliation trigger queued");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue scheduled reconciliation trigger");
            }
        }

        logger.LogInformation("Nuplane automatic reconciliation stopped");
    }
}
