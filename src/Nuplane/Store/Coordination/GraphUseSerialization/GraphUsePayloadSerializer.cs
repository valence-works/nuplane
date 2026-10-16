using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.Coordination.GraphUseSerialization;

/// <summary>Strictly serializes bounded descriptive graph-use candidates.</summary>
/// <remarks>Deserialization validates integrity but grants no native or graph authority.</remarks>
internal sealed class GraphUsePayloadSerializer
{
    internal const int MaximumPayloadBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 64
    };

    internal byte[] Serialize(GraphUseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIntegrity(record, deserializing: false);

        using var payload = new BoundedControlPayloadStream(MaximumPayloadBytes);
        try
        {
            JsonSerializer.Serialize(payload, GraphUseDto.FromRecord(record), JsonOptions);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The graph-use payload exceeds the supported size limit.", exception);
        }
        return payload.ToArray();
    }

    internal GraphUseRecord Deserialize(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new JsonException("The graph-use payload is empty or exceeds the supported size limit.");

        try
        {
            using var document = JsonDocument.Parse(payload, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A graph-use payload must be a JSON object.");
            PersistedJsonChecks.EnsureUniqueProperties(document.RootElement, "graph-use");

            var dto = document.RootElement.Deserialize<GraphUseDto>(JsonOptions)
                ?? throw new JsonException("The graph-use payload cannot be null.");
            var record = dto.ToRecord();
            ValidateIntegrity(record, deserializing: true);
            return record;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException("The graph-use payload is malformed or inconsistent.", exception);
        }
    }

    private static void ValidateIntegrity(GraphUseRecord record, bool deserializing)
    {
        ProtectionDigest.ValidateCanonicalDigest(record.PayloadDigest);
        var actual = ProtectionDigest.GraphUse(record);
        if (!string.Equals(record.PayloadDigest, actual, StringComparison.Ordinal))
        {
            if (deserializing)
                throw new JsonException("The graph-use payload digest does not match its contents.");
            throw new InvalidOperationException("The graph-use payload digest does not match its contents.");
        }
    }

    private static int Required(int? value, string name)
        => value ?? throw new JsonException($"The graph-use payload is missing '{name}'.");

    private static long Required(long? value, string name)
        => value ?? throw new JsonException($"The graph-use payload is missing '{name}'.");

    private static int RequiredPositive(int? value, string name)
    {
        var result = Required(value, name);
        return result > 0 ? result : throw new JsonException($"The graph-use payload has invalid '{name}'.");
    }

    private static Guid Required(Guid? value, string name)
        => value ?? throw new JsonException($"The graph-use payload is missing '{name}'.");

    private static string Required(string? value, string name)
        => value ?? throw new JsonException($"The graph-use payload is missing '{name}'.");

    private static T Required<T>(T? value, string name) where T : class
        => value ?? throw new JsonException($"The graph-use payload is missing '{name}'.");

    private static TEnum ReadEnum<TEnum>(int? value, string name) where TEnum : struct, Enum
    {
        var raw = Required(value, name);
        var result = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return Enum.IsDefined(result) ? result : throw new JsonException($"The graph-use payload has unsupported '{name}'.");
    }

    private sealed class GraphUseDto
    {
        public int? SchemaVersion { get; set; }
        public PhysicalRootIdentityDto? RootIdentity { get; set; }
        public long? EnrollmentEpoch { get; set; }
        public Guid? UseId { get; set; }
        public ProtectedGraphSnapshotDto? GraphSnapshot { get; set; }
        public int? SnapshotState { get; set; }
        public PhysicalFileIdentityDto? SentinelIdentity { get; set; }
        public int? LifetimeKind { get; set; }
        public int? DiagnosticProcessId { get; set; }
        public string? PayloadDigest { get; set; }

        internal GraphUseRecord ToRecord()
            => new(
                Required(SchemaVersion, nameof(SchemaVersion)),
                Required(RootIdentity, nameof(RootIdentity)).ToIdentity(),
                Required(EnrollmentEpoch, nameof(EnrollmentEpoch)),
                Required(UseId, nameof(UseId)),
                Required(GraphSnapshot, nameof(GraphSnapshot)).ToSnapshot(),
                ReadEnum<PackageGraphUseSnapshotState>(SnapshotState, nameof(SnapshotState)),
                Required(SentinelIdentity, nameof(SentinelIdentity)).ToIdentity(),
                ReadEnum<GraphUseLifetimeKind>(LifetimeKind, nameof(LifetimeKind)),
                RequiredPositive(DiagnosticProcessId, nameof(DiagnosticProcessId)),
                Required(PayloadDigest, nameof(PayloadDigest)));

        internal static GraphUseDto FromRecord(GraphUseRecord record)
            => new()
            {
                SchemaVersion = record.SchemaVersion,
                RootIdentity = PhysicalRootIdentityDto.FromIdentity(record.RootIdentity),
                EnrollmentEpoch = record.EnrollmentEpoch,
                UseId = record.UseId,
                GraphSnapshot = ProtectedGraphSnapshotDto.FromSnapshot(record.GraphSnapshot),
                SnapshotState = (int)record.SnapshotState,
                SentinelIdentity = PhysicalFileIdentityDto.FromIdentity(record.SentinelIdentity),
                LifetimeKind = (int)record.LifetimeKind,
                DiagnosticProcessId = record.DiagnosticProcessId,
                PayloadDigest = record.PayloadDigest
            };
    }
}
