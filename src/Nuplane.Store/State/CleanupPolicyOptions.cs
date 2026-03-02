namespace Nuplane.Store.State;

public enum CleanupExecutionMode
{
    Automatic,
    ManualOnly
}

public sealed class CleanupPolicyOptions
{
    public int? RetainLastNVersions { get; set; }

    public int? RetainYoungerThanDays { get; set; }

    public CleanupExecutionMode Mode { get; set; } = CleanupExecutionMode.Automatic;

    public bool ProtectLastKnownGood { get; set; } = true;

    public bool IsValid() =>
        (!RetainLastNVersions.HasValue || RetainLastNVersions.Value > 0) &&
        (!RetainYoungerThanDays.HasValue || RetainYoungerThanDays.Value >= 0);

    public bool IsRetainedByUnion(int versionOrdinalFromNewest, int ageInDays)
    {
        var keepByCount = RetainLastNVersions.HasValue && versionOrdinalFromNewest <= RetainLastNVersions.Value;
        var keepByAge = RetainYoungerThanDays.HasValue && ageInDays <= RetainYoungerThanDays.Value;

        if (!RetainLastNVersions.HasValue && !RetainYoungerThanDays.HasValue)
        {
            return true;
        }

        return keepByCount || keepByAge;
    }
}
