using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Hosting;

internal sealed class ReconciliationTriggerDispatchRequest
{
    public ReconciliationTriggerDispatchRequest(
        ReconciliationTrigger trigger,
        TaskCompletionSource<ReconciliationRunResult>? completionSource,
        CancellationToken cancellationToken)
    {
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        CompletionSource = completionSource;
        CancellationToken = cancellationToken;
    }

    public ReconciliationTrigger Trigger { get; }

    public TaskCompletionSource<ReconciliationRunResult>? CompletionSource { get; }

    public CancellationToken CancellationToken { get; }
}
