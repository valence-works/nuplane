namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Stable reason codes for a protection closure whose contents are unknown.</summary>
public enum PackageProtectionUnknownReasonCode
{
    /// <summary>Legacy state did not contain a protection projection.</summary>
    LegacyProtectionMissing = 1,

    /// <summary>The active graph or one of its install identities could not be proven complete.</summary>
    ActiveGraphIncomplete = 2,

    /// <summary>The historical recovery policy does not expose a complete recoverable graph closure.</summary>
    RecoveryClosureUnavailable = 3,

    /// <summary>The configured serializer does not participate in protection round-tripping.</summary>
    SerializerNotParticipating = 4,

    /// <summary>A persisted protection value is malformed or internally inconsistent.</summary>
    MalformedPersistedProtection = 5,

    /// <summary>The persisted protection schema is not supported by this reader.</summary>
    UnsupportedSchemaVersion = 6
}
