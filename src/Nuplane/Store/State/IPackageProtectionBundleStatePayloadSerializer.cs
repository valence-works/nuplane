namespace Nuplane.Store.State;

/// <summary>Declares support for round-tripping v2 protection bundles on caller-owned state payload streams.</summary>
/// <remarks>
/// This additive capability is required before a future group publisher may use a serializer for bundle payloads.
/// It does not declare native atomic publication, multiroot admission, graph verification, or deletion authority.
/// Existing serializer interfaces remain sufficient for schema-1 payloads.
/// </remarks>
public interface IPackageProtectionBundleStatePayloadSerializer : IPackageProtectionStatePayloadSerializer
{
}
