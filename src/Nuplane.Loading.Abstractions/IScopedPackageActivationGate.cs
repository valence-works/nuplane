namespace Nuplane.Loading;

/// <summary>Declares an activation gate that consumes the graph-use leases in its activation context.</summary>
/// <remarks>
/// The existing EvaluateAsync signature remains unchanged. On enrolled roots the loader supplies
/// complete leases before invoking the gate. Package reads must use those lease views, including
/// counted read pins; the gate must not dispose or retain release authority.
/// </remarks>
public interface IScopedPackageActivationGate : IPackageActivationGate
{
}
