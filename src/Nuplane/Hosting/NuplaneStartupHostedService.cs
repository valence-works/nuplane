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

        if (options.Value.StartupFailurePolicy == StartupFailurePolicy.FailHost
            && result is { IsDegraded: true })
        {
            throw new NuplaneStartupReconciliationException(
                correlationId,
                result.FailedPackages,
                result);
        }

        logger.LogInformation("Nuplane startup reconciliation completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
