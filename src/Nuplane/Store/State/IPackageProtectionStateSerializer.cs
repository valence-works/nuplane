namespace Nuplane.Store.State;

/// <summary>Declares support for round-tripping complete package-protection metadata with store state.</summary>
/// <remarks>
/// This declaration does not replace saved-payload and digest verification. An unclassified custom
/// serializer cannot enroll a store; a declared serializer that drops protection cannot complete publication.
/// Participating serializers must also reject ambiguous duplicate protection envelope fields.
/// <see cref="ProtectionSerialization.PackageProtectionRecordJsonConverter"/> supports the protection
/// value in custom JSON formats; it does not validate the containing state envelope.
/// </remarks>
public interface IPackageProtectionStateSerializer : IStoreStateSerializer
{
}
