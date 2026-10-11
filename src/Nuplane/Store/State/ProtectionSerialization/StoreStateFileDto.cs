using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Coordination;

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
    private bool _protectionBundleAssigned;
    private PackageProtectionBundle? _protectionBundle;
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

    [JsonPropertyName("protectionBundle")]
    public PackageProtectionBundle? ProtectionBundle
    {
        get => _protectionBundle;
        set
        {
            if (_protectionBundleAssigned)
                throw new JsonException("The store state contains duplicate 'protectionBundle' fields.");
            _protectionBundleAssigned = true;
            _protectionBundle = value;
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

        if (_protectionBundleAssigned && _protectionBundle is null)
            throw new JsonException("The protectionBundle field cannot be null when present.");
        if (_protectionBundleAssigned && _protectionAssigned)
            throw new JsonException("A v2 protection bundle cannot share a state file with the legacy protection field.");

        var state = new StoreStateRecord(
            ActiveVersionById,
            LastKnownGoodById,
            LastFailureById,
            LastSuccessfulSourceSnapshots,
            UpdatedAt.Value,
            ActivePackageDescriptorsById,
            ActiveGraphsById)
        {
            ProtectionRecord = ProtectionRecord,
            ProtectionBundle = ProtectionBundle
        };
        if (state.ProtectionBundle is { } bundle &&
            !string.Equals(ProtectionDigest.StateBody(state), bundle.StateBodyDigest, StringComparison.Ordinal))
            throw new JsonException("The v2 protection bundle does not bind this exact state body.");
        return state;
    }

    internal static StoreStateFileDto FromState(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ProtectionRecord is not null && state.ProtectionBundle is not null)
            throw new JsonException("A state file cannot contain both legacy protection and a v2 protection bundle.");
        if (state.ProtectionBundle is { } bundle &&
            !string.Equals(ProtectionDigest.StateBody(state), bundle.StateBodyDigest, StringComparison.Ordinal))
            throw new JsonException("The v2 protection bundle does not bind this exact state body.");

        return new StoreStateFileDto
        {
            ActiveVersionById = state.ActiveVersionById,
            LastKnownGoodById = state.LastKnownGoodById,
            LastFailureById = state.LastFailureById,
            LastSuccessfulSourceSnapshots = state.LastSuccessfulSourceSnapshots,
            UpdatedAt = state.UpdatedAt,
            ActivePackageDescriptorsById = state.ActivePackageDescriptorsByIdNormalized,
            ActiveGraphsById = state.ActiveGraphsByIdNormalized,
            ProtectionRecord = state.ProtectionRecord,
            ProtectionBundle = state.ProtectionBundle?.Copy()
        };
    }
}
