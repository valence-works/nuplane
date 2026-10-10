using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Tests.Coordination;

public sealed class RootMembershipRecordTests
{
    [Fact]
    public void Constructor_Incomplete_KeepsOldNewUnionAndCopiesExplicitTargets()
    {
        var prior = Member("old", Ack("old"));
        var declared = Member("new", new RootMemberRecord.DeclaredBinding());
        var members = new List<RootMemberRecord> { prior, declared };
        var targets = new List<string> { "new" };

        var ledger = Ledger(members, targets);
        members.Clear();
        targets.Clear();

        Assert.Equal(new[] { "old", "new" }, ledger.Members.Select(member => member.MemberId));
        Assert.Equal("new", Assert.Single(ledger.TargetMemberIds));
        Assert.IsType<RootMemberRecord.DeclaredBinding>(ledger.Members[1].Binding);
        Assert.Throws<NotSupportedException>(() => ((IList<RootMemberRecord>)ledger.Members).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ledger.TargetMemberIds).Clear());
    }

    [Fact]
    public void Constructor_CompleteKnownEmptyMembers_RequiresExactAcknowledgedTargetSet()
    {
        var members = new[] { Member("one", Ack("one")), Member("two", Ack("two")) };
        var ledger = Ledger(members, ["two", "one"], RootMembershipStatus.Complete);

        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        Assert.Null(ledger.PendingStateCommit);
        Assert.Equal("ledger-digest", ledger.LedgerDigest);
        Assert.All(ledger.Members, member => Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding));
        Assert.Throws<ArgumentException>(() => Ledger(members, ["one"], RootMembershipStatus.Complete));
    }

    [Theory]
    [InlineData("declared")]
    [InlineData("prospective")]
    [InlineData("unprotected")]
    [InlineData("unknown-active")]
    [InlineData("unknown-recovery")]
    [InlineData("legacy-recovery")]
    public void Constructor_UnresolvedMember_CanRemainIncompleteButCannotBecomeComplete(string kind)
    {
        var binding = kind switch
        {
            "declared" => (RootMemberRecord.MemberBinding)new RootMemberRecord.DeclaredBinding(),
            "prospective" => Prospective(),
            "unprotected" => Unprotected(),
            _ => Ack("member", unknownActive: kind == "unknown-active", unknownRecovery: kind == "unknown-recovery", legacyUnknown: kind == "legacy-recovery")
        };
        var member = Member("member", binding);

        Assert.Single(Ledger([member], ["member"]).Members);
        Assert.Throws<ArgumentException>(() => Ledger([member], ["member"], RootMembershipStatus.Complete));
    }

    [Fact]
    public void Pending_AbsentAndLegacyExisting_PreserveDifferentPriorEvidenceAndFirstRevision()
    {
        var absent = Member("new", Prospective());
        var legacy = Member("legacy", Unprotected());
        var first = Pending(Root(), 1, absent, Protection("new"));
        var migration = Pending(Root(), 1, legacy, Protection("legacy"));

        Assert.IsType<RootMemberRecord.ProspectiveBinding>(first.Prior);
        var prior = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(migration.Prior);
        Assert.True(prior.ProtectionMetadataAbsent);
        Assert.Equal("legacy-body", prior.StateBodyDigest);
        Assert.Equal(1, first.NextProtectionRecord.Revision);
        Assert.Equal(1, migration.NextProtectionRecord.Revision);
        Assert.Single(Ledger([absent, legacy], ["new", "legacy"], pending: migration).TargetMemberIds.Where(id => id == "legacy"));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, legacy, Protection("legacy", revision: 2)));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, Member("declared", new RootMemberRecord.DeclaredBinding()), Protection("declared")));
    }

    [Fact]
    public void Pending_RecordsPriorLedgerAndPublicationIdentityAndValidatesRecoveryBaseline()
    {
        var prospective = Member("member", Prospective());
        var nextFirst = Protection("member");
        var incomplete = Pending(Root(), 1, prospective, nextFirst);

        Assert.Equal(RootMembershipStatus.Incomplete, incomplete.PriorMembershipStatus);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", incomplete.PriorLedgerDigest);
        Assert.NotEqual(Guid.Empty, incomplete.PublicationId);
        Assert.Equal(PendingStateCommitResolution.Unresolved, incomplete.Resolution);

        var acknowledged = Member("member", Ack("member"));
        var complete = Pending(Root(), 1, acknowledged, Protection("member", revision: 2), RootMembershipStatus.Complete);
        Assert.Equal(RootMembershipStatus.Complete, complete.PriorMembershipStatus);

        Assert.Throws<ArgumentOutOfRangeException>(() => Pending(Root(), 1, acknowledged, Protection("member", revision: 2), (RootMembershipStatus)9));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst, RootMembershipStatus.Complete));
        Assert.Throws<ArgumentNullException>(() => Pending(Root(), 1, prospective, nextFirst, priorLedgerDigest: null!));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst, priorLedgerDigest: " "));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst, priorLedgerDigest: "A".PadRight(64, 'a')));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst, priorLedgerDigest: "bad-digest"));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst, publicationId: Guid.Empty));

        var stagedProspective = Identity("staged");
        var prospectivePublication = Pending(Root(), 1, prospective, nextFirst, stagedStateFileIdentity: stagedProspective);
        Assert.Equal(stagedProspective, prospectivePublication.StagedStateFileIdentity);
        Assert.NotSame(stagedProspective, prospectivePublication.StagedStateFileIdentity);
        Assert.Null(prospectivePublication.BackupStateFileIdentity);
        Assert.Equal(PendingStateCommitResolution.Prior,
            Pending(Root(), 1, prospective, nextFirst, stagedStateFileIdentity: stagedProspective,
                resolution: PendingStateCommitResolution.Prior).Resolution);
        Assert.Equal(PendingStateCommitResolution.Next,
            Pending(Root(), 1, prospective, nextFirst, stagedStateFileIdentity: stagedProspective,
                resolution: PendingStateCommitResolution.Next).Resolution);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pending(Root(), 1, prospective, nextFirst,
            resolution: (PendingStateCommitResolution)99));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst,
            resolution: PendingStateCommitResolution.Next));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, prospective, nextFirst,
            backupStateFileIdentity: Identity("backup")));

        var existing = Member("existing", Unprotected());
        var staged = Identity("staged-existing");
        var backup = Identity("backup-existing");
        var existingPublication = Pending(Root(), 1, existing, Protection("existing"),
            stagedStateFileIdentity: staged, backupStateFileIdentity: backup);
        Assert.NotSame(staged, existingPublication.StagedStateFileIdentity);
        Assert.NotSame(backup, existingPublication.BackupStateFileIdentity);
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, existing, Protection("existing"),
            backupStateFileIdentity: backup));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, existing, Protection("existing"),
            stagedStateFileIdentity: staged));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, existing, Protection("existing"),
            stagedStateFileIdentity: Identity("legacy-file"), backupStateFileIdentity: backup));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, existing, Protection("existing"),
            stagedStateFileIdentity: staged, backupStateFileIdentity: Identity("legacy-file")));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, existing, Protection("existing"),
            stagedStateFileIdentity: staged, backupStateFileIdentity: staged));
    }

    [Fact]
    public void Pending_AcknowledgedPrior_BindsExactSlotFileBodyAndRevision()
    {
        var member = Member("member", Ack("member", revision: 3));
        var pending = Pending(Root(), 1, member, Protection("member", revision: 4), RootMembershipStatus.Complete);
        var ledger = Ledger([member], ["member"], pending: pending);

        Assert.Same(pending, ledger.PendingStateCommit);
        Assert.Equal(3, Assert.IsType<RootMemberRecord.AcknowledgedBinding>(pending.Prior).ProtectionRecord.Revision);
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, member, Protection("member", revision: 3)));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, member, Protection("member", revision: 5)));
        Assert.Throws<ArgumentException>(() => Ledger([member], ["member"], RootMembershipStatus.Complete, pending));

        foreach (var changed in new[]
        {
            Ack("member", revision: 3, basename: "renamed.json"),
            Ack("member", revision: 3, fileId: "replacement"),
            Ack("member", revision: 3, body: "third-body"),
            Ack("member", revision: 3, unknownRecovery: true),
            Ack("member", revision: 3, legacyUnknown: true)
        })
        {
            Assert.Throws<ArgumentException>(() => Ledger([Member("member", changed)], ["member"], pending: pending));
        }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("recovery")]
    [InlineData("legacy")]
    public void Pending_SameDigestsButDifferentProtectionPayload_Refuses(string changedField)
    {
        var original = Member("member", Ack("member", revision: 3));
        var pending = Pending(Root(), 1, original, Protection("member", revision: 4));
        var substituted = Member("member", Ack("member", revision: 3, unknownActive: changedField == "active",
            unknownRecovery: changedField == "recovery", legacyUnknown: changedField == "legacy"));

        Assert.Throws<ArgumentException>(() => Ledger([substituted], ["member"], pending: pending));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("member")]
    [InlineData("epoch")]
    [InlineData("active")]
    [InlineData("recovery")]
    [InlineData("legacy")]
    public void Pending_ForeignOrUnknownNext_Refuses(string mismatch)
    {
        var member = Member("member", Prospective());
        var next = Protection(mismatch == "member" ? "foreign" : "member", epoch: mismatch == "epoch" ? 2 : 1,
            root: mismatch == "root" ? Root("foreign") : null, unknownActive: mismatch == "active",
            unknownRecovery: mismatch == "recovery", legacyUnknown: mismatch == "legacy");
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, member, next));
    }

    [Fact]
    public void Pending_FuturePriorOrOverflow_Refuses()
    {
        var future = Member("member", Ack("member", epoch: 2));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, future, Protection("member", revision: 2)));
        var overflow = Member("member", Ack("member", revision: long.MaxValue));
        Assert.Throws<ArgumentException>(() => Pending(Root(), 1, overflow, Protection("member")));
    }

    [Fact]
    public void Constructor_PendingMustMatchRootEpochMemberAndProspectiveParent()
    {
        var member = Member("member", Prospective());
        var pending = Pending(Root(), 1, member, Protection("member"));
        Assert.Throws<ArgumentException>(() => Ledger([member], ["member"], pending: pending, root: Root("foreign")));
        Assert.Throws<ArgumentException>(() => Ledger([member], ["member"], pending: pending, epoch: 2));
        Assert.Throws<ArgumentException>(() => Ledger([], [], pending: pending));
        Assert.Throws<ArgumentException>(() => Ledger([Member("member", Prospective("different-parent"))], ["member"], pending: pending));
    }

    [Fact]
    public void Constructor_MalformedMembershipAndUnsupportedSchema_Refuses()
    {
        var member = Member("member", Prospective());
        Assert.Throws<ArgumentException>(() => Ledger([member, member], ["member"]));
        Assert.Throws<ArgumentException>(() => Ledger([member], ["member", "member"]));
        Assert.Throws<ArgumentException>(() => Ledger([member], ["missing"]));
        Assert.Throws<ArgumentException>(() => Ledger([null!], []));
        Assert.Throws<ArgumentException>(() => Ledger([member], [" "]));
        Assert.Single(Ledger([member], ["member"], schema: 2).Members);
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger([member], ["member"], schema: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger([member], ["member"], status: (RootMembershipStatus)99));
        Assert.Throws<ArgumentException>(() => new RootMembershipRecord(1, Root(), 1, RootMembershipStatus.Incomplete, [member], ["member"], [], null, " "));
        Assert.Throws<ArgumentException>(() => new RootMemberRecord.ExistingUnprotectedBinding(Slot(), Identity("file"), "body", false));
    }

    [Fact]
    public void Constructor_DuplicateCanonicalSlotAcrossDifferentMembers_RefusesWithoutFoldingNames()
    {
        var one = Member("one", Ack("one", basename: "same.json"));
        var two = Member("two", Ack("two", basename: "same.json"));
        Assert.Throws<ArgumentException>(() => Ledger([one, two], ["one", "two"]));
        var legacy = Member("legacy", new RootMemberRecord.ExistingUnprotectedBinding(Slot("same.json"), Identity("legacy"), "body", true));
        Assert.Throws<ArgumentException>(() => Ledger([one, legacy], ["one", "legacy"]));

        var differentCase = Member("two", Ack("two", basename: "Same.json"));
        Assert.Equal(2, Ledger([one, differentCase], ["one", "two"]).Members.Count);
    }

    [Fact]
    public void Constructor_EpochMigration_PreservesPriorButRequiresCurrentEpochForComplete()
    {
        var old = Member("member", Ack("member", epoch: 1));
        Assert.Single(Ledger([old], ["member"], epoch: 2).Members);
        Assert.Throws<ArgumentException>(() => Ledger([old], ["member"], RootMembershipStatus.Complete, epoch: 2));
        Assert.Throws<ArgumentException>(() => Ledger([Member("member", Ack("member", epoch: 3))], ["member"], epoch: 2));
        Assert.Single(Ledger([Member("member", Ack("member", epoch: 2))], ["member"], RootMembershipStatus.Complete, epoch: 2).Members);
    }

    [Fact]
    public void Constructor_MemberRetirement_KeepsPriorEvidenceAndRejectsConflictingTargets()
    {
        var binding = Ack("old");
        var old = Member("old", binding);
        var retirement = new RootMemberRetirementEvidence("old", binding, 2, "migration-proof");
        var next = Member("new", Ack("new", epoch: 2));
        var ledger = Ledger([old, next], ["new"], retired: [retirement], epoch: 2);

        Assert.Equal("old", Assert.Single(ledger.RetiredMembers).MemberId);
        Assert.Equal(binding.StateSlot, Assert.Single(ledger.RetiredMembers).PriorBinding.StateSlot);
        Assert.Throws<ArgumentException>(() => Ledger([old, next], ["old", "new"], retired: [retirement], epoch: 2));
        Assert.Throws<ArgumentException>(() => Ledger([Member("old", Ack("old", fileId: "substituted")), next], ["new"], retired: [retirement], epoch: 2));
        Assert.Throws<ArgumentException>(() => Ledger([old, next], ["new"], retired: [retirement, retirement], epoch: 2));
        Assert.Throws<ArgumentException>(() => Ledger([old, next], ["new"], retired: [retirement], epoch: 3));

        var complete = Ledger([next], ["new"], RootMembershipStatus.Complete, retired: [retirement], epoch: 2);
        Assert.Equal("old", Assert.Single(complete.RetiredMembers).MemberId);
    }

    private static RootMembershipRecord Ledger(IEnumerable<RootMemberRecord> members, IEnumerable<string> targets,
        RootMembershipStatus status = RootMembershipStatus.Incomplete, PendingStateCommit? pending = null,
        IEnumerable<RootMemberRetirementEvidence>? retired = null, long epoch = 1, PhysicalRootIdentity? root = null, int schema = 1)
        => new(schema, root ?? Root(), epoch, status, members, targets, retired ?? [], pending, "ledger-digest");

    private static PendingStateCommit Pending(PhysicalRootIdentity root, long epoch, RootMemberRecord member,
        PackageProtectionRecord next, RootMembershipStatus priorStatus = RootMembershipStatus.Incomplete,
        string priorLedgerDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Guid? publicationId = null, PhysicalFileIdentity? stagedStateFileIdentity = null,
        PhysicalFileIdentity? backupStateFileIdentity = null,
        PendingStateCommitResolution resolution = PendingStateCommitResolution.Unresolved)
        => new(root, epoch, priorStatus, priorLedgerDigest,
            publicationId ?? Guid.Parse("10000000-0000-0000-0000-000000000001"), member, next,
            stagedStateFileIdentity, backupStateFileIdentity, resolution);

    private static RootMemberRecord Member(string id, RootMemberRecord.MemberBinding binding)
        => new(id, $"/external-state/{id}.json", binding);

    private static RootMemberRecord.ProspectiveBinding Prospective(string parent = "external-parent")
        => new(Identity(parent), Names(), "state.json");

    private static RootMemberRecord.ExistingUnprotectedBinding Unprotected()
        => new(Slot(), Identity("legacy-file"), "legacy-body", true);

    private static RootMemberRecord.AcknowledgedBinding Ack(string id, long revision = 1, long epoch = 1,
        string? basename = null, string fileId = "file", string body = "body", bool unknownActive = false,
        bool unknownRecovery = false, bool legacyUnknown = false)
        => new(Slot(basename ?? $"{id}.json"), Identity(fileId), Protection(id, revision, epoch, body: body,
            unknownActive: unknownActive, unknownRecovery: unknownRecovery, legacyUnknown: legacyUnknown));

    private static PackageProtectionRecord Protection(string member, long revision = 1, long epoch = 1,
        PhysicalRootIdentity? root = null, string body = "body", bool unknownActive = false,
        bool unknownRecovery = false, bool legacyUnknown = false)
        => new(1, root ?? Root(), epoch, member, revision, body, "protection",
            Closure(unknownActive), Closure(unknownRecovery), [], legacyUnknown);

    private static PackageProtectionClosure Closure(bool unknown)
        => unknown
            ? new(PackageProtectionClosureKnowledge.Unknown, PackageProtectionUnknownReasonCode.LegacyProtectionMissing, null)
            : new(PackageProtectionClosureKnowledge.Known, null, []);

    private static StateSlotIdentity Slot(string basename = "state.json") => new(Identity("external-parent"), Names(), basename);
    private static PhysicalStoreNameSemantics Names() => new("test-profile-v1", PhysicalStoreNameEncoding.Utf8, true, false);
    private static PhysicalFileIdentity Identity(string id) => new("test", "volume", id);
    private static PhysicalRootIdentity Root(string id = "store") => new(Identity(id));
}
