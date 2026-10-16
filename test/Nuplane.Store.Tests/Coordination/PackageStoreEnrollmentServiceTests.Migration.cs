using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class PackageStoreEnrollmentServiceTests
{
    [SupportedPhysicalStoreFact]
    public async Task RecoverEnrollmentAsync_AcceptsSourceBoundHistoricalGraphEvidence()
    {
        using var fixture = await LegacyActiveGraphCandidateProducerTests.CandidateFixture.CreateAsync();
        var evidence = await CreateHistoricalEvidenceAsync(fixture);
        var declarations = SingleMemberDeclarations(fixture.StatePath);
        var service = CreateService(fixture.Files, fixture.Serializer, fixture.Fixture.PackageInstallRoot, declarations);
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var request = new PackageStoreEnrollmentOperationRequest("default", fixture.RootIdentity,
            ledger.EnrollmentEpoch, true, ["legacy-member"], ["legacy-member"], recoveryEvidence: [evidence]);

        var result = await service.RecoverEnrollmentAsync(request, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, result.Status);
        Assert.Equal(PackageStoreEnrollmentMemberDisposition.Enrolled, Assert.Single(result.Members).Disposition);
        var protectedState = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        Assert.True(Assert.Single(evidence.ActiveGraphs).HasSamePayloadAs(
            Assert.Single(protectedState.ProtectionRecord!.ActiveClosure.Graphs!)));
        Assert.True(Assert.Single(evidence.RecoverableGraphs).HasSamePayloadAs(
            Assert.Single(protectedState.ProtectionRecord.RecoverableClosure.Graphs!)));
    }

    [SupportedPhysicalStoreFact]
    public async Task EnrollAsync_OrdinarySuccessfulLegacyUpdateDoesNotVerifyUnknownRecovery()
    {
        using var fixture = await LegacyActiveGraphCandidateProducerTests.CandidateFixture.CreateAsync(
            "extra-lkg", initializeMembership: false);
        var ordinaryRegistry = new StoreRegistry(fixture.Serializer, fixture.StatePath);
        await ordinaryRegistry.PersistActiveVersionsAsync(fixture.State.ActiveVersionById,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Root.First"] = "1.0.0"
            }, "ordinary-success", CancellationToken.None);

        var updated = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        Assert.True(updated.UpdatedAt > fixture.State.UpdatedAt);
        Assert.Equal("9.9.9", updated.LastKnownGoodById["Legacy.Recovery"]);
        Assert.Null(updated.ProtectionRecord);
        var updatedBytes = File.ReadAllBytes(fixture.StatePath);
        var service = CreateService(fixture.Files, fixture.Serializer, fixture.Fixture.PackageInstallRoot,
            SingleMemberDeclarations(fixture.StatePath));
        var request = new PackageStoreEnrollmentOperationRequest("default", fixture.RootIdentity, 1,
            true, ["legacy-member"], []);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.EnrollAsync(request, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Equal(updatedBytes, File.ReadAllBytes(fixture.StatePath));
        var unchanged = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        Assert.Equal("9.9.9", unchanged.LastKnownGoodById["Legacy.Recovery"]);
        Assert.Null(unchanged.ProtectionRecord);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverEnrollmentAsync_RetiresLegacyLkgWithDurableSourceBoundEvidence()
    {
        using var fixture = await LegacyActiveGraphCandidateProducerTests.CandidateFixture.CreateAsync("extra-lkg");
        var evidence = await CreateHistoricalEvidenceAsync(fixture);
        var sourceState = fixture.State;
        Assert.Equal("9.9.9", sourceState.LastKnownGoodById["Legacy.Recovery"]);
        var service = CreateService(fixture.Files, fixture.Serializer, fixture.Fixture.PackageInstallRoot,
            SingleMemberDeclarations(fixture.StatePath));
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var request = new PackageStoreEnrollmentOperationRequest("default", fixture.RootIdentity,
            ledger.EnrollmentEpoch, true, ["legacy-member"], ["legacy-member"],
            recoveryEvidence: [evidence], retireLegacyRecoveryMemberIds: ["legacy-member"]);

        await service.RecoverEnrollmentAsync(request, CancellationToken.None);

        var protectedState = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        var protection = Assert.IsType<PackageProtectionRecord>(protectedState.ProtectionRecord);
        var retirement = Assert.IsType<LegacyRecoveryRetirementEvidence>(protection.LegacyRecoveryRetirementEvidence);
        Assert.False(protection.LegacyUnknownRecovery);
        Assert.False(protectedState.LastKnownGoodById.ContainsKey("Legacy.Recovery"));
        Assert.Equal(ProtectionDigest.StateSlotIdentityDigest(evidence.ExpectedStateSlot), retirement.SourceStateSlotDigest);
        Assert.Equal(ProtectionDigest.PhysicalFileIdentityDigest(evidence.ExpectedStateFileIdentity), retirement.SourceStateFileIdentityDigest);
        Assert.Equal(evidence.ExpectedStateBodyDigest, retirement.SourceStateBodyDigest);
        Assert.Equal(ProtectionDigest.ActiveGraphClosureDigest(evidence.ActiveGraphs), retirement.ActiveClosureDigest);
        Assert.Equal("9.9.9", retirement.RetiredLastKnownGood["Legacy.Recovery"]);
        Assert.True(Assert.Single(evidence.RecoverableGraphs).HasSamePayloadAs(
            Assert.Single(protection.RecoverableClosure.Graphs!)));
        Assert.Equal(ledger.EnrollmentEpoch, retirement.RetiringEpoch);
        Assert.Equal(1, retirement.RetiringRevision);
        _ = PersistedStoreStateGraphVerifier.Verify(protectedState);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("slot")]
    [InlineData("file")]
    [InlineData("body")]
    public async Task RecoverEnrollmentAsync_RefusesHistoricalEvidenceForAnotherSource(string mismatch)
    {
        using var fixture = await LegacyActiveGraphCandidateProducerTests.CandidateFixture.CreateAsync();
        var evidence = await CreateHistoricalEvidenceAsync(fixture);
        evidence = mismatch switch
        {
            "slot" => CopyEvidence(evidence, expectedSlot: new StateSlotIdentity(
                evidence.ExpectedStateSlot.ParentIdentity, evidence.ExpectedStateSlot.NameSemantics,
                evidence.ExpectedStateSlot.CanonicalBasename + ".other")),
            "file" => CopyEvidence(evidence, expectedFileIdentity: await CreateOtherStateFileIdentityAsync(fixture)),
            "body" => CopyEvidence(evidence, expectedStateBodyDigest: ProtectionDigest.StateBody(StoreStateRecord.Empty())),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, null)
        };
        var declarations = SingleMemberDeclarations(fixture.StatePath);
        var service = CreateService(fixture.Files, fixture.Serializer, fixture.Fixture.PackageInstallRoot, declarations);
        var beforeState = File.ReadAllBytes(fixture.StatePath);
        var beforeLedger = fixture.Registry.ReadCandidate(fixture.Root);
        var request = new PackageStoreEnrollmentOperationRequest("default", fixture.RootIdentity,
            beforeLedger.EnrollmentEpoch, true, ["legacy-member"], ["legacy-member"], recoveryEvidence: [evidence]);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.RecoverEnrollmentAsync(request, CancellationToken.None));

        Assert.Equal(beforeState, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(beforeLedger.LedgerDigest, fixture.Registry.ReadCandidate(fixture.Root).LedgerDigest);
        Assert.Null((await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None)).ProtectionRecord);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverEnrollmentAsync_RefusesHistoricalEvidenceAfterNativeInstallReplacement()
    {
        using var fixture = await LegacyActiveGraphCandidateProducerTests.CandidateFixture.CreateAsync();
        var evidence = await CreateHistoricalEvidenceAsync(fixture);
        var declarations = SingleMemberDeclarations(fixture.StatePath);
        var service = CreateService(fixture.Files, fixture.Serializer, fixture.Fixture.PackageInstallRoot, declarations);
        var beforeLedger = fixture.Registry.ReadCandidate(fixture.Root);
        var beforeInstall = fixture.CaptureInstallIdentities()["Root.First"];
        ReplaceInstallDirectory(fixture.InstallPaths["Root.First"]);
        Assert.NotEqual(beforeInstall.DirectoryIdentity, fixture.CaptureInstallIdentities()["Root.First"].DirectoryIdentity);
        var request = new PackageStoreEnrollmentOperationRequest("default", fixture.RootIdentity,
            beforeLedger.EnrollmentEpoch, true, ["legacy-member"], ["legacy-member"], recoveryEvidence: [evidence]);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.RecoverEnrollmentAsync(request, CancellationToken.None));

        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Null(ledger.PendingStateCommit);
        Assert.Equal(beforeLedger.LedgerDigest, ledger.LedgerDigest);
        var unchangedState = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        Assert.Equal(evidence.ExpectedStateBodyDigest, ProtectionDigest.StateBody(unchangedState));
        Assert.Null(unchangedState.ProtectionRecord);
    }

    [SupportedPhysicalStoreFact]
    public async Task MigrateAsync_RequiresExplicitRetirementBeforePublishingTargetStates()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var targets = PrepareMigrationTargets(context);
        var service = CreateMigrationService(context, new StoreStateSerializer(), targets.Declarations);
        var oldLedger = context.Registry.ReadCandidate(context.Root);
        var beforeBytes = CaptureMemberBytes(context);
        var request = MigrationRequest(context, oldLedger.EnrollmentEpoch,
            ["first", "third"], ["first", "second"]);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.MigrateAsync(request, CancellationToken.None));

        var after = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(oldLedger.LedgerDigest, after.LedgerDigest);
        Assert.Equal(RootMembershipStatus.Complete, after.Status);
        AssertMemberBytesEqual(beforeBytes, CaptureMemberBytes(context));
        Assert.False(File.Exists(targets.FirstPath));
        Assert.False(File.Exists(targets.ThirdPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task MigrateAsync_MovesAddsAndRetiresMembersWhilePreservingOldStateFiles()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var targets = PrepareMigrationTargets(context);
        var service = CreateMigrationService(context, new StoreStateSerializer(), targets.Declarations);
        var beforeBytes = CaptureMemberBytes(context);
        var request = MigrationRequest(context, 1, ["first", "third"], ["first", "second"], ["second"]);

        var result = await service.MigrateAsync(request, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, result.Status);
        Assert.Equal(2, result.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "third" }, result.Members.Where(static member =>
            member.Disposition != PackageStoreEnrollmentMemberDisposition.Retired)
            .Select(static member => member.MemberId).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(PackageStoreEnrollmentMemberDisposition.Retired,
            Assert.Single(result.Members, static member => member.MemberId == "second").Disposition);
        AssertMemberBytesEqual(beforeBytes, CaptureMemberBytes(context));

        var migrated = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(RootMembershipStatus.Complete, migrated.Status);
        Assert.Equal(2, migrated.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "third" }, migrated.TargetMemberIds.OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(new[] { "first", "third" }, migrated.Members.Select(static member => member.MemberId)
            .OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal("second", Assert.Single(migrated.RetiredMembers).MemberId);
        var moved = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
            migrated.Members.Single(static member => member.MemberId == "first").Binding);
        Assert.Equal(Path.GetFileName(targets.FirstPath), moved.StateSlot.CanonicalBasename);
        Assert.Equal(2, (await new StoreStateSerializer().LoadAsync(targets.FirstPath, CancellationToken.None))
            .ProtectionRecord!.EnrollmentEpoch);
        Assert.Equal(2, (await new StoreStateSerializer().LoadAsync(targets.ThirdPath, CancellationToken.None))
            .ProtectionRecord!.EnrollmentEpoch);
        Assert.True(File.Exists(context.StatePaths["first"]));
        Assert.True(File.Exists(context.StatePaths["second"]));
    }

    [SupportedPhysicalStoreFact]
    public async Task MigrateAsync_SecondMigrationRebasesInheritedRetirementAndPreservesPriorSources()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var firstTargets = PrepareMigrationTargets(context);
        var serializer = new StoreStateSerializer();
        var firstMigration = await CreateMigrationService(context, serializer, firstTargets.Declarations)
            .MigrateAsync(MigrationRequest(context, 1, ["first", "third"], ["first", "second"], ["second"]),
                CancellationToken.None);
        Assert.Equal(2, firstMigration.EnrollmentEpoch);
        var afterFirst = context.Registry.ReadCandidate(context.Root);
        var inheritedBefore = Assert.Single(afterFirst.RetiredMembers);
        Assert.Equal("second", inheritedBefore.MemberId);
        var originalSourceBytes = CaptureMemberBytes(context);
        var firstMigrationSourceBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["first"] = File.ReadAllBytes(firstTargets.FirstPath),
            ["third"] = File.ReadAllBytes(firstTargets.ThirdPath)
        };
        var secondTargets = PrepareSecondMigrationTargets(context);

        var secondMigration = await CreateMigrationService(context, serializer, secondTargets.Declarations)
            .MigrateAsync(MigrationRequest(context, 2, ["first", "fourth"], ["first", "third"], ["third"]),
                CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, secondMigration.Status);
        Assert.Equal(3, secondMigration.EnrollmentEpoch);
        var completed = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(RootMembershipStatus.Complete, completed.Status);
        Assert.Equal(3, completed.EnrollmentEpoch);
        Assert.Equal(new[] { "first", "fourth" }, completed.Members.Select(static member => member.MemberId)
            .Order(StringComparer.Ordinal));
        var inheritedAfter = Assert.Single(completed.RetiredMembers,
            static retirement => retirement.MemberId == "second");
        var newlyRetired = Assert.Single(completed.RetiredMembers,
            static retirement => retirement.MemberId == "third");
        Assert.Equal(3, inheritedAfter.RetiringEpoch);
        Assert.Equal(3, newlyRetired.RetiringEpoch);
        Assert.NotEqual(inheritedBefore.ProofDigest, inheritedAfter.ProofDigest);
        Assert.Equal(inheritedBefore.PriorBinding.StateSlot, inheritedAfter.PriorBinding.StateSlot);
        Assert.Equal(Path.GetFileName(firstTargets.ThirdPath), newlyRetired.PriorBinding.StateSlot.CanonicalBasename);
        AssertMemberBytesEqual(originalSourceBytes, CaptureMemberBytes(context));
        Assert.Equal(firstMigrationSourceBytes["first"], File.ReadAllBytes(firstTargets.FirstPath));
        Assert.Equal(firstMigrationSourceBytes["third"], File.ReadAllBytes(firstTargets.ThirdPath));
        Assert.True(File.Exists(secondTargets.FirstPath));
        Assert.True(File.Exists(secondTargets.FourthPath));

        var migratedFirst = await serializer.LoadAsync(secondTargets.FirstPath, CancellationToken.None);
        Assert.Equal(3, migratedFirst.ProtectionRecord!.EnrollmentEpoch);
        var verified = PersistedStoreStateGraphVerifier.Verify(migratedFirst);
        Assert.NotEmpty(verified.ActiveGraphs);
        Assert.NotEmpty(verified.RecoverableGraphs);
        Assert.Equal(3, (await serializer.LoadAsync(secondTargets.FourthPath, CancellationToken.None))
            .ProtectionRecord!.EnrollmentEpoch);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverEnrollmentAsync_ResumesInterruptedRosterMigrationAfterPartialMemberPublication()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var targets = PrepareMigrationTargets(context);
        var serializer = new InterruptingMemberStateSerializer("third", expectedEpoch: 2);
        var service = CreateMigrationService(context, serializer, targets.Declarations);
        var priorBytes = CaptureMemberBytes(context);
        var request = MigrationRequest(context, 1, ["first", "third"], ["first", "second"], ["second"]);

        await Assert.ThrowsAsync<IOException>(() => service.MigrateAsync(request, CancellationToken.None));

        var interrupted = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(RootMembershipStatus.Incomplete, interrupted.Status);
        Assert.NotNull(interrupted.PendingMembershipMigration);
        Assert.Null(interrupted.PendingStateCommit);
        AssertMemberBytesEqual(priorBytes, CaptureMemberBytes(context));
        Assert.True(File.Exists(targets.FirstPath));
        Assert.False(File.Exists(targets.ThirdPath));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.RecoverEnrollmentAsync(
            MigrationRequest(context, 1, ["first", "third"], ["first", "second", "third"], ["second"]),
            CancellationToken.None));
        Assert.Equal(interrupted.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);

        var recovered = await service.RecoverEnrollmentAsync(
            MigrationRequest(context, interrupted.EnrollmentEpoch, ["first", "third"],
                interrupted.Members.Select(static member => member.MemberId), ["second"]), CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, recovered.Status);
        Assert.Equal(2, recovered.EnrollmentEpoch);
        AssertMemberBytesEqual(priorBytes, CaptureMemberBytes(context));
        var final = context.Registry.ReadCandidate(context.Root);
        Assert.Null(final.PendingMembershipMigration);
        Assert.Null(final.PendingStateCommit);
        Assert.Equal(new[] { "first", "third" }, final.Members.Select(static member => member.MemberId)
            .OrderBy(static id => id, StringComparer.Ordinal));
        Assert.True(File.Exists(targets.FirstPath));
        Assert.True(File.Exists(targets.ThirdPath));
    }

    private static async Task<PackageStoreMemberRecoveryEvidence> CreateHistoricalEvidenceAsync(
        LegacyActiveGraphCandidateProducerTests.CandidateFixture fixture)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var member = ledger.Members.Single();
        var binding = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(member.Binding);
        var fileIdentity = fixture.CaptureStateFileIdentity();
        return await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, ledger.EnrollmentEpoch, true,
            async (locked, token) =>
            {
                using var reconstructed = await LegacyActiveGraphCandidateProducer.CreateAsync(
                    fixture.Files, fixture.Registry, fixture.Serializer, locked, member.MemberId, fixture.Feeds, token);
                Assert.True(reconstructed.IsVerified, reconstructed.UnknownReasonCode);
                var protection = Assert.IsType<PackageProtectionRecord>(reconstructed.Candidate!.ProtectionRecord);
                ProtectedGraphSnapshot BindHistoricalGeneration(ProtectedGraphSnapshot graph)
                {
                    var activation = fixture.State.ActiveGraphsByIdNormalized.Values.SingleOrDefault(record =>
                        string.Equals(record.GraphId, graph.GraphId, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("Reconstructed graph has no exact persisted historical activation record.");
                    return new ProtectedGraphSnapshot(graph.SnapshotId, activation.GraphId, activation.GenerationId,
                        graph.Disposition, graph.Roots, graph.RequestedRoots, graph.Nodes, graph.Edges,
                        graph.RecoverySelectionEvidence);
                }

                return new PackageStoreMemberRecoveryEvidence(member.MemberId, binding.StateSlot, fileIdentity,
                    ProtectionDigest.StateBody(fixture.State), protection.ActiveClosure.Graphs!.Select(BindHistoricalGeneration),
                    protection.RecoverableClosure.Graphs!.Select(BindHistoricalGeneration));
            }, CancellationToken.None).ConfigureAwait(false);
    }

    private static PackageStoreMemberRecoveryEvidence CopyEvidence(
        PackageStoreMemberRecoveryEvidence evidence,
        StateSlotIdentity? expectedSlot = null,
        PhysicalFileIdentity? expectedFileIdentity = null,
        string? expectedStateBodyDigest = null)
        => new(evidence.MemberId, expectedSlot ?? evidence.ExpectedStateSlot,
            expectedFileIdentity ?? evidence.ExpectedStateFileIdentity,
            expectedStateBodyDigest ?? evidence.ExpectedStateBodyDigest,
            evidence.ActiveGraphs, evidence.RecoverableGraphs);

    private static async Task<PhysicalFileIdentity> CreateOtherStateFileIdentityAsync(
        LegacyActiveGraphCandidateProducerTests.CandidateFixture fixture)
    {
        var path = fixture.Fixture.CreateStateSlot("state/other-source.json");
        await fixture.Serializer.SaveAsync(path, StoreStateRecord.Empty(), CancellationToken.None);
        using var parent = PhysicalStoreTestDirectory.Open(fixture.Files, Path.GetDirectoryName(path)!);
        return fixture.Files.InspectChildNoFollow(parent, Path.GetFileName(path))!.Identity;
    }

    private static TrustedPackageStoreMemberDeclarationDefinition SingleMemberDeclarations(string path)
        => new(true, [new TrustedPackageStoreMemberDeclaration("default", "legacy-member", path)]);

    private static (TrustedPackageStoreMemberDeclarationDefinition Declarations, string FirstPath, string ThirdPath)
        PrepareMigrationTargets(RootMembershipProtectionVerificationTests.Context context)
    {
        var firstPath = context.Fixture.CreateStateSlot("relocated/first.json");
        var thirdPath = context.Fixture.CreateStateSlot("new/third.json");
        var declarations = new TrustedPackageStoreMemberDeclarationDefinition(true,
        [
            new TrustedPackageStoreMemberDeclaration("default", "first", firstPath),
            new TrustedPackageStoreMemberDeclaration("default", "third", thirdPath)
        ]);
        return (declarations, firstPath, thirdPath);
    }

    private static (TrustedPackageStoreMemberDeclarationDefinition Declarations, string FirstPath, string FourthPath)
        PrepareSecondMigrationTargets(RootMembershipProtectionVerificationTests.Context context)
    {
        var firstPath = context.Fixture.CreateStateSlot("relocated/second-first.json");
        var fourthPath = context.Fixture.CreateStateSlot("new/fourth.json");
        var declarations = new TrustedPackageStoreMemberDeclarationDefinition(true,
        [
            new TrustedPackageStoreMemberDeclaration("default", "first", firstPath),
            new TrustedPackageStoreMemberDeclaration("default", "fourth", fourthPath)
        ]);
        return (declarations, firstPath, fourthPath);
    }

    private static PackageStoreEnrollmentService CreateMigrationService(
        RootMembershipProtectionVerificationTests.Context context,
        IStoreStateSerializer serializer,
        TrustedPackageStoreMemberDeclarationDefinition declarations)
        => CreateService(context.Files, serializer, context.Fixture.PackageInstallRoot, declarations);

    private static PackageStoreEnrollmentOperationRequest MigrationRequest(
        RootMembershipProtectionVerificationTests.Context context,
        long expectedEpoch,
        IEnumerable<string> intended,
        IEnumerable<string> current,
        IEnumerable<string>? retire = null)
        => new("default", context.RootIdentity, expectedEpoch, true, intended, current, retire);

    private static Dictionary<string, byte[]> CaptureMemberBytes(
        RootMembershipProtectionVerificationTests.Context context)
        => context.StatePaths.ToDictionary(static pair => pair.Key,
            static pair => File.ReadAllBytes(pair.Value), StringComparer.Ordinal);

    private static void AssertMemberBytesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (memberId, bytes) in expected)
            Assert.Equal(bytes, actual[memberId]);
    }

    private static void ReplaceInstallDirectory(string installPath)
    {
        var displaced = installPath + ".displaced-" + Guid.NewGuid().ToString("N");
        Directory.Move(installPath, displaced);
        Directory.CreateDirectory(installPath);
        foreach (var file in Directory.EnumerateFiles(displaced, "*", SearchOption.AllDirectories))
        {
            var replacement = Path.Combine(installPath, Path.GetRelativePath(displaced, file));
            Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
            File.Copy(file, replacement);
        }
    }

}
