namespace Nuplane.Store.Transactions;

/// <summary>
/// Represents the result of a package transaction, including success/failure status and rollback information.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The target version.</param>
/// <param name="Succeeded">Whether the transaction completed successfully.</param>
/// <param name="FailedStage">The stage at which the transaction failed, if any.</param>
/// <param name="FailureMessage">The failure message, if any.</param>
/// <param name="LastKnownGoodPreserved">Whether the last-known-good version was preserved on failure.</param>
public sealed record PackageTransactionResult(
    string PackageId,
    string Version,
    bool Succeeded,
    PackageTransactionStage? FailedStage,
    string? FailureMessage,
    bool LastKnownGoodPreserved);