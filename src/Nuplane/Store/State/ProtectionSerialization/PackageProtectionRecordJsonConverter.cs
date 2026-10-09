using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.State.ProtectionSerialization;

/// <summary>Reads and writes validated package-protection candidates without exposing their constructors.</summary>
/// <remarks>
/// Register this converter with an external <see cref="JsonSerializerOptions"/> instance when a custom
/// <see cref="IStoreStateSerializer"/> opts into <see cref="IPackageProtectionStateSerializer"/>. The
/// returned record remains descriptive persisted data, not admission or deletion authority. Validation
/// checks the payload structure and digest text shape only; core digest verification and graph-completeness
/// validation remain separate operations.
/// </remarks>
public sealed class PackageProtectionRecordJsonConverter : JsonConverter<PackageProtectionRecord>
{
    private static readonly JsonSerializerOptions ProtectionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64
    };

    /// <inheritdoc />
    public override PackageProtectionRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        PersistedJsonChecks.EnsureUniqueProperties(document.RootElement, "protection");

        try
        {
            var dto = document.RootElement.Deserialize<PackageProtectionRecordDto>(ProtectionJsonOptions)
                ?? throw new JsonException("The protection record cannot be null.");
            return dto.ToRecord();
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException("The package-protection record is malformed or inconsistent.", exception);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PackageProtectionRecord value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateDigest(value.StateBodyDigest, "stateBodyDigest");
        ValidateDigest(value.ProtectionDigest, "protectionDigest");
        foreach (var retiredGraph in value.RetiredGraphs)
            ValidateDigest(retiredGraph.ProofDigest, "retiredGraphs.proofDigest");
        JsonSerializer.Serialize(writer, PackageProtectionRecordDto.FromRecord(value), ProtectionJsonOptions);
    }

    internal static void ValidateDigest(string? digest, string fieldName)
    {
        if (digest is null || digest.Length != 64 || digest.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new JsonException($"Protection field '{fieldName}' must be a lowercase SHA-256 hex digest.");
    }
}
