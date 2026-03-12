namespace Nuplane.Reconciliation;

/// <summary>
/// Represents the result of a rollback evaluation across all packages in a reconciliation cycle.
/// </summary>
/// <param name="RollbackPerformed">Whether any rollback was required.</param>
/// <param name="RolledBackPackages">Package IDs that were rolled back to LKG.</param>
/// <param name="PreservedPackages">Package IDs that were preserved (skipped).</param>
/// <param name="SucceededPackages">Package IDs that completed successfully.</param>
/// <param name="ReasonCode">The overall reason code for the rollback evaluation.</param>
public sealed record RollbackResult(
    bool RollbackPerformed,
    IReadOnlyList<string> RolledBackPackages,
    IReadOnlyList<string> PreservedPackages,
    IReadOnlyList<string> SucceededPackages,
    string ReasonCode);