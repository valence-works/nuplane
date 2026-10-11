using NuGet.Versioning;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Purely classifies completed installs under exact protection and explicit retention evidence.</summary>
/// <remarks>
/// The planner performs no filesystem access and its output is descriptive only. An Eligible row is
/// not permission to delete the install; any future executor must obtain fresh authority and evidence.
/// </remarks>
internal sealed class PackageStoreRetentionPlanner : IPackageStoreRetentionPlanner
{
    private static readonly IEqualityComparer<NuGetVersion> VersionReleaseComparer = VersionComparer.VersionRelease;
    private static readonly IComparer<NuGetVersion> VersionReleaseOrder = VersionComparer.VersionRelease;

    /// <inheritdoc />
    public PackageStoreRetentionPlan Plan(PackageStoreRetentionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var globalReasons = new HashSet<PackageStoreRetentionReason>();
        switch (snapshot.InventoryStatus)
        {
            case PackageStoreRetentionInventoryStatus.Complete:
                break;
            case PackageStoreRetentionInventoryStatus.Incomplete:
                globalReasons.Add(PackageStoreRetentionReason.InventoryIncomplete);
                break;
            case PackageStoreRetentionInventoryStatus.Unknown:
                globalReasons.Add(PackageStoreRetentionReason.InventoryUnknown);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot), "The inventory status is unsupported.");
        }

        if (snapshot.ProtectionKnowledge == PackageStoreRetentionProtectionKnowledge.Unknown)
            globalReasons.Add(PackageStoreRetentionReason.ProtectionUnknown);

        var analyses = snapshot.CompletedInstalls.Select(static install => new InstallAnalysis(install)).ToArray();
        var protectedReasonsByInstall = BuildProtectionIndex(snapshot, analyses, globalReasons);

        foreach (var analysis in analyses)
        {
            if (analysis.Install.Root != snapshot.Root)
            {
                analysis.Reasons.Add(PackageStoreRetentionReason.RootMismatch);
                globalReasons.Add(PackageStoreRetentionReason.RootMismatch);
            }

            if (!NuGetVersion.TryParse(analysis.Install.Version, out var version))
            {
                analysis.Reasons.Add(PackageStoreRetentionReason.MalformedVersion);
            }
            else
            {
                analysis.Version = version;
                if (!HasConsistentInstallPath(analysis.Install, version))
                    analysis.Reasons.Add(PackageStoreRetentionReason.MalformedInstallPath);
            }
        }

        AddIdentityAmbiguities(analyses);
        var packageBlockers = BuildPackageBlockers(analyses);
        var retentionGroups = SelectRetentionGroups(analyses, snapshot.KeepNewestInactiveVersionsPerPackage);
        var entries = new List<PackageStoreRetentionPlanEntry>(analyses.Length);

        foreach (var analysis in analyses)
        {
            var reasons = new HashSet<PackageStoreRetentionReason>(analysis.Reasons);
            reasons.UnionWith(globalReasons);

            if (packageBlockers.TryGetValue(analysis.Install.PackageId, out var packageReasons))
                reasons.UnionWith(packageReasons);

            if (protectedReasonsByInstall.TryGetValue(analysis.Install, out var protectedReasons))
                reasons.UnionWith(protectedReasons.Select(ToRetentionReason));

            var hasProtection = protectedReasonsByInstall.ContainsKey(analysis.Install);
            var hasIdentityProblem = analysis.Reasons.Contains(PackageStoreRetentionReason.RootMismatch) ||
                                    analysis.Reasons.Contains(PackageStoreRetentionReason.MalformedVersion) ||
                                    analysis.Reasons.Contains(PackageStoreRetentionReason.MalformedInstallPath) ||
                                    analysis.Reasons.Contains(PackageStoreRetentionReason.DuplicateIdentity) ||
                                    analysis.Reasons.Contains(PackageStoreRetentionReason.IdentityConflict);
            var isBlocked = globalReasons.Count > 0 || packageReasons is not null || hasIdentityProblem;

            PackageStoreRetentionClassification classification;
            if (hasProtection && !hasIdentityProblem)
            {
                classification = PackageStoreRetentionClassification.Retained;
            }
            else if (isBlocked)
            {
                classification = PackageStoreRetentionClassification.Refused;
            }
            else if (snapshot.KeepNewestInactiveVersionsPerPackage is null)
            {
                reasons.Add(PackageStoreRetentionReason.RetentionPolicyAbsent);
                classification = PackageStoreRetentionClassification.Retained;
            }
            else if (analysis.Version is not null && retentionGroups.Contains(new RetentionGroupKey(
                         analysis.Install.PackageId,
                         analysis.Version)))
            {
                reasons.Add(PackageStoreRetentionReason.WithinInactiveRetentionBudget);
                classification = PackageStoreRetentionClassification.Retained;
            }
            else
            {
                reasons.Add(PackageStoreRetentionReason.OutsideInactiveRetentionBudget);
                classification = PackageStoreRetentionClassification.Eligible;
            }

            entries.Add(new PackageStoreRetentionPlanEntry(analysis.Install, classification, reasons));
        }

        entries.Sort(PlanEntryComparer.Instance);
        return new PackageStoreRetentionPlan(
            snapshot.Root,
            snapshot.EnrollmentEpoch,
            snapshot.InventoryStatus,
            snapshot.ProtectionKnowledge,
            snapshot.KeepNewestInactiveVersionsPerPackage,
            globalReasons,
            entries);
    }

    private static Dictionary<PackageInstallIdentity, HashSet<PackageStoreRetentionProtectionReason>> BuildProtectionIndex(
        PackageStoreRetentionSnapshot snapshot,
        IReadOnlyList<InstallAnalysis> analyses,
        HashSet<PackageStoreRetentionReason> globalReasons)
    {
        var installs = new Dictionary<PackageInstallIdentity, List<InstallAnalysis>>(InstallIdentityComparer.Instance);
        foreach (var analysis in analyses)
        {
            if (!installs.TryGetValue(analysis.Install, out var matches))
                installs.Add(analysis.Install, matches = []);
            matches.Add(analysis);
        }

        var protection = new Dictionary<PackageInstallIdentity, HashSet<PackageStoreRetentionProtectionReason>>(
            InstallIdentityComparer.Instance);
        foreach (var protectedInstall in snapshot.ProtectedInstalls)
        {
            var identity = protectedInstall.Install;
            if (identity.Root != snapshot.Root)
                globalReasons.Add(PackageStoreRetentionReason.ProtectedIdentityRootMismatch);

            if (!installs.TryGetValue(identity, out var matches))
            {
                globalReasons.Add(PackageStoreRetentionReason.ProtectedInstallNotInventoried);
                continue;
            }

            foreach (var match in matches)
                match.IsProtected = true;

            if (!protection.TryGetValue(identity, out var reasons))
                protection.Add(identity, reasons = []);
            reasons.Add(protectedInstall.Reason);
        }

        return protection;
    }

    private static void AddIdentityAmbiguities(IReadOnlyList<InstallAnalysis> analyses)
    {
        var exactGroups = Group(analyses, static analysis => analysis.Install, InstallIdentityComparer.Instance);
        foreach (var group in exactGroups.Values.Where(static group => group.Count > 1))
            foreach (var analysis in group)
                analysis.Reasons.Add(PackageStoreRetentionReason.DuplicateIdentity);

        var paths = Group(analyses,
            static analysis => new RootPathKey(analysis.Install.Root, analysis.Install.RootRelativeInstallPath),
            RootPathKeyComparer.Instance);
        MarkConflictingGroups(paths.Values);

        var directories = Group(analyses, static analysis => analysis.Install.DirectoryIdentity, EqualityComparer<PhysicalFileIdentity>.Default);
        MarkConflictingGroups(directories.Values);

        var completions = Group(analyses, static analysis => analysis.Install.CompletionIdentity, StringComparer.Ordinal);
        MarkConflictingGroups(completions.Values);
    }

    private static Dictionary<TKey, List<InstallAnalysis>> Group<TKey>(
        IReadOnlyList<InstallAnalysis> analyses,
        Func<InstallAnalysis, TKey> keySelector,
        IEqualityComparer<TKey> comparer)
        where TKey : notnull
    {
        var groups = new Dictionary<TKey, List<InstallAnalysis>>(comparer);
        foreach (var analysis in analyses)
        {
            var key = keySelector(analysis);
            if (!groups.TryGetValue(key, out var group))
                groups.Add(key, group = []);
            group.Add(analysis);
        }

        return groups;
    }

    private static void MarkConflictingGroups(IEnumerable<List<InstallAnalysis>> groups)
    {
        foreach (var group in groups)
        {
            if (group.Count < 2)
                continue;

            var distinctIdentities = group
                .Select(static analysis => analysis.Install)
                .Distinct(InstallIdentityComparer.Instance)
                .Take(2)
                .Count();
            if (distinctIdentities < 2)
                continue;

            foreach (var analysis in group)
                analysis.Reasons.Add(PackageStoreRetentionReason.IdentityConflict);
        }
    }

    private static Dictionary<string, HashSet<PackageStoreRetentionReason>> BuildPackageBlockers(
        IEnumerable<InstallAnalysis> analyses)
    {
        var blockers = new Dictionary<string, HashSet<PackageStoreRetentionReason>>(StringComparer.OrdinalIgnoreCase);
        foreach (var analysis in analyses)
        {
            foreach (var reason in analysis.Reasons.Where(static reason =>
                         reason is PackageStoreRetentionReason.MalformedVersion or PackageStoreRetentionReason.MalformedInstallPath))
            {
                if (!blockers.TryGetValue(analysis.Install.PackageId, out var packageReasons))
                    blockers.Add(analysis.Install.PackageId, packageReasons = []);
                packageReasons.Add(reason);
            }
        }

        return blockers;
    }

    private static HashSet<RetentionGroupKey> SelectRetentionGroups(
        IEnumerable<InstallAnalysis> analyses,
        int? keepNewestInactiveVersionsPerPackage)
    {
        var selected = new HashSet<RetentionGroupKey>(RetentionGroupKeyComparer.Instance);
        if (keepNewestInactiveVersionsPerPackage is null or 0)
            return selected;

        var inactive = analyses
            .Where(static analysis => analysis.Version is not null &&
                                      !analysis.Reasons.Contains(PackageStoreRetentionReason.RootMismatch) &&
                                      !analysis.Reasons.Contains(PackageStoreRetentionReason.MalformedVersion) &&
                                      !analysis.Reasons.Contains(PackageStoreRetentionReason.MalformedInstallPath))
            .ToArray();

        foreach (var package in inactive.GroupBy(static analysis => analysis.Install.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            // The budget counts VersionRelease equivalence groups, not feed-specific directory copies.
            // Every exact install in a selected group, including metadata spellings, occupies the tie.
            var versions = package
                .Where(analysis => !analysis.IsProtected)
                .GroupBy(static analysis => analysis.Version!, VersionReleaseComparer)
                .OrderByDescending(static versionGroup => versionGroup.Key, VersionReleaseOrder)
                .Take(keepNewestInactiveVersionsPerPackage.Value);

            foreach (var versionGroup in versions)
            {
                foreach (var analysis in versionGroup)
                    selected.Add(new RetentionGroupKey(analysis.Install.PackageId, versionGroup.Key));
            }
        }

        return selected;
    }

    private static bool HasConsistentInstallPath(PackageInstallIdentity install, NuGetVersion version)
    {
        var components = install.RootRelativeInstallPath.Split('/', StringSplitOptions.None);
        return components.Length == 3 &&
               string.Equals(components[1], install.PackageId, StringComparison.OrdinalIgnoreCase) &&
               NuGetVersion.TryParse(components[2], out var pathVersion) &&
               VersionReleaseComparer.Equals(pathVersion, version);
    }

    private static PackageStoreRetentionReason ToRetentionReason(PackageStoreRetentionProtectionReason reason)
        => reason switch
        {
            PackageStoreRetentionProtectionReason.Active => PackageStoreRetentionReason.ProtectedActive,
            PackageStoreRetentionProtectionReason.RecoverableLastKnownGood => PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood,
            PackageStoreRetentionProtectionReason.LiveUse => PackageStoreRetentionReason.ProtectedLiveUse,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), "The protection reason is unsupported.")
        };

    private sealed class InstallAnalysis(PackageInstallIdentity install)
    {
        internal PackageInstallIdentity Install { get; } = install;
        internal NuGetVersion? Version { get; set; }
        internal HashSet<PackageStoreRetentionReason> Reasons { get; } = [];
        internal bool IsProtected { get; set; }
    }

    private readonly record struct RetentionGroupKey(string PackageId, NuGetVersion Version);

    private sealed class RetentionGroupKeyComparer : IEqualityComparer<RetentionGroupKey>
    {
        internal static RetentionGroupKeyComparer Instance { get; } = new();

        public bool Equals(RetentionGroupKey left, RetentionGroupKey right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.PackageId, right.PackageId) &&
               VersionReleaseComparer.Equals(left.Version, right.Version);

        public int GetHashCode(RetentionGroupKey value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId),
                VersionReleaseComparer.GetHashCode(value.Version));
    }

    private readonly record struct RootPathKey(PhysicalRootIdentity Root, string Path);

    private sealed class RootPathKeyComparer : IEqualityComparer<RootPathKey>
    {
        internal static RootPathKeyComparer Instance { get; } = new();

        public bool Equals(RootPathKey left, RootPathKey right)
            => left.Root == right.Root && StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path);

        public int GetHashCode(RootPathKey value)
            => HashCode.Combine(value.Root, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }

    private sealed class InstallIdentityComparer : IEqualityComparer<PackageInstallIdentity>
    {
        internal static InstallIdentityComparer Instance { get; } = new();

        public bool Equals(PackageInstallIdentity? left, PackageInstallIdentity? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;

            // Archive hashes are supplemental observations, not install or protection identity.
            return left.Root == right.Root &&
                   StringComparer.OrdinalIgnoreCase.Equals(left.PackageId, right.PackageId) &&
                   StringComparer.Ordinal.Equals(left.Version, right.Version) &&
                   StringComparer.Ordinal.Equals(left.RootRelativeInstallPath, right.RootRelativeInstallPath) &&
                   left.DirectoryIdentity == right.DirectoryIdentity &&
                   StringComparer.Ordinal.Equals(left.CompletionIdentity, right.CompletionIdentity);
        }

        public int GetHashCode(PackageInstallIdentity install)
            => HashCode.Combine(
                install.Root,
                StringComparer.OrdinalIgnoreCase.GetHashCode(install.PackageId),
                StringComparer.Ordinal.GetHashCode(install.Version),
                StringComparer.Ordinal.GetHashCode(install.RootRelativeInstallPath),
                install.DirectoryIdentity,
                StringComparer.Ordinal.GetHashCode(install.CompletionIdentity));
    }

    private sealed class PlanEntryComparer : IComparer<PackageStoreRetentionPlanEntry>
    {
        internal static PlanEntryComparer Instance { get; } = new();

        public int Compare(PackageStoreRetentionPlanEntry? left, PackageStoreRetentionPlanEntry? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Install.PackageId, right.Install.PackageId);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Install.PackageId, right.Install.PackageId);
            if (comparison != 0)
                return comparison;

            var leftValidVersion = NuGetVersion.TryParse(left.Install.Version, out var leftVersion);
            var rightValidVersion = NuGetVersion.TryParse(right.Install.Version, out var rightVersion);
            if (leftValidVersion && rightValidVersion)
            {
                comparison = VersionReleaseOrder.Compare(rightVersion, leftVersion);
                if (comparison != 0)
                    return comparison;
            }
            else
            {
                comparison = rightValidVersion.CompareTo(leftValidVersion);
                if (comparison != 0)
                    return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Install.RootRelativeInstallPath, right.Install.RootRelativeInstallPath);
            if (comparison != 0)
                return comparison;
            comparison = CompareIdentity(left.Install.Root.HandleIdentity, right.Install.Root.HandleIdentity);
            if (comparison != 0)
                return comparison;
            comparison = CompareIdentity(left.Install.DirectoryIdentity, right.Install.DirectoryIdentity);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Install.CompletionIdentity, right.Install.CompletionIdentity);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Install.Version, right.Install.Version);
            if (comparison != 0)
                return comparison;
            return StringComparer.Ordinal.Compare(left.Install.VerifiedArchiveHash, right.Install.VerifiedArchiveHash);
        }

        private static int CompareIdentity(PhysicalFileIdentity left, PhysicalFileIdentity right)
        {
            var comparison = StringComparer.Ordinal.Compare(left.Provider, right.Provider);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.VolumeOrDeviceId, right.VolumeOrDeviceId);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.FileId, right.FileId);
        }
    }
}
