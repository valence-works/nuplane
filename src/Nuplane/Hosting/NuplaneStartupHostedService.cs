using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// Blocking hosted service that performs the initial startup reconciliation synchronously
/// during <see cref="IHostedService.StartAsync"/>. This ensures all packages are reconciled
/// and loaded before subsequent hosted services (such as CShells feature discovery) run.
/// </summary>
/// <remarks>
/// Must be registered after <see cref="ReconciliationTriggerDispatcherHostedService"/> so the
/// dispatcher's background loop is running when the startup trigger is enqueued.
/// </remarks>
internal sealed class NuplaneStartupHostedService(
    IReconciliationTriggerIngress triggerIngress,
    IOptions<ReconciliationOptions> options,
    ILogger<NuplaneStartupHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Nuplane startup reconciliation starting");

        var correlationId = CorrelationContext.CreateNew();
        var result = await triggerIngress.EnqueueAndWaitAsync(
            ReconciliationTrigger.Startup(correlationId),
            cancellationToken);

        if (result is { IsDegraded: true })
        {
            switch (options.Value.StartupFailurePolicy)
            {
                case StartupFailurePolicy.FailHost:
                    throw CreateStartupReconciliationException(correlationId, result);

                case StartupFailurePolicy.UseLastKnownGood:
                    logger.LogError(
                        "StartupFailurePolicy.UseLastKnownGood is not implemented [CorrelationId={CorrelationId}]; failing host startup instead of starting degraded",
                        correlationId);
                    throw CreateStartupReconciliationException(correlationId, result);

                case StartupFailurePolicy.StartDegraded:
                    logger.LogWarning(
                        "Nuplane startup reconciliation completed in a degraded state [CorrelationId={CorrelationId}]; host is starting degraded as configured by StartupFailurePolicy.StartDegraded",
                        correlationId);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Unsupported startup failure policy '{options.Value.StartupFailurePolicy}'.");
            }
        }

        logger.LogInformation("Nuplane startup reconciliation completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static NuplaneStartupReconciliationException CreateStartupReconciliationException(
        string correlationId,
        ReconciliationRunResult result) =>
        new(correlationId, result.FailedPackages, result);
}
