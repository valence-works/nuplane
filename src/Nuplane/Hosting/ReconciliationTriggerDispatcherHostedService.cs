using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Hosting;

/// <summary>
/// Drains queued reconciliation triggers and dispatches them through the reconciliation service.
/// </summary>
internal sealed class ReconciliationTriggerDispatcherHostedService(
    ReconciliationTriggerQueue triggerQueue,
    IReconciliationService reconciliationService,
    ReconciliationMetrics metrics,
    ILogger<ReconciliationTriggerDispatcherHostedService> logger)
    : BackgroundService
{
    private readonly ReconciliationTriggerQueue _triggerQueue = triggerQueue ?? throw new ArgumentNullException(nameof(triggerQueue));
    private readonly IReconciliationService _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
    private readonly ReconciliationMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly ILogger<ReconciliationTriggerDispatcherHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var trigger in _triggerQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await _reconciliationService.TriggerAsync(trigger, stoppingToken);

                if (result.Skipped)
                {
                    _metrics.RecordTrigger(trigger.Type.ToString());
                    _logger.LogDebug(
                        "Reconciliation trigger skipped (single-flight active). TriggerType={TriggerType}, Source={TriggerSource}",
                        trigger.Type,
                        trigger.Source);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception while dispatching reconciliation trigger. TriggerType={TriggerType}, Source={TriggerSource}",
                    trigger.Type,
                    trigger.Source);
            }
        }
    }
}

