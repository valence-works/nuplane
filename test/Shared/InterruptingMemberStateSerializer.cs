using Nuplane.Store.State;

namespace Nuplane.Tests.Shared;

internal sealed class InterruptingMemberStateSerializer(
    string memberId,
    long expectedEpoch,
    Action? beforeInterrupt = null)
    : IPackageProtectionStatePayloadSerializer
{
    private readonly StoreStateSerializer _inner = new();
    private bool _thrown;

    public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
        => _inner.LoadAsync(stateFilePath, cancellationToken);

    public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
        => _inner.SaveAsync(stateFilePath, state, cancellationToken);

    public Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        => _inner.ReadPayloadAsync(payload, cancellationToken);

    public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
    {
        if (!_thrown && state.ProtectionRecord is { } protection &&
            string.Equals(protection.MemberId, memberId, StringComparison.Ordinal) &&
            protection.EnrollmentEpoch == expectedEpoch)
        {
            _thrown = true;
            beforeInterrupt?.Invoke();
            throw new IOException("Simulated interruption while publishing a migration member.");
        }
        return _inner.WritePayloadAsync(payload, state, cancellationToken);
    }
}
