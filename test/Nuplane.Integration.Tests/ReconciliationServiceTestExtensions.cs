using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Integration.Tests;

internal static class ReconciliationServiceTestExtensions
{
    public static Task<ReconciliationRunResult> TriggerManualAsync(this IReconciliationService service, CancellationToken cancellationToken) =>
        service.TriggerAsync(new ReconciliationTrigger(TriggerType.Manual), cancellationToken);
}

