namespace Nuplane.Store.Transactions;

/// <summary>
/// Represents the sequential stages of a package transaction.
/// </summary>
public enum PackageTransactionStage
{
    /// <summary>Trust policy gate evaluation stage.</summary>
    TrustPolicyGate,
    /// <summary>Lock file policy gate evaluation stage.</summary>
    LockFileGate,
    /// <summary>Package staging (download/prepare) stage.</summary>
    Stage,
    /// <summary>Package validation (integrity check) stage.</summary>
    Validate,
    /// <summary>Immutable artifact publishing stage.</summary>
    PublishImmutable,
    /// <summary>Atomic version pointer switching stage.</summary>
    AtomicSwitch,
    /// <summary>State persistence stage.</summary>
    PersistState
}