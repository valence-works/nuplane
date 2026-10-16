namespace Nuplane.Reconciliation;

/// <summary>Declares that deferred graph loading consumes only Core-published complete graph leases.</summary>
/// <remarks>Enrolled reconciliation refuses a configured handoff that does not make this explicit declaration.</remarks>
public interface ILeaseBoundPackageGraphLoadingObserver : ILeasedPackageGraphLoadingObserver
{
}
