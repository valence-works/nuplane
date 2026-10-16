using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Maintenance;

namespace Nuplane.Store.Tests;

public sealed class PackageStoreRetentionPlannerTests
{
    private static readonly PhysicalRootIdentity Root = new(new PhysicalFileIdentity("test", "volume", "root"));

    [Fact]
    public void Plan_KnownEmptyInventoryAndProtection_ReturnsAResolvedEmptyPlan()
    {
        var plan = Plan(Snapshot([], keepNewest: 0));

        Assert.Equal(Root, plan.Root);
        Assert.Equal(3, plan.EnrollmentEpoch);
        Assert.Equal(PackageStoreRetentionInventoryStatus.Complete, plan.InventoryStatus);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, plan.ProtectionKnowledge);
        Assert.Empty(plan.Entries);
        Assert.Empty(plan.Reasons);
        Assert.False(plan.HasRefusedEntries);
        Assert.False(plan.HasUnresolvedEvidence);
    }

    [Fact]
    public void Plan_WhenRetentionIsAbsent_RetainsEveryUnprotectedInstall()
    {
        var installs = new[]
        {
            Install("Widget", "1.0.0", "feed-a"),
            Install("Widget", "2.0.0", "feed-a")
        };

        var plan = Plan(Snapshot(installs, keepNewest: null));

        Assert.All(plan.Entries, entry => Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification));
        Assert.All(plan.Entries, entry => Assert.Contains(PackageStoreRetentionReason.RetentionPolicyAbsent, entry.Reasons));
        Assert.Empty(plan.Reasons);
    }

    [Fact]
    public void Plan_WhenBudgetIsZero_MarksOnlyUnprotectedInstallsEligible()
    {
        var protectedInstall = Install("Widget", "2.0.0", "feed-a");
        var inactiveInstall = Install("Widget", "1.0.0", "feed-a");
        var plan = Plan(Snapshot(
            [protectedInstall, inactiveInstall],
            keepNewest: 0,
            protectedInstalls: [Protect(protectedInstall, PackageStoreRetentionProtectionReason.Active)]));

        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, protectedInstall).Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedActive, Entry(plan, protectedInstall).Reasons);
        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(plan, inactiveInstall).Classification);
        Assert.Contains(PackageStoreRetentionReason.OutsideInactiveRetentionBudget, Entry(plan, inactiveInstall).Reasons);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Refused);
    }

    [Fact]
    public void Plan_ProtectedVersionsDoNotConsumeInactiveBudget()
    {
        var active = Install("Widget", "3.0.0", "feed-a");
        var newestInactive = Install("Widget", "2.0.0", "feed-a");
        var olderInactive = Install("Widget", "1.0.0", "feed-a");

        var plan = Plan(Snapshot(
            [active, newestInactive, olderInactive],
            keepNewest: 1,
            protectedInstalls: [Protect(active, PackageStoreRetentionProtectionReason.Active)]));

        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, active).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, newestInactive).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(plan, olderInactive).Classification);
    }

    [Theory]
    [InlineData("Active", "ProtectedActive")]
    [InlineData("RecoverableLastKnownGood", "ProtectedRecoverableLastKnownGood")]
    [InlineData("LiveUse", "ProtectedLiveUse")]
    public void Plan_ExactProtectionAlwaysRetainsItsInstall(
        string protectionReasonName,
        string expectedReasonName)
    {
        var protectionReason = Enum.Parse<PackageStoreRetentionProtectionReason>(protectionReasonName);
        var expectedReason = Enum.Parse<PackageStoreRetentionReason>(expectedReasonName);
        var install = Install("Widget", "1.0.0", "feed-a");
        var plan = Plan(Snapshot(
            [install],
            keepNewest: 0,
            protectedInstalls: [Protect(install, protectionReason)]));

        var entry = Entry(plan, install);
        Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
        Assert.Contains(expectedReason, entry.Reasons);
    }

    [Fact]
    public void Plan_KnownEmptyProtectionIsDifferentFromUnknownProtection()
    {
        var install = Install("Widget", "1.0.0", "feed-a");

        var knownEmpty = Plan(Snapshot([install], keepNewest: 0));
        var unknown = Plan(Snapshot(
            [install],
            keepNewest: 0,
            protectionKnowledge: PackageStoreRetentionProtectionKnowledge.Unknown));

        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(knownEmpty, install).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(unknown, install).Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectionUnknown, unknown.Reasons);
        Assert.Contains(PackageStoreRetentionReason.ProtectionUnknown, Entry(unknown, install).Reasons);
        Assert.DoesNotContain(unknown.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_UnknownProtectionPreservesPositiveProtectionFactsAndRefusesOtherRows()
    {
        var active = Install("Widget", "2.0.0", "feed-a");
        var inactive = Install("Widget", "1.0.0", "feed-a");
        var plan = Plan(Snapshot(
            [active, inactive],
            keepNewest: 0,
            protectionKnowledge: PackageStoreRetentionProtectionKnowledge.Unknown,
            protectedInstalls: [Protect(active, PackageStoreRetentionProtectionReason.Active)]));

        var activeEntry = Entry(plan, active);
        Assert.Equal(PackageStoreRetentionClassification.Retained, activeEntry.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedActive, activeEntry.Reasons);
        Assert.Contains(PackageStoreRetentionReason.ProtectionUnknown, activeEntry.Reasons);
        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, inactive).Classification);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Theory]
    [InlineData("Incomplete", "InventoryIncomplete")]
    [InlineData("Unknown", "InventoryUnknown")]
    public void Plan_NonCompleteInventoryRefusesUnprotectedRowsButKeepsKnownProtectedRows(
        string statusName,
        string expectedReasonName)
    {
        var status = Enum.Parse<PackageStoreRetentionInventoryStatus>(statusName);
        var expectedReason = Enum.Parse<PackageStoreRetentionReason>(expectedReasonName);
        var active = Install("Widget", "2.0.0", "feed-a");
        var inactive = Install("Widget", "1.0.0", "feed-a");
        var plan = Plan(Snapshot(
            [active, inactive],
            keepNewest: 0,
            inventoryStatus: status,
            protectedInstalls: [Protect(active, PackageStoreRetentionProtectionReason.LiveUse)]));

        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, active).Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedLiveUse, Entry(plan, active).Reasons);
        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, inactive).Classification);
        Assert.Contains(expectedReason, plan.Reasons);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_UsesNuGetPrereleaseOrderingInsteadOfLexicalOrdering()
    {
        var stable = Install("Widget", "1.0.0", "feed-a");
        var rcTen = Install("Widget", "1.0.0-rc.10", "feed-a");
        var rcTwo = Install("Widget", "1.0.0-rc.2", "feed-a");

        var plan = Plan(Snapshot([rcTwo, stable, rcTen], keepNewest: 2));

        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, stable).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, rcTen).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(plan, rcTwo).Classification);
    }

    [Fact]
    public void Plan_BudgetCountsVersionReleaseGroupsAndRetainsEveryFeedCopyAtTheCutoff()
    {
        var feedA = Install("Widget", "2.0.0+feed.a", "feed-a");
        var feedZ = Install("Widget", "2.0.0+feed.z", "feed-z");
        var older = Install("Widget", "1.0.0", "feed-a");

        var plan = Plan(Snapshot([older, feedZ, feedA], keepNewest: 1));

        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, feedA).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, feedZ).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(plan, older).Classification);
        Assert.Equal(
            new[] { "feed-a/Widget/2.0.0+feed.a", "feed-z/Widget/2.0.0+feed.z" },
            plan.Entries.Where(static entry => entry.Classification == PackageStoreRetentionClassification.Retained)
                .Select(static entry => entry.Install.RootRelativeInstallPath));
    }

    [Fact]
    public void Plan_EquivalentInputOrderAndPackageIdCasingProduceTheSameStablePlan()
    {
        var installs = new[]
        {
            Install("Widget", "2.0.0+z", "feed-z"),
            Install("widget", "2.0.0+a", "feed-a"),
            Install("WIDGET", "1.0.0", "feed-b"),
            Install("Other", "1.0.0", "feed-a"),
            Install("other", "3.0.0", "feed-b")
        };
        var forward = Plan(Snapshot(installs, keepNewest: 1));
        var reverse = Plan(Snapshot(installs.Reverse().ToArray(), keepNewest: 1));

        Assert.Equal(Signature(forward), Signature(reverse));
    }

    [Fact]
    public void Plan_NativePathReplacementConflictRefusesRowsAndPreservesPositiveProtectionReason()
    {
        var previous = Install("Widget", "1.0.0", "feed-a", directoryId: "previous-dir", completion: "previous-marker");
        var replacement = Install("Widget", "1.0.0", "feed-a", directoryId: "replacement-dir", completion: "replacement-marker");
        var plan = Plan(Snapshot(
            [replacement, previous],
            keepNewest: 0,
            protectedInstalls: [Protect(previous, PackageStoreRetentionProtectionReason.RecoverableLastKnownGood)]));

        var previousEntry = Entry(plan, previous);
        var replacementEntry = Entry(plan, replacement);
        Assert.Equal(PackageStoreRetentionClassification.Refused, previousEntry.Classification);
        Assert.Equal(PackageStoreRetentionClassification.Refused, replacementEntry.Classification);
        Assert.Contains(PackageStoreRetentionReason.IdentityConflict, previousEntry.Reasons);
        Assert.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood, previousEntry.Reasons);
        Assert.Contains(PackageStoreRetentionReason.IdentityConflict, replacementEntry.Reasons);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_ProtectionRequiresExactInstallIdentityBeyondPackageAndVersion()
    {
        var inventoried = Install("Widget", "1.0.0", "feed-a");
        var protectedElsewhere = Install("Widget", "1.0.0", "feed-b");
        var plan = Plan(Snapshot(
            [inventoried],
            keepNewest: 0,
            protectedInstalls: [Protect(protectedElsewhere, PackageStoreRetentionProtectionReason.Active)]));

        var entry = Entry(plan, inventoried);
        Assert.Equal(PackageStoreRetentionClassification.Refused, entry.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedInstallNotInventoried, plan.Reasons);
        Assert.DoesNotContain(PackageStoreRetentionReason.ProtectedActive, entry.Reasons);
        Assert.DoesNotContain(plan.Entries, candidate => candidate.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_MissingArchiveHashDoesNotEraseExactProtection()
    {
        var inventoried = new PackageInstallIdentity(
            Root,
            "Widget",
            "1.0.0",
            "feed-a/Widget/1.0.0",
            new PhysicalFileIdentity("test", "volume", "directory-with-hash"),
            "completion-with-hash",
            "sha512:observed");
        var protectedIdentity = new PackageInstallIdentity(
            Root,
            "Widget",
            "1.0.0",
            "feed-a/Widget/1.0.0",
            new PhysicalFileIdentity("test", "volume", "directory-with-hash"),
            "completion-with-hash");
        var plan = Plan(Snapshot(
            [inventoried],
            keepNewest: 0,
            protectedInstalls: [Protect(protectedIdentity, PackageStoreRetentionProtectionReason.LiveUse)]));

        var entry = Entry(plan, inventoried);
        Assert.Equal(PackageStoreRetentionClassification.Retained, entry.Classification);
        Assert.Contains(PackageStoreRetentionReason.ProtectedLiveUse, entry.Reasons);
    }

    [Fact]
    public void Plan_MalformedVersionRefusesItsPackageInsteadOfMakingRowsEligible()
    {
        var malformed = Install("Widget", "not-a-version", "feed-a");
        var otherwiseValid = Install("widget", "1.0.0", "feed-b");
        var plan = Plan(Snapshot([malformed, otherwiseValid], keepNewest: 0));

        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, malformed).Classification);
        Assert.Contains(PackageStoreRetentionReason.MalformedVersion, Entry(plan, malformed).Reasons);
        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, otherwiseValid).Classification);
        Assert.Contains(PackageStoreRetentionReason.MalformedVersion, Entry(plan, otherwiseValid).Reasons);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_RootMismatchRefusesAllRowsInTheSingleRootSnapshot()
    {
        var matchingRoot = Install("Widget", "2.0.0", "feed-a");
        var otherRoot = new PhysicalRootIdentity(new PhysicalFileIdentity("test", "volume", "other-root"));
        var mismatched = Install("Widget", "1.0.0", "feed-b", root: otherRoot);
        var plan = Plan(Snapshot([matchingRoot, mismatched], keepNewest: 0));

        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, matchingRoot).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Refused, Entry(plan, mismatched).Classification);
        Assert.Contains(PackageStoreRetentionReason.RootMismatch, plan.Reasons);
        Assert.DoesNotContain(plan.Entries, entry => entry.Classification == PackageStoreRetentionClassification.Eligible);
    }

    [Fact]
    public void Plan_DuplicateCompletedIdentityIsRefused()
    {
        var duplicate = Install("Widget", "1.0.0", "feed-a");
        var plan = Plan(Snapshot([duplicate, duplicate], keepNewest: 0));

        Assert.Equal(2, plan.Entries.Length);
        Assert.All(plan.Entries, entry =>
        {
            Assert.Equal(PackageStoreRetentionClassification.Refused, entry.Classification);
            Assert.Contains(PackageStoreRetentionReason.DuplicateIdentity, entry.Reasons);
        });
    }

    [Fact]
    public void Plan_CopiesInputCollectionsAndReturnsImmutableCollections()
    {
        var active = Install("Widget", "2.0.0", "feed-a");
        var inactive = Install("Widget", "1.0.0", "feed-a");
        var installs = new List<PackageInstallIdentity> { active, inactive };
        var protections = new List<PackageStoreRetentionProtectedInstall>
        {
            Protect(active, PackageStoreRetentionProtectionReason.Active)
        };
        var snapshot = Snapshot(installs, keepNewest: 0, protectedInstalls: protections);

        installs.Clear();
        protections.Clear();
        var plan = Plan(snapshot);
        var firstPlan = Signature(plan);

        installs.Add(Install("Other", "9.0.0", "feed-z"));
        protections.Add(Protect(inactive, PackageStoreRetentionProtectionReason.LiveUse));

        Assert.Equal(2, snapshot.CompletedInstalls.Length);
        Assert.Single(snapshot.ProtectedInstalls);
        Assert.Equal(firstPlan, Signature(plan));
        Assert.Equal(PackageStoreRetentionClassification.Retained, Entry(plan, active).Classification);
        Assert.Equal(PackageStoreRetentionClassification.Eligible, Entry(plan, inactive).Classification);
    }

    [Fact]
    public void Snapshot_WhenBudgetIsNegative_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot([], keepNewest: -1));
    }

    [Fact]
    public void Snapshot_WhenProtectionCollectionIsNull_DoesNotTreatItAsKnownEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => new PackageStoreRetentionSnapshot(
            Root,
            enrollmentEpoch: 3,
            PackageStoreRetentionInventoryStatus.Complete,
            [],
            PackageStoreRetentionProtectionKnowledge.Known,
            protectedInstalls: null!,
            keepNewestInactiveVersionsPerPackage: 0));
    }

    private static PackageStoreRetentionPlan Plan(PackageStoreRetentionSnapshot snapshot)
        => new PackageStoreRetentionPlanner().Plan(snapshot);

    private static PackageStoreRetentionSnapshot Snapshot(
        IReadOnlyList<PackageInstallIdentity> installs,
        int? keepNewest,
        IReadOnlyList<PackageStoreRetentionProtectedInstall>? protectedInstalls = null,
        PackageStoreRetentionInventoryStatus inventoryStatus = PackageStoreRetentionInventoryStatus.Complete,
        PackageStoreRetentionProtectionKnowledge protectionKnowledge = PackageStoreRetentionProtectionKnowledge.Known,
        PhysicalRootIdentity? root = null)
        => new(
            root ?? Root,
            enrollmentEpoch: 3,
            inventoryStatus,
            installs,
            protectionKnowledge,
            protectedInstalls ?? [],
            keepNewest);

    private static PackageInstallIdentity Install(
        string packageId,
        string version,
        string feed,
        string? directoryId = null,
        string? completion = null,
        PhysicalRootIdentity? root = null)
        => new(
            root ?? Root,
            packageId,
            version,
            $"{feed}/{packageId}/{version}",
            new PhysicalFileIdentity("test", "volume", directoryId ?? $"dir-{feed}-{packageId}-{version}"),
            completion ?? $"marker-{feed}-{packageId}-{version}");

    private static PackageStoreRetentionProtectedInstall Protect(
        PackageInstallIdentity install,
        PackageStoreRetentionProtectionReason reason)
        => new(install, reason);

    private static PackageStoreRetentionPlanEntry Entry(PackageStoreRetentionPlan plan, PackageInstallIdentity install)
        => Assert.Single(plan.Entries.Where(entry => entry.Install.Root == install.Root &&
                                                     entry.Install.DirectoryIdentity == install.DirectoryIdentity &&
                                                     string.Equals(entry.Install.CompletionIdentity,
                                                         install.CompletionIdentity,
                                                         StringComparison.Ordinal)));

    private static string Signature(PackageStoreRetentionPlan plan)
        => string.Join(Environment.NewLine, plan.Entries.Select(entry =>
            $"{entry.Install.PackageId}|{entry.Install.Version}|{entry.Install.RootRelativeInstallPath}|{entry.Classification}|{string.Join(",", entry.Reasons)}"));
}
