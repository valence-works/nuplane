using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Tests.Coordination;

public sealed class RootMembershipPayloadSerializerTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly RootMembershipPayloadSerializer _serializer = new();

    [Theory]
    [InlineData("declared")]
    [InlineData("prospective")]
    [InlineData("unprotected")]
    [InlineData("acknowledged")]
    public void RoundTrip_IncompleteMembership_PreservesEveryBindingVariant(string variant)
    {
        var binding = variant switch
        {
            "declared" => (RootMemberRecord.MemberBinding)new RootMemberRecord.DeclaredBinding(),
            "prospective" => Prospective(),
            "unprotected" => Unprotected(),
            _ => Acknowledged("member")
        };
        var original = Ledger([Member("member", binding)], ["member"]);
        var payload = _serializer.Serialize(original);

        var roundTrip = _serializer.Deserialize(payload);

        Assert.Equal(original.LedgerDigest, roundTrip.LedgerDigest);
        Assert.Equal(binding.GetType(), roundTrip.Members[0].Binding.GetType());
        Assert.Equal(original.TargetMemberIds, roundTrip.TargetMemberIds);
        Assert.Equal(payload, _serializer.Serialize(roundTrip));
    }

    [Fact]
    public void RoundTrip_CompleteMembershipAndRetirementEvidence_VerifiesNestedDigests()
    {
        var old = Member("old", Acknowledged("old"));
        var current = Member("current", Acknowledged("current", epoch: 2));
        var retirement = new RootMemberRetirementEvidence(
            "old", (RootMemberRecord.AcknowledgedBinding)old.Binding, 2, Digest);
        var original = Ledger([current], ["current"], RootMembershipStatus.Complete, epoch: 2, retired: [retirement]);

        var roundTrip = _serializer.Deserialize(_serializer.Serialize(original));

        Assert.Equal(RootMembershipStatus.Complete, roundTrip.Status);
        Assert.Equal("current", Assert.Single(roundTrip.TargetMemberIds));
        Assert.Equal("old", Assert.Single(roundTrip.RetiredMembers).MemberId);
        Assert.Equal(retirement.PriorBinding.ProtectionRecord.ProtectionDigest,
            roundTrip.RetiredMembers[0].PriorBinding.ProtectionRecord.ProtectionDigest);
    }

    [Theory]
    [InlineData("prospective", "unresolved")]
    [InlineData("unprotected", "unresolved")]
    [InlineData("acknowledged", "unresolved")]
    [InlineData("acknowledged-no-artifacts", "unresolved")]
    [InlineData("prospective", "prior")]
    [InlineData("prospective", "next")]
    [InlineData("prospective-no-artifacts", "prior")]
    [InlineData("acknowledged-no-artifacts", "prior")]
    [InlineData("unprotected", "prior")]
    [InlineData("unprotected", "next")]
    [InlineData("acknowledged", "prior")]
    [InlineData("acknowledged", "next")]
    public void RoundTrip_PendingCommit_PreservesPriorNextAndResolution(string priorKind, string resolutionName)
    {
        var priorBinding = priorKind switch
        {
            "prospective" or "prospective-no-artifacts" => (RootMemberRecord.MemberBinding)Prospective(),
            "unprotected" => Unprotected(),
            _ => Acknowledged("member", revision: 2)
        };
        var member = Member("member", priorBinding);
        var nextRevision = priorKind.StartsWith("acknowledged", StringComparison.Ordinal) ? 3 : 1;
        var stagedIdentity = priorKind.EndsWith("-no-artifacts", StringComparison.Ordinal) ? null : Identity("staged-state");
        var backupIdentity = priorKind.StartsWith("prospective", StringComparison.Ordinal) || stagedIdentity is null ? null : Identity("backup-state");
        var resolution = resolutionName switch
        {
            "unresolved" => PendingStateCommitResolution.Unresolved,
            "prior" => PendingStateCommitResolution.Prior,
            "next" => PendingStateCommitResolution.Next,
            _ => throw new ArgumentOutOfRangeException(nameof(resolutionName))
        };
        var pending = new PendingStateCommit(
            Root(), 1, RootMembershipStatus.Incomplete, Digest,
            Guid.Parse("29d3a04d-d5f9-4628-aed8-5e5e14349dc5"), member,
            Protection("member", nextRevision), stagedIdentity, backupIdentity, resolution);
        var original = Ledger([member], ["member"], pending: pending);

        var roundTrip = _serializer.Deserialize(_serializer.Serialize(original));
        var restoredPending = Assert.IsType<PendingStateCommit>(roundTrip.PendingStateCommit);

        Assert.Equal(pending.PublicationId, restoredPending.PublicationId);
        Assert.Equal(pending.PriorMembershipStatus, restoredPending.PriorMembershipStatus);
        Assert.Equal(pending.PriorLedgerDigest, restoredPending.PriorLedgerDigest);
        Assert.Equal(pending.Prior.GetType(), restoredPending.Prior.GetType());
        Assert.Equal(pending.NextProtectionRecord.ProtectionDigest, restoredPending.NextProtectionRecord.ProtectionDigest);
        Assert.Equal(stagedIdentity, restoredPending.StagedStateFileIdentity);
        Assert.Equal(backupIdentity, restoredPending.BackupStateFileIdentity);
        Assert.Equal(resolution, restoredPending.Resolution);
        Assert.Equal(_serializer.Serialize(original), _serializer.Serialize(roundTrip));
    }

    [Fact]
    public void Deserialize_RequiresSupportedPendingResolutionAndNextStageIdentity()
    {
        var member = Member("member", Prospective());
        var pending = new PendingStateCommit(
            Root(), 1, RootMembershipStatus.Incomplete, Digest,
            Guid.Parse("a40ca718-29e9-4748-8c6f-5ea7285f9304"), member,
            Protection("member"), Identity("staged-state"), resolution: PendingStateCommitResolution.Next);
        var payload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger([member], ["member"], pending: pending)));

        var missingResolution = Parse(payload);
        missingResolution["pendingStateCommit"]!.AsObject().Remove("resolution");
        Assert.Throws<JsonException>(() => Deserialize(missingResolution));

        var unsupportedResolution = Parse(payload);
        unsupportedResolution["pendingStateCommit"]!["resolution"] = 99;
        Assert.Throws<JsonException>(() => Deserialize(unsupportedResolution));

        var nextWithoutStage = Parse(payload);
        nextWithoutStage["pendingStateCommit"]!.AsObject().Remove("stagedStateFileIdentity");
        var exception = Assert.Throws<JsonException>(() => Deserialize(nextWithoutStage));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void Deserialize_RejectsDuplicateUnknownMissingAndUnsupportedShape()
    {
        var payload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [Member("member", new RootMemberRecord.DeclaredBinding())], ["member"])));

        Assert.Throws<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(
            payload[..^1] + ",\"ledgerDigest\":\"" + Digest + "\"}")));
        Assert.Throws<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(
            payload[..^1] + ",\"futureField\":true}")));

        var missing = Parse(payload);
        missing.AsObject().Remove("targetMemberIds");
        Assert.Throws<JsonException>(() => Deserialize(missing));

        var unsupportedTag = Parse(payload);
        unsupportedTag["members"]![0]!["binding"]!["tag"] = 99;
        Assert.Throws<JsonException>(() => Deserialize(unsupportedTag));

        var mismatchedTag = Parse(payload);
        mismatchedTag["members"]![0]!["binding"]!["requestedBasename"] = "wrong-variant-field";
        Assert.Throws<JsonException>(() => Deserialize(mismatchedTag));

        var unsupportedStatus = Parse(payload);
        unsupportedStatus["status"] = 99;
        Assert.Throws<JsonException>(() => Deserialize(unsupportedStatus));

        var unsupportedSchema = Parse(payload);
        unsupportedSchema["schemaVersion"] = 99;
        Assert.Throws<JsonException>(() => Deserialize(unsupportedSchema));

        var invalidNameEncoding = Encoding.UTF8.GetString(_serializer.Serialize(
            Ledger([Member("member", Prospective())], ["member"])));
        var invalidEnum = Parse(invalidNameEncoding);
        invalidEnum["members"]![0]!["binding"]!["nameSemantics"]!["encoding"] = 99;
        Assert.Throws<JsonException>(() => Deserialize(invalidEnum));

        var invalidDigest = Parse(payload);
        invalidDigest["ledgerDigest"] = "not-a-digest";
        Assert.Throws<JsonException>(() => Deserialize(invalidDigest));
    }

    [Fact]
    public void Deserialize_RejectsSameValueDuplicatePropertiesInNestedIdentityAndPendingBinding()
    {
        var identityPayload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [Member("member", new RootMemberRecord.DeclaredBinding())], ["member"])));
        var duplicateIdentity = DuplicateProperty(identityPayload, "\"fileId\":\"store-root\"");
        Assert.Throws<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicateIdentity)));

        var member = Member("member", Prospective());
        var pending = new PendingStateCommit(
            Root(), 1, RootMembershipStatus.Incomplete, Digest,
            Guid.Parse("ef5745b0-b0d4-4e2a-82f0-9d90253cbda9"), member, Protection("member"));
        var pendingPayload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [member], ["member"], pending: pending)));
        var pendingStart = pendingPayload.IndexOf("\"pendingStateCommit\":", StringComparison.Ordinal);
        Assert.True(pendingStart >= 0);
        var duplicatePendingPrior = DuplicateProperty(
            pendingPayload,
            "\"requestedBasename\":\"state.json\"",
            searchStart: pendingStart);
        Assert.Throws<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicatePendingPrior)));

        var acknowledgedPayload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [Member("member", Acknowledged("member"))], ["member"])));
        var protectionStart = acknowledgedPayload.IndexOf("\"protectionRecord\":", StringComparison.Ordinal);
        Assert.True(protectionStart >= 0);
        var duplicateProtectionField = DuplicateProperty(
            acknowledgedPayload,
            "\"memberId\":\"member\"",
            searchStart: protectionStart);
        Assert.Throws<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicateProtectionField)));
    }

    [Fact]
    public void Deserialize_RejectsExplicitNullForRequiredFieldsAndItems()
    {
        var declaredPayload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [Member("member", new RootMemberRecord.DeclaredBinding())], ["member"])));
        var invalidPayloads = new List<JsonNode>();

        var nullMembers = Parse(declaredPayload);
        nullMembers["members"] = null;
        invalidPayloads.Add(nullMembers);

        var nullMember = Parse(declaredPayload);
        nullMember["members"]![0] = null;
        invalidPayloads.Add(nullMember);

        var nullBinding = Parse(declaredPayload);
        nullBinding["members"]![0]!["binding"] = null;
        invalidPayloads.Add(nullBinding);

        var nullPhysicalIdentityField = Parse(declaredPayload);
        nullPhysicalIdentityField["rootIdentity"]!["handleIdentity"]!["fileId"] = null;
        invalidPayloads.Add(nullPhysicalIdentityField);

        var nullTargetMember = Parse(declaredPayload);
        nullTargetMember["targetMemberIds"]![0] = null;
        invalidPayloads.Add(nullTargetMember);

        var member = Member("member", Prospective());
        var pending = new PendingStateCommit(
            Root(), 1, RootMembershipStatus.Incomplete, Digest,
            Guid.Parse("a384dd58-0dc1-4c7c-9d8e-8772bdb639c9"), member, Protection("member"));
        var pendingPayload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [member], ["member"], pending: pending)));
        var nullPendingPrior = Parse(pendingPayload);
        nullPendingPrior["pendingStateCommit"]!["prior"] = null;
        invalidPayloads.Add(nullPendingPrior);

        foreach (var invalidPayload in invalidPayloads)
            Assert.Throws<JsonException>(() => Deserialize(invalidPayload));
    }

    [Fact]
    public void Deserialize_RejectsCorruptLedgerAndNestedProtectionDigests()
    {
        var payload = Encoding.UTF8.GetString(_serializer.Serialize(Ledger(
            [Member("member", Acknowledged("member"))], ["member"])));

        var corruptLedger = Parse(payload);
        corruptLedger["ledgerDigest"] = Digest;
        Assert.Throws<JsonException>(() => Deserialize(corruptLedger));

        var corruptProtection = Parse(payload);
        corruptProtection["members"]![0]!["binding"]!["protectionRecord"]!["protectionDigest"] = Digest;
        Assert.Throws<JsonException>(() => Deserialize(corruptProtection));

        var invalidPendingIdentity = _serializer.Serialize(Ledger(
        [
            Member("member", Prospective())
        ], ["member"], pending: new PendingStateCommit(
            Root(), 1, RootMembershipStatus.Incomplete, Digest,
            Guid.Parse("0b4bd0c0-e1c1-4c32-9d77-9bce8fa9f4db"),
            Member("member", Prospective()), Protection("member"), Identity("staged"))));
        var mismatchedStage = Parse(Encoding.UTF8.GetString(invalidPendingIdentity));
        mismatchedStage["pendingStateCommit"]!["stagedStateFileIdentity"]!["fileId"] = "another-stage";
        Assert.Throws<JsonException>(() => Deserialize(mismatchedStage));
    }

    [Fact]
    public void PayloadSize_IsBoundedOnReadAndWrite()
    {
        Assert.Throws<JsonException>(() => _serializer.Deserialize(new byte[RootMembershipPayloadSerializer.MaximumPayloadBytes + 1]));

        var oversized = Ledger(
            [Member("member", new RootMemberRecord.DeclaredBinding(), new string('x', RootMembershipPayloadSerializer.MaximumPayloadBytes + 1))],
            ["member"]);
        Assert.Throws<InvalidOperationException>(() => _serializer.Serialize(oversized));
    }

    private RootMembershipRecord Deserialize(JsonNode payload)
        => _serializer.Deserialize(Encoding.UTF8.GetBytes(payload.ToJsonString()));

    private static string DuplicateProperty(string payload, string propertyPair, int searchStart = 0)
    {
        var index = payload.IndexOf(propertyPair, searchStart, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected one serialized occurrence of {propertyPair}.");
        return payload.Insert(index + propertyPair.Length, "," + propertyPair);
    }

    private static JsonNode Parse(string payload) => JsonNode.Parse(payload)!;

    private static RootMembershipRecord Ledger(
        IEnumerable<RootMemberRecord> members,
        IEnumerable<string> targets,
        RootMembershipStatus status = RootMembershipStatus.Incomplete,
        long epoch = 1,
        IEnumerable<RootMemberRetirementEvidence>? retired = null,
        PendingStateCommit? pending = null)
    {
        var memberArray = members.ToArray();
        var targetArray = targets.ToArray();
        var retiredArray = retired?.ToArray() ?? [];
        var candidate = new RootMembershipRecord(1, Root(), epoch, status, memberArray, targetArray, retiredArray, pending, Digest);
        var digest = ProtectionDigest.Ledger(candidate);
        return new RootMembershipRecord(1, Root(), epoch, status, memberArray, targetArray, retiredArray, pending, digest);
    }

    private static RootMemberRecord Member(string id, RootMemberRecord.MemberBinding binding, string? locator = null)
        => new(id, locator ?? $"/external/{id}.json", binding);

    private static RootMemberRecord.ProspectiveBinding Prospective()
        => new(Identity("parent"), NameSemantics(), "state.json");

    private static RootMemberRecord.ExistingUnprotectedBinding Unprotected()
        => new(Slot(), Identity("legacy"), Digest, protectionMetadataAbsent: true);

    private static RootMemberRecord.AcknowledgedBinding Acknowledged(string memberId, long revision = 1, long epoch = 1)
        => new(Slot($"{memberId}.json"), Identity($"state-{memberId}"), Protection(memberId, revision, epoch));

    private static PackageProtectionRecord Protection(string memberId, long revision = 1, long epoch = 1)
    {
        var candidate = new PackageProtectionRecord(
            PackageProtectionRecord.CurrentSchemaVersion,
            Root(), epoch, memberId, revision, Digest, Digest,
            KnownClosure(), KnownClosure(), [], legacyUnknownRecovery: false);
        var protectionDigest = ProtectionDigest.Protection(candidate);
        return new PackageProtectionRecord(
            candidate.SchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch, candidate.MemberId,
            candidate.Revision, candidate.StateBodyDigest, protectionDigest,
            candidate.ActiveClosure, candidate.RecoverableClosure, candidate.RetiredGraphs, candidate.LegacyUnknownRecovery);
    }

    private static PackageProtectionClosure KnownClosure()
        => new(PackageProtectionClosureKnowledge.Known, unknownReason: null, []);

    private static StateSlotIdentity Slot(string basename = "state.json")
        => new(Identity("parent"), NameSemantics(), basename);

    private static PhysicalStoreNameSemantics NameSemantics()
        => new("test-profile-v1", PhysicalStoreNameEncoding.Utf8, caseSensitive: true, normalizationInsensitive: false);

    private static PhysicalFileIdentity Identity(string id)
        => new("test-provider", "test-volume", id);

    private static PhysicalRootIdentity Root()
        => new(Identity("store-root"));
}
