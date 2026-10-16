namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Explains why package-store admission or scoped access was refused.</summary>
public enum PackageStoreAdmissionReason
{
    /// <summary>The store's protection authority cannot be established.</summary>
    UnknownAuthority,
    /// <summary>The filesystem provider cannot supply required safe identity operations.</summary>
    UnsupportedFilesystem,
    /// <summary>The requested path or identity belongs to a different physical root.</summary>
    RootMismatch,
    /// <summary>Enrollment is incomplete or its active and recoverable last-known-good closure is unknown.</summary>
    IncompleteEnrollment,
    /// <summary>Persisted state does not match the expected enrolled state.</summary>
    StateMismatch,
    /// <summary>The operation scope has ended or was closed.</summary>
    ExpiredScope,
    /// <summary>A required participant has not declared a supported scoped contract.</summary>
    UnsupportedParticipant
}
