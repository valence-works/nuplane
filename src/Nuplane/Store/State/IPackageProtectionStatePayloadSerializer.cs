namespace Nuplane.Store.State;

/// <summary>Declares support for reading and writing protected store-state payloads on caller-owned streams.</summary>
/// <remarks>
/// The caller supplies already-open, bounded payload streams and retains ownership of each stream.
/// Implementations must operate only on the supplied stream; they must not open or reconstruct a path.
/// Declaring this capability does not verify that publication was performed atomically or that the
/// written payload was successfully read back and validated.
/// </remarks>
public interface IPackageProtectionStatePayloadSerializer : IPackageProtectionStateSerializer
{
    /// <summary>Reads one complete protected store-state payload from an already-open stream.</summary>
    /// <param name="payload">A caller-owned stream positioned at the start of a bounded payload.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The parsed and normalized store state.</returns>
    /// <remarks>The serializer leaves <paramref name="payload"/> open and does not access a path.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    /// <exception cref="System.Text.Json.JsonException">The payload is malformed or contains duplicate protection fields.</exception>
    Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken);

    /// <summary>Writes one complete protected store-state payload to an already-open stream.</summary>
    /// <param name="payload">A caller-owned stream positioned where the payload should be written.</param>
    /// <param name="state">The state to serialize.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the payload has been serialized.</returns>
    /// <remarks>The serializer leaves <paramref name="payload"/> open and does not access a path.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> or <paramref name="state"/> is null.</exception>
    /// <exception cref="System.Text.Json.JsonException">The protection metadata is malformed.</exception>
    Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken);
}
