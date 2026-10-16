using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.DeletionSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageStoreDeletionJournalTests
{
    private readonly PackageStoreDeletionJournalSerializer _serializer = new();

    [Fact]
    public void Manifest_ExactSubsetReportsMissingRowsButDoesNotRequireTheMarkerInCurrentObservation()
    {
        var baseline = DeletionTestData.CreateManifest();
        var markerless = DeletionTestData.CreateManifest(includeMarker: false);
        var empty = DeletionTestData.CreateManifest(entries: []);

        Assert.Equal(0, baseline.RequireExactSubset(baseline));
        Assert.Equal(1, baseline.RequireExactSubset(markerless));
        Assert.Equal(baseline.Entries.Length, baseline.RequireExactSubset(empty));
    }

    [Fact]
    public void Manifest_RejectsAddedOrReplacedSurvivingEntries()
    {
        var baseline = DeletionTestData.CreateManifest();
        var replaced = DeletionTestData.CreateManifest(entries: DeletionTestData.Entries().Select(entry =>
            entry.RelativeComponents.SequenceEqual(["payload.dat"])
                ? DeletionTestData.Entry(["payload.dat"], PhysicalStoreEntryKind.RegularFile, "replacement-file", DeletionTestData.InstallDirectory)
                : entry));
        var added = DeletionTestData.CreateManifest(entries: DeletionTestData.Entries().Append(
            DeletionTestData.Entry(["extra.dat"], PhysicalStoreEntryKind.RegularFile, "extra-file", DeletionTestData.InstallDirectory)));

        AssertUnknownAuthority(() => baseline.RequireExactSubset(replaced));
        AssertUnknownAuthority(() => baseline.RequireExactSubset(added));
    }

    [Fact]
    public void Manifest_RequiresEveryNestedEntryToBindItsExactDirectoryParent()
    {
        var rows = DeletionTestData.Entries().ToArray();
        var nestedIndex = Array.FindIndex(rows, static entry => entry.RelativeComponents.SequenceEqual(["nested", "inside.dat"]));
        rows[nestedIndex] = DeletionTestData.Entry(
            ["nested", "inside.dat"], PhysicalStoreEntryKind.RegularFile, "nested-file", DeletionTestData.InstallDirectory);

        Assert.Throws<ArgumentException>(() => DeletionTestData.CreateManifest(entries: rows));
    }

    [Fact]
    public void Manifest_OrdersPathsByOrdinalComponentsRatherThanJoinedText()
    {
        var directoryA = DeletionTestData.Entry(["a"], PhysicalStoreEntryKind.Directory, "a-directory",
            DeletionTestData.InstallDirectory, DeletionTestData.InstallSemantics, DeletionTestData.NestedSemantics);
        var directoryAHyphen = DeletionTestData.Entry(["a-"], PhysicalStoreEntryKind.Directory, "a-hyphen-directory",
            DeletionTestData.InstallDirectory, DeletionTestData.InstallSemantics, DeletionTestData.NestedSemantics);
        var childA = DeletionTestData.Entry(["a", "z"], PhysicalStoreEntryKind.RegularFile, "a-child",
            DeletionTestData.Identity("a-directory"), DeletionTestData.NestedSemantics);
        var childAHyphen = DeletionTestData.Entry(["a-", "x"], PhysicalStoreEntryKind.RegularFile, "a-hyphen-child",
            DeletionTestData.Identity("a-hyphen-directory"), DeletionTestData.NestedSemantics);

        var manifest = DeletionTestData.CreateManifest(entries: [childAHyphen, directoryAHyphen, childA, directoryA]);

        Assert.Equal(new[] { "a", "a/z", "a-", "a-/x" },
            manifest.Entries.Select(static entry => PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents)));
    }

    [Fact]
    public void Manifest_EnforcesEntryDepthAndDirectChildBounds()
    {
        var oneEntry = DeletionTestData.Entry(["entry.dat"], PhysicalStoreEntryKind.RegularFile, "entry-file",
            DeletionTestData.InstallDirectory);
        var tooManyEntries = Enumerable.Repeat(oneEntry, PhysicalStoreDeletionManifest.MaximumEntries + 1);
        var tooManyChildren = Enumerable.Range(0, PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory + 1)
            .Select(index => DeletionTestData.Entry([$"entry-{index}"], PhysicalStoreEntryKind.RegularFile,
                $"child-{index}", DeletionTestData.InstallDirectory));
        var tooDeep = Enumerable.Range(0, PhysicalStoreDeletionManifest.MaximumDepth + 1).Select(static index => $"part-{index}");

        Assert.Throws<ArgumentOutOfRangeException>(() => DeletionTestData.CreateManifest(entries: tooManyEntries));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeletionTestData.CreateManifest(entries: tooManyChildren));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicalStoreDeletionManifestEntry(
            tooDeep, PhysicalStoreEntryKind.RegularFile, DeletionTestData.Identity("deep-file"),
            DeletionTestData.InstallDirectory, DeletionTestData.InstallSemantics, directorySemantics: null));
        Assert.Throws<ArgumentException>(() => DeletionTestData.Entry([new string('é', 128)],
            PhysicalStoreEntryKind.RegularFile, "oversized-utf8", DeletionTestData.InstallDirectory));
    }

    [Fact]
    public void Journal_RequiresOneBaselineCompletionMarkerAndMatchingCompletionIdentity()
    {
        var withoutMarker = DeletionTestData.CreateManifest(includeMarker: false);
        var wrongMarkerIdentity = DeletionTestData.Identity("different-marker");

        Assert.Throws<ArgumentException>(() => DeletionTestData.CreateJournal(withoutMarker));
        Assert.Throws<ArgumentException>(() => DeletionTestData.CreateJournal(
            DeletionTestData.CreateManifest(), completionMarkerIdentity: wrongMarkerIdentity));
        Assert.Throws<ArgumentException>(() => DeletionTestData.CreateJournal(
            packageParentIdentity: DeletionTestData.StoreRootIdentity));
    }

    [Fact]
    public void RoundTrip_PreservesJournalAndManifestAndWritesDeterministicCanonicalBytes()
    {
        var original = DeletionTestData.CreateJournal();
        var reordered = DeletionTestData.CreateJournal(DeletionTestData.CreateManifest(
            entries: DeletionTestData.Entries().Reverse()));

        var payload = _serializer.Serialize(original);
        var restored = _serializer.Deserialize(payload);

        Assert.Equal(original.IntegrityDigest, restored.IntegrityDigest);
        Assert.Equal(original.StoreRoot, restored.StoreRoot);
        Assert.Equal(original.EnrollmentEpoch, restored.EnrollmentEpoch);
        Assert.Equal(original.OperationId, restored.OperationId);
        Assert.Equal(original.CandidateId, restored.CandidateId);
        Assert.Equal(original.OriginalInstall, restored.OriginalInstall);
        Assert.Equal(original.PackageParentRelativeComponents.ToArray(), restored.PackageParentRelativeComponents.ToArray());
        Assert.Equal(original.PackageParentIdentity, restored.PackageParentIdentity);
        Assert.Equal(original.PackageParentSemantics, restored.PackageParentSemantics);
        Assert.Equal(original.OriginalBasename, restored.OriginalBasename);
        Assert.Equal(original.QuarantineBasename, restored.QuarantineBasename);
        Assert.Equal(original.CompletionMarkerIdentity, restored.CompletionMarkerIdentity);
        Assert.Equal(original.KeepNewestInactiveVersionsPerPackage, restored.KeepNewestInactiveVersionsPerPackage);
        Assert.Equal(PackageStoreDeletionPhase.Prepared, restored.Phase);
        Assert.Equal(original.Manifest.Entries.Length, restored.Manifest.Entries.Length);
        for (var index = 0; index < original.Manifest.Entries.Length; index++)
        {
            var expected = original.Manifest.Entries[index];
            var actual = restored.Manifest.Entries[index];
            Assert.Equal(PhysicalStoreDeletionManifest.GetPathKey(expected.RelativeComponents),
                PhysicalStoreDeletionManifest.GetPathKey(actual.RelativeComponents));
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Identity, actual.Identity);
            Assert.Equal(expected.ParentIdentity, actual.ParentIdentity);
            Assert.Equal(expected.ParentSemantics, actual.ParentSemantics);
            Assert.Equal(expected.DirectorySemantics, actual.DirectorySemantics);
        }
        Assert.Equal(original.IntegrityDigest, reordered.IntegrityDigest);
        Assert.Equal(payload, _serializer.Serialize(reordered));
        Assert.Equal(payload, _serializer.Serialize(restored));
    }

    [Fact]
    public void Deserialize_RequiresEveryFieldIncludingNullableFieldsAndRejectsUnknownFields()
    {
        var payload = _serializer.Serialize(DeletionTestData.CreateJournal());
        var missingNullField = Parse(payload);
        missingNullField["originalInstall"]!.AsObject().Remove("verifiedArchiveHash");
        var missingEntryNullField = Parse(payload);
        missingEntryNullField["manifest"]!["entries"]![0]!.AsObject().Remove("directorySemantics");
        var missingProfileFlag = Parse(payload);
        missingProfileFlag["packageParentSemantics"]!.AsObject().Remove("caseSensitive");
        var nullProfileFlag = Parse(payload);
        nullProfileFlag["packageParentSemantics"]!["caseSensitive"] = null;
        var unknown = Parse(payload);
        unknown["manifest"]!["entries"]![0]!["unexpected"] = null;

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(missingNullField)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(missingEntryNullField)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(missingProfileFlag)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(nullProfileFlag)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(unknown)));
    }

    [Fact]
    public void Deserialize_RejectsDuplicatePropertiesAndDigestTampering()
    {
        var text = Encoding.UTF8.GetString(_serializer.Serialize(DeletionTestData.CreateJournal()));
        var duplicate = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        var duplicateNested = text.Replace("\"provider\":\"test-provider\"",
            "\"provider\":\"test-provider\",\"provider\":\"test-provider\"", StringComparison.Ordinal);
        var tampered = Parse(Encoding.UTF8.GetBytes(text));
        tampered["keepNewestInactiveVersionsPerPackage"] = 7;

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicate)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicateNested)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(tampered)));
    }

    [Fact]
    public void Digest_BindsValidJournalAndManifestMutations()
    {
        var baseline = DeletionTestData.CreateJournal();
        var changedPayloadIdentity = DeletionTestData.Identity("payload-file-updated");
        var changedEntries = DeletionTestData.Entries().Select(entry =>
            entry.RelativeComponents.SequenceEqual(["payload.dat"])
                ? new PhysicalStoreDeletionManifestEntry(
                    entry.RelativeComponents,
                    entry.Kind,
                    changedPayloadIdentity,
                    entry.ParentIdentity,
                    entry.ParentSemantics,
                    entry.DirectorySemantics)
                : entry);
        var changedInstallSemantics = DeletionTestData.Semantics("install-profile-updated");
        var changedProfileEntries = DeletionTestData.Entries().Select(entry => entry.RelativeComponents.Length == 1
            ? new PhysicalStoreDeletionManifestEntry(
                entry.RelativeComponents,
                entry.Kind,
                entry.Identity,
                entry.ParentIdentity,
                changedInstallSemantics,
                entry.DirectorySemantics)
            : entry);
        var changedProfileManifest = new PhysicalStoreDeletionManifest(
            DeletionTestData.InstallDirectory,
            changedInstallSemantics,
            changedProfileEntries);
        var changedMarkerIdentity = DeletionTestData.Identity("completion-marker-updated");
        var changedCandidateId = Guid.Parse("0bb6f2f9-6b77-4e29-9831-0316e8ad4e0d");
        var changedOperationId = Guid.Parse("e5676d59-45c8-4da5-8290-d99c601c5d26");

        Func<string?, PackageStoreDeletionJournal>[] mutations =
        [
            digest => DeletionTestData.CreateJournal(
                storeRootIdentity: DeletionTestData.Identity("store-root-updated"), expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(enrollmentEpoch: 4, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(operationId: changedOperationId, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(candidateId: changedCandidateId, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(verifiedArchiveHash: new string('b', 64), expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(feedName: "feed-b", expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(packageId: "Other.Package", expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(packageParentIdentity: DeletionTestData.Identity("package-parent-updated"), expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(
                packageParentSemantics: DeletionTestData.Semantics("package-parent-profile-updated"), expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(manifest: changedProfileManifest, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(version: "2.0.0", expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(
                manifest: DeletionTestData.CreateManifest(markerIdentity: changedMarkerIdentity),
                completionMarkerIdentity: changedMarkerIdentity,
                expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(
                keepNewestInactiveVersionsPerPackage: 3, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(phase: PackageStoreDeletionPhase.Quarantined, expectedIntegrityDigest: digest),
            digest => DeletionTestData.CreateJournal(
                manifest: DeletionTestData.CreateManifest(entries: changedEntries), expectedIntegrityDigest: digest)
        ];

        foreach (var createMutation in mutations)
        {
            var changed = createMutation(null);

            Assert.NotEqual(baseline.IntegrityDigest, changed.IntegrityDigest);
            Assert.Throws<ArgumentException>(() => createMutation(baseline.IntegrityDigest));
        }
    }

    [Fact]
    public void Deserialize_RejectsMissingMarkerBaselineInvalidPhaseAndPayloadOutsideBound()
    {
        var payload = _serializer.Serialize(DeletionTestData.CreateJournal());
        var invalidPhase = Parse(payload);
        invalidPhase["phase"] = 99;
        var badDigest = Parse(payload);
        badDigest["integrityDigest"] = new string('a', 64);

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(invalidPhase)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(badDigest)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ReadOnlyMemory<byte>.Empty));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(new byte[PackageStoreDeletionJournalSerializer.MaximumPayloadBytes + 1]));
    }

    private static void AssertUnknownAuthority(Action action)
    {
        var exception = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, exception.Reason);
    }

    private static JsonObject Parse(ReadOnlyMemory<byte> payload)
        => JsonNode.Parse(payload.Span)!.AsObject();

    private static byte[] ToBytes(JsonNode node)
        => Encoding.UTF8.GetBytes(node.ToJsonString());
}

internal static class DeletionTestData
{
    private static readonly Guid Operation = Guid.Parse("491e13d1-18c1-4f1e-ae02-1f4dedad5481");
    private static readonly Guid Candidate = Guid.Parse("a3f458d9-347b-4ef1-89c2-64ec9b5bfe32");

    internal static PhysicalFileIdentity StoreRootIdentity => Identity("store-root");
    internal static PhysicalFileIdentity PackageParentIdentity => Identity("package-parent");
    internal static PhysicalFileIdentity InstallDirectory => Identity("install-directory");
    internal static PhysicalFileIdentity MarkerIdentity => Identity("completion-marker");
    internal static PhysicalStoreNameSemantics ParentSemantics { get; } = Semantics("package-parent-profile");
    internal static PhysicalStoreNameSemantics InstallSemantics { get; } = Semantics("install-profile");
    internal static PhysicalStoreNameSemantics NestedSemantics { get; } = Semantics("nested-profile");

    internal static PhysicalFileIdentity Identity(string value)
        => new("test-provider", "test-volume", value);

    internal static PhysicalStoreNameSemantics Semantics(string profileId)
        => new(profileId, PhysicalStoreNameEncoding.Utf8, caseSensitive: true, normalizationInsensitive: false);

    internal static IEnumerable<PhysicalStoreDeletionManifestEntry> Entries(bool includeMarker = true)
        => Entries(includeMarker, markerIdentity: null);

    internal static IEnumerable<PhysicalStoreDeletionManifestEntry> Entries(
        bool includeMarker,
        PhysicalFileIdentity? markerIdentity)
    {
        if (includeMarker)
        {
            yield return new PhysicalStoreDeletionManifestEntry(
                [PackageStoreDeletionJournal.CompletionMarkerName],
                PhysicalStoreEntryKind.RegularFile,
                markerIdentity ?? MarkerIdentity,
                InstallDirectory,
                InstallSemantics,
                directorySemantics: null);
        }

        yield return Entry(["payload.dat"], PhysicalStoreEntryKind.RegularFile, "payload-file", InstallDirectory, InstallSemantics);
        yield return Entry(["nested"], PhysicalStoreEntryKind.Directory, "nested-directory", InstallDirectory,
            InstallSemantics, NestedSemantics);
        yield return Entry(["nested", "inside.dat"], PhysicalStoreEntryKind.RegularFile, "nested-file",
            Identity("nested-directory"), NestedSemantics);
    }

    internal static PhysicalStoreDeletionManifestEntry Entry(
        IEnumerable<string> path,
        PhysicalStoreEntryKind kind,
        string identity,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics? parentSemantics = null,
        PhysicalStoreNameSemantics? directorySemantics = null)
        => new(path, kind, Identity(identity), parentIdentity, parentSemantics ?? InstallSemantics, directorySemantics);

    internal static PhysicalStoreDeletionManifest CreateManifest(
        bool includeMarker = true,
        IEnumerable<PhysicalStoreDeletionManifestEntry>? entries = null,
        PhysicalFileIdentity? markerIdentity = null)
        => new(InstallDirectory, InstallSemantics, entries ?? Entries(includeMarker, markerIdentity));

    internal static PackageStoreDeletionJournal CreateJournal(
        PhysicalStoreDeletionManifest? manifest = null,
        PhysicalFileIdentity? completionMarkerIdentity = null,
        PhysicalFileIdentity? packageParentIdentity = null,
        PhysicalFileIdentity? storeRootIdentity = null,
        long enrollmentEpoch = 3,
        Guid? operationId = null,
        Guid? candidateId = null,
        string feedName = "feed-a",
        string packageId = "Sample.Package",
        string version = "1.0.0",
        string? verifiedArchiveHash = null,
        PhysicalStoreNameSemantics? packageParentSemantics = null,
        int keepNewestInactiveVersionsPerPackage = 2,
        PackageStoreDeletionPhase phase = PackageStoreDeletionPhase.Prepared,
        string? expectedIntegrityDigest = null)
    {
        var storeRoot = new PhysicalRootIdentity(storeRootIdentity ?? StoreRootIdentity);
        var markerIdentity = completionMarkerIdentity ?? MarkerIdentity;
        var install = new PackageInstallIdentity(
            storeRoot,
            packageId,
            version,
            $"{feedName}/{packageId}/{version}",
            InstallDirectory,
            ProtectionDigest.PackageInstallCompletionIdentity(markerIdentity),
            verifiedArchiveHash);
        var resolvedCandidateId = candidateId ?? Candidate;
        return new PackageStoreDeletionJournal(
            PackageStoreDeletionJournal.CurrentSchemaVersion,
            storeRoot,
            enrollmentEpoch,
            operationId ?? Operation,
            resolvedCandidateId,
            install,
            [feedName, packageId],
            packageParentIdentity ?? PackageParentIdentity,
            packageParentSemantics ?? ParentSemantics,
            version,
            PackageStoreDeletionJournal.QuarantineBasenameFor(resolvedCandidateId),
            markerIdentity,
            keepNewestInactiveVersionsPerPackage,
            phase,
            manifest ?? CreateManifest(markerIdentity: markerIdentity),
            expectedIntegrityDigest);
    }
}
