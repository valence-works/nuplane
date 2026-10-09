using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.State.ProtectionSerialization;

/// <summary>Core-owned JSON shape for the legacy store body and its optional protection property.</summary>
/// <remarks>
/// This deliberately omits the derived <c>*Normalized</c> properties on <see cref="StoreStateRecord" />.
/// The reader still ignores those aliases when they occur in older files written by direct STJ serialization.
/// </remarks>
internal sealed class StoreStateFileDto
{
    private bool _protectionAssigned;
    private PackageProtectionRecord? _protectionRecord;
    public Dictionary<string, string>? ActiveVersionById { get; set; }

    public Dictionary<string, string>? LastKnownGoodById { get; set; }

    public Dictionary<string, FailureRecord>? LastFailureById { get; set; }

    public Dictionary<string, SourceSnapshotRef>? LastSuccessfulSourceSnapshots { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Dictionary<string, ActivePackageDescriptor>? ActivePackageDescriptorsById { get; set; }

    public Dictionary<string, GraphActivationRecord>? ActiveGraphsById { get; set; }

    [JsonPropertyName("protection")]
    public PackageProtectionRecord? ProtectionRecord
    {
        get => _protectionRecord;
        set
        {
            if (_protectionAssigned)
                throw new JsonException("The store state contains duplicate 'protection' fields.");
            _protectionAssigned = true;
            _protectionRecord = value;
        }
    }

    internal StoreStateRecord ToStoreStateRecord()
    {
        if (ActiveVersionById is null ||
            LastKnownGoodById is null ||
            LastFailureById is null ||
            LastSuccessfulSourceSnapshots is null ||
            UpdatedAt is null)
        {
            throw new JsonException("The store state is missing a required legacy state field.");
        }

        return new StoreStateRecord(
            ActiveVersionById,
            LastKnownGoodById,
            LastFailureById,
            LastSuccessfulSourceSnapshots,
            UpdatedAt.Value,
            ActivePackageDescriptorsById,
            ActiveGraphsById)
        {
            ProtectionRecord = ProtectionRecord
        };
    }

    internal static StoreStateFileDto FromState(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new StoreStateFileDto
        {
            ActiveVersionById = state.ActiveVersionById,
            LastKnownGoodById = state.LastKnownGoodById,
            LastFailureById = state.LastFailureById,
            LastSuccessfulSourceSnapshots = state.LastSuccessfulSourceSnapshots,
            UpdatedAt = state.UpdatedAt,
            ActivePackageDescriptorsById = state.ActivePackageDescriptorsByIdNormalized,
            ActiveGraphsById = state.ActiveGraphsByIdNormalized,
            ProtectionRecord = state.ProtectionRecord
        };
    }
}
