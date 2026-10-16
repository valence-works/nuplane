namespace Nuplane.Store.State;

/// <summary>Optional strict observation of the serialized state payload root.</summary>
/// <remarks>This companion preserves the ordinary serializer contract while exposing JSON null to read-only diagnostics.</remarks>
internal interface IStoreStatePayloadObservationSerializer
{
    Task<StoreStatePayloadObservation> ReadPayloadForObservationAsync(Stream payload, CancellationToken cancellationToken);
}

internal sealed record StoreStatePayloadObservation(bool IsNullPayload, StoreStateRecord? State);
