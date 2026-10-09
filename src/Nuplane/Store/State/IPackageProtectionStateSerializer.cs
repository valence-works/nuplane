namespace Nuplane.Store.State;

/// <summary>Declares support for round-tripping complete package-protection metadata with store state.</summary>
/// <remarks>
/// This declaration does not replace saved-payload and digest verification. An unclassified custom
/// serializer cannot enroll a store; a declared serializer that drops protection cannot complete publication.
/// </remarks>
public interface IPackageProtectionStateSerializer : IStoreStateSerializer
{
}
