namespace Nuplane.Loading;

/// <summary>Declares an advisor that consumes the graph-use leases in its advisor context.</summary>
/// <remarks>
/// The existing EvaluateAsync signature remains unchanged. On enrolled roots all graph leases must
/// be published before evaluation; package IO must consume the appropriate lease's counted read pin.
/// </remarks>
public interface IScopedPackageLoadModeAdvisor : IPackageLoadModeAdvisor
{
}
