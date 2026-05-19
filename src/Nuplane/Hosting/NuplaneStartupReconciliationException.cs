using Nuplane.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// Represents a startup reconciliation failure with package-level diagnostic context.
/// </summary>
public sealed class NuplaneStartupReconciliationException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="NuplaneStartupReconciliationException"/>.
    /// </summary>
    public NuplaneStartupReconciliationException(
        string correlationId,
        IReadOnlyList<string> failedPackageIds,
        ReconciliationRunResult? runResult,
        Exception? innerException = null)
        : base(CreateMessage(correlationId, failedPackageIds), innerException)
    {
        CorrelationId = correlationId;
        FailedPackageIds = failedPackageIds;
        RunResult = runResult;
    }

    /// <summary>
    /// Gets the startup reconciliation correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the failed package identifiers reported by startup reconciliation.
    /// </summary>
    public IReadOnlyList<string> FailedPackageIds { get; }

    /// <summary>
    /// Gets the reconciliation run result when startup reconciliation completed.
    /// </summary>
    public ReconciliationRunResult? RunResult { get; }

    private static string CreateMessage(string correlationId, IReadOnlyList<string> failedPackageIds)
    {
        var packageSummary = failedPackageIds.Count == 0
            ? "no package identifiers were reported"
            : string.Join(", ", failedPackageIds);

        return $"Nuplane startup reconciliation failed [CorrelationId={correlationId}, FailedPackages={packageSummary}].";
    }
}
