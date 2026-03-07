using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests;

internal static class ReconciliationServiceTestExtensions
{
    public static Task<ReconciliationRunResult> TriggerManualAsync(this IReconciliationService service, CancellationToken cancellationToken) =>
        service.TriggerAsync(new(TriggerType.Manual), cancellationToken);
}

