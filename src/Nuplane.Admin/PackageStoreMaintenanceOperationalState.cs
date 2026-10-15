using Nuplane.Operational;

namespace Nuplane.Admin;

/// <summary>Reports the process-local outcome of the most recent mutating maintenance operation.</summary>
/// <remarks>This diagnostic state is neither persisted nor used as package-store authority.</remarks>
internal sealed class PackageStoreMaintenanceOperationalState : IOperationalStateContributor
{
    private string? _degradedReason;

    internal void RecordOutcome(string operation, string outcome)
    {
        if (operation is "preview" or "enrollment_preview")
            return;

        var reason = outcome switch
        {
            "completed" => null,
            "refused" => "package-store-maintenance:refused",
            "cancelled" => "package-store-maintenance:cancelled",
            _ => "package-store-maintenance:failed"
        };
        Interlocked.Exchange(ref _degradedReason, reason);
    }

    public Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reason = Volatile.Read(ref _degradedReason);
        return Task.FromResult(new OperationalStateContribution(
            "package-store-maintenance", reason is null ? [] : [reason]));
    }
}
