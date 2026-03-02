namespace Nuplane.Store.State;

public enum CleanupAction
{
    Kept,
    Deleted,
    Blocked
}

public sealed record PackageVersionEntry(
    string PackageId,
    string Version,
    DateTimeOffset CapturedAt,
    bool IsLastKnownGood);

public sealed record CleanupDecision(
    string PackageId,
    string Version,
    CleanupAction Action,
    string Reason,
    DateTimeOffset Timestamp,
    string CorrelationId);

public sealed class CleanupPolicyEvaluator
{
    public CleanupDecision Evaluate(
        string packageId,
        string version,
        DateTimeOffset capturedAt,
        int versionOrdinalFromNewest,
        bool isLastKnownGood,
        CleanupPolicyOptions options,
        DateTimeOffset now,
        string correlationId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProtectLastKnownGood && isLastKnownGood)
        {
            return new CleanupDecision(packageId, version, CleanupAction.Kept, "protected-lkg", now, correlationId);
        }

        var ageInDays = Math.Max(0, (int)(now - capturedAt).TotalDays);
        if (options.IsRetainedByUnion(versionOrdinalFromNewest, ageInDays))
        {
            return new CleanupDecision(packageId, version, CleanupAction.Kept, "retained-policy", now, correlationId);
        }

        return new CleanupDecision(packageId, version, CleanupAction.Deleted, "eligible-for-deletion", now, correlationId);
    }
}
