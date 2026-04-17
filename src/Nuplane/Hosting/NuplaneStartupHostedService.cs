using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Reconciliation;
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
    ILogger<NuplaneStartupHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Nuplane startup reconciliation starting");

        await triggerIngress.EnqueueAndWaitAsync(
            ReconciliationTrigger.Startup(),
            cancellationToken);

        logger.LogInformation("Nuplane startup reconciliation completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
