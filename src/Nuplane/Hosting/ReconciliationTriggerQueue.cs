using System.Threading.Channels;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// In-memory trigger ingress used by hosted producers and operator-initiated callers.
/// </summary>
internal sealed class ReconciliationTriggerQueue : IReconciliationTriggerIngress
{
    private readonly Channel<ReconciliationTriggerDispatchRequest> _requests = Channel.CreateUnbounded<ReconciliationTriggerDispatchRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(ReconciliationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Write(new ReconciliationTriggerDispatchRequest(trigger, completionSource: null, CancellationToken.None));
    }

    public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        cancellationToken.ThrowIfCancellationRequested();

        var completionSource = new TaskCompletionSource<ReconciliationRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state =>
                ((TaskCompletionSource<ReconciliationRunResult>)state!).TrySetCanceled(), completionSource)
            : default;

        completionSource.Task.ContinueWith(
            static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            cancellationRegistration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Write(new ReconciliationTriggerDispatchRequest(trigger, completionSource, cancellationToken));
        return completionSource.Task;
    }

    public IAsyncEnumerable<ReconciliationTriggerDispatchRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _requests.Reader.ReadAllAsync(cancellationToken);

    private void Write(ReconciliationTriggerDispatchRequest request)
    {
        if (!_requests.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("Failed to enqueue reconciliation trigger.");
        }
    }
}
