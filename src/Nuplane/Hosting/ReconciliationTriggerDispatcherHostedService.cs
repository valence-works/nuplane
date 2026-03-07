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
        await foreach (var request in _triggerQueue.ReadAllAsync(stoppingToken))
        {
            var trigger = request.Trigger;
            var triggerSource = trigger.ObservedOrigin?.FeedName;
            using var dispatchCts = request.CancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.CancellationToken)
                : null;
            var dispatchToken = dispatchCts?.Token ?? stoppingToken;

            try
            {
                var result = await _reconciliationService.TriggerAsync(trigger, dispatchToken);

                if (result.Skipped)
                {
                    _metrics.RecordTrigger(trigger.Type.ToString());
                    _logger.LogDebug(
                        "Reconciliation trigger skipped (single-flight active). TriggerType={TriggerType}, Source={TriggerSource}",
                        trigger.Type,
                        triggerSource);
                }

                request.CompletionSource?.TrySetResult(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                request.CompletionSource?.TrySetCanceled(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                request.CompletionSource?.TrySetCanceled(request.CancellationToken);
            }
            catch (Exception ex)
            {
                request.CompletionSource?.TrySetException(ex);

                if (request.CompletionSource is null)
                {
                    _logger.LogError(
                        ex,
                        "Unhandled exception while dispatching reconciliation trigger. TriggerType={TriggerType}, Source={TriggerSource}",
                        trigger.Type,
                        triggerSource);
                }
            }
        }
    }
}
