namespace Nuplane.Reconciliation;

/// <summary>Declares that retries always propagate package-store admission refusals unchanged.</summary>
/// <remarks>Implementations must never retry, suppress, or replace <c>PackageStoreAdmissionException</c>.</remarks>
public interface IPackageStoreRefusalPreservingRetryPolicy : IReconciliationRetryPolicy
{
}
