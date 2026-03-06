using System.Threading.Channels;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// In-memory trigger ingress used by hosted producers to enqueue reconciliation work.
/// </summary>
internal sealed class ReconciliationTriggerQueue : IReconciliationTriggerSink
{
    private readonly Channel<ReconciliationTrigger> _triggers = Channel.CreateUnbounded<ReconciliationTrigger>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(ReconciliationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (!_triggers.Writer.TryWrite(trigger))
        {
            throw new InvalidOperationException("Failed to enqueue reconciliation trigger.");
        }
    }

    public IAsyncEnumerable<ReconciliationTrigger> ReadAllAsync(CancellationToken cancellationToken) =>
        _triggers.Reader.ReadAllAsync(cancellationToken);
}

