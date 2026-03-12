using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests;

internal static class ReconciliationServiceTestExtensions
{
    public static Task<ReconciliationRunResult> TriggerManualAsync(this IReconciliationService service, CancellationToken cancellationToken) =>
        service.TriggerAsync(new(TriggerType.Manual), cancellationToken);
}

