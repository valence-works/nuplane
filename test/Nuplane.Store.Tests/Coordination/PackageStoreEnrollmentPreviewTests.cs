using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageStoreEnrollmentPreviewTests
{
    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_UnenrolledRootReturnsActualLegacyStateWithoutMutation()
    {
        using var source = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        using var fixture = new PackageStoreFixture();
        var files = NativeFiles();
        var state = source.States["first"] with { ProtectionRecord = null, ProtectionBundle = null };
        var serializer = new StoreStateSerializer();
        await serializer.SaveAsync(fixture.StateFilePath, state, CancellationToken.None);
        var beforeBytes = await File.ReadAllBytesAsync(fixture.StateFilePath);
        using var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!);
        var beforeIdentity = files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity;

        var service = CreateService(files, serializer,
            new PreviewRootCatalog("default", fixture.RootPath),
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", "legacy", fixture.StateFilePath)]));
        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);

        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Unenrolled, snapshot.Membership);
        Assert.True(snapshot.IsNonQuiescentObservation);
        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
        Assert.Equal(state.ActiveVersionById, member.ActiveVersionById);
        Assert.Equal(state.LastKnownGoodById, member.LastKnownGoodById);
        Assert.Equal(state.ActivePackageDescriptorsById!.Keys.Order().ToArray(), member.ActivePackageDescriptorsById.Keys.Order().ToArray());
        Assert.Equal(state.ActiveGraphsById!.Keys.Order().ToArray(), member.ActivationHistoryById.Keys.Order().ToArray());
        Assert.Equal(state.ActivePackageDescriptorsById["Root.First"].RootPackageIds,
            member.ActivePackageDescriptorsById["Root.First"].RootPackageIds);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
        Assert.Equal("LegacyProtectionMissing", member.ActiveClosure.UnknownReason);
        Assert.Equal("RecoveryClosureUnavailable", member.RecoverableClosure.UnknownReason);
        Assert.Equal(beforeIdentity, files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity);
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(fixture.StateFilePath));
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.RootPath);
        Assert.Null(files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName));
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_MissingDeclarationsReturnsExplicitDiagnosticWithoutReading()
    {
        using var fixture = new PackageStoreFixture();
        var serializer = new CountingPayloadSerializer();
        var files = NativeFiles();
        await new StoreStateSerializer().SaveAsync(fixture.StateFilePath, StoreStateRecord.Empty(), CancellationToken.None);
        var service = CreateService(files, serializer, new PreviewRootCatalog("default", fixture.RootPath), definition: null);

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);

        Assert.Contains("TrustedMemberDeclarationsMissing", Assert.Single(snapshot.Diagnostics));
        Assert.Empty(snapshot.Members);
        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_LedgerDeclarationMismatchStopsBeforePayloadReads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var serializer = new CountingPayloadSerializer();
        var declarations = new TrustedPackageStoreMemberDeclarationDefinition(true,
            [new("default", "first", context.StatePaths["first"])]);
        var service = CreateService(context.Files, serializer,
            new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot), declarations);

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);

        Assert.True(snapshot.Membership == PackageStoreEnrollmentPreviewMembership.Incomplete,
            string.Join("; ", snapshot.Diagnostics));
        Assert.Contains("TrustedDeclarationsDoNotMatchLedgerMembersAndTargets", Assert.Single(snapshot.Diagnostics));
        Assert.Empty(snapshot.Members);
        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_CompleteV1UsesFullCatalogVerificationAndReturnsKnownClosures()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var declarations = context.StatePaths.Select(pair => new TrustedPackageStoreMemberDeclaration(
            "default", pair.Key, pair.Value)).ToArray();
        var service = CreateService(context.Files, new StoreStateSerializer(),
            new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot),
            new TrustedPackageStoreMemberDeclarationDefinition(true, declarations));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);

        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Complete, snapshot.Membership);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.All(snapshot.Members, member =>
        {
            Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, member.ActiveClosure.Knowledge);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, member.RecoverableClosure.Knowledge);
            Assert.NotNull(member.RawPayloadDigest);
            Assert.NotNull(member.StateBodyDigest);
            Assert.NotNull(member.StateFileIdentity);
        });
        Assert.Equal(context.RootIdentity, snapshot.RootIdentity);
        Assert.Empty(snapshot.Diagnostics);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_IncompleteBoundUnprotectedMembersReportMigrationAndActualState()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var declarations = context.StatePaths.Select(pair => new TrustedPackageStoreMemberDeclaration(
            "default", pair.Key, pair.Value)).ToArray();
        var service = CreateService(context.Files, new StoreStateSerializer(),
            new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot),
            new TrustedPackageStoreMemberDeclarationDefinition(true, declarations));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);

        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Incomplete, snapshot.Membership);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.All(snapshot.Members, member =>
        {
            Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
            Assert.Equal(PackageStoreEnrollmentPreviewMigration.ProtectionAcknowledgementRequired, member.Migration);
            Assert.NotNull(member.RawPayloadDigest);
            Assert.NotNull(member.StateBodyDigest);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_PendingLocalPublicationReportsRecoveryWithoutMutation()
    {
        using var context = await RootMembershipRegistryTests.Context.CreateAsync(
            RootMembershipRegistryTests.PriorBranch.Acknowledged);
        await context.InterruptAsync(RootMembershipPublicationPoint.PendingPublished);
        var candidate = context.Registry.ReadCandidate(context.Root);
        var pending = Assert.IsType<PendingStateCommit>(candidate.PendingStateCommit);
        var declared = Assert.Single(candidate.Members);
        var stateBefore = await context.ReadStateAsync();
        var stagePath = Path.Combine(context.ParentPath, RootMembershipRegistry.StageName(pending));
        var backupPath = Path.Combine(context.ParentPath, RootMembershipRegistry.BackupName(pending));
        var evidenceBefore = PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.Fixture.PackageInstallRoot], [declared.ConfiguredLocator, stagePath, backupPath]);
        var roots = new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot);
        var service = CreateService(context.Files, new StoreStateSerializer(), roots,
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", declared.MemberId, declared.ConfiguredLocator)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);

        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Incomplete, snapshot.Membership);
        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
        Assert.Equal(PackageStoreEnrollmentPreviewMigration.RecoveryRequired, member.Migration);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
        Assert.Equal("PendingPublicationRecoveryRequired", member.ActiveClosure.UnknownReason);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.RecoverableClosure.Knowledge);
        Assert.Equal(stateBefore.ActiveVersionById, member.ActiveVersionById);
        Assert.Equal(stateBefore.LastKnownGoodById, member.LastKnownGoodById);
        Assert.Contains("Pending local, group or membership migration publication requires recovery review", Assert.Single(snapshot.Diagnostics));
        Assert.Equal(evidenceBefore, PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.Fixture.PackageInstallRoot], [declared.ConfiguredLocator, stagePath, backupPath]));
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_AcknowledgedBindingRejectsSameBodyDifferentProtectionPayload()
    {
        using var context = await RootMembershipRegistryTests.Context.CreateAsync(
            RootMembershipRegistryTests.PriorBranch.Acknowledged);
        var prior = await context.ReadStateAsync();
        var declaration = Assert.Single(context.Initial.Members);
        var identity = context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity;
        await WriteKnownEmptyProtectionInPlaceAsync(declaration.ConfiguredLocator, prior,
            context.Initial.RootIdentity, revision: 2);
        Assert.Equal(identity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
        Assert.Equal(2, (await new StoreStateSerializer().LoadAsync(declaration.ConfiguredLocator,
            CancellationToken.None)).ProtectionRecord!.Revision);
        var catalog = new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot);
        var service = CreateService(context.Files, new StoreStateSerializer(), catalog,
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", declaration.MemberId, declaration.ConfiguredLocator)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);

        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
        Assert.Equal(PackageStoreEnrollmentPreviewMigration.None, member.Migration);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
        Assert.Equal("ProtectionBindingMismatch", member.ActiveClosure.UnknownReason);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.RecoverableClosure.Knowledge);
        Assert.Equal(ProtectionDigest.StateBody(prior), member.StateBodyDigest);
        Assert.Equal(prior.ActiveVersionById, member.ActiveVersionById);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_UnprotectedBindingDoesNotTrustUnacknowledgedProtectionMetadata()
    {
        using var context = await RootMembershipRegistryTests.Context.CreateAsync(
            RootMembershipRegistryTests.PriorBranch.Legacy);
        var prior = await context.ReadStateAsync();
        var declaration = Assert.Single(context.Initial.Members);
        var identity = context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity;
        await WriteKnownEmptyProtectionInPlaceAsync(declaration.ConfiguredLocator, prior,
            context.Initial.RootIdentity, revision: 1);
        Assert.Equal(identity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
        Assert.NotNull((await new StoreStateSerializer().LoadAsync(declaration.ConfiguredLocator,
            CancellationToken.None)).ProtectionRecord);
        var catalog = new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot);
        var service = CreateService(context.Files, new StoreStateSerializer(), catalog,
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", declaration.MemberId, declaration.ConfiguredLocator)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);

        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
        Assert.Equal(PackageStoreEnrollmentPreviewMigration.ProtectionAcknowledgementRequired, member.Migration);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
        Assert.Equal("ProtectionBindingMismatch", member.ActiveClosure.UnknownReason);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.RecoverableClosure.Knowledge);
        Assert.Equal(ProtectionDigest.StateBody(prior), member.StateBodyDigest);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("null", "NullPayload")]
    [InlineData("{broken", "Malformed")]
    [InlineData("<missing>", "Missing")]
    public async Task PreviewRootAsync_NullMalformedAndMissingRemainDistinct(string payload,
        string expected)
    {
        using var fixture = new PackageStoreFixture();
        var serializer = new StoreStateSerializer();
        if (payload != "<missing>")
            await File.WriteAllTextAsync(fixture.StateFilePath, payload);
        var files = NativeFiles();
        var service = CreateService(files, serializer, new PreviewRootCatalog("default", fixture.RootPath),
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", "member", fixture.StateFilePath)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);

        Assert.Equal(Enum.Parse<PackageStoreEnrollmentPreviewObservation>(expected), member.Observation);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
        Assert.Equal(expected switch
        {
            "Missing" => "StateMissing",
            "NullPayload" => "StateNull",
            _ => "StateMalformed"
        }, member.ActiveClosure.UnknownReason);
        Assert.Equal(payload == "<missing>", !File.Exists(fixture.StateFilePath));
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_LeafReplacementDuringAsyncDecodeReturnsChangedButPreservesDecodedMaps()
    {
        using var source = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        using var fixture = new PackageStoreFixture();
        var state = source.States["first"] with { ProtectionRecord = null, ProtectionBundle = null };
        var serializer = new StoreStateSerializer();
        await serializer.SaveAsync(fixture.StateFilePath, state, CancellationToken.None);
        var replacement = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Replacement.Package"] = "9.9.9"
            }
        };
        replacement.LastKnownGoodById["Replacement.Package"] = "9.9.9";
        var files = NativeFiles();
        using var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!);
        var beforeIdentity = files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity;
        var decoder = new ReplaceOnReadSerializer(fixture.StateFilePath, replacement);
        var service = CreateService(files, decoder, new PreviewRootCatalog("default", fixture.RootPath),
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", "legacy", fixture.StateFilePath)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);
        var afterIdentity = files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity;

        Assert.NotEqual(beforeIdentity, afterIdentity);
        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Changed, member.Observation);
        Assert.Equal(state.ActiveVersionById, member.ActiveVersionById);
        Assert.Equal(state.ActivePackageDescriptorsById!.Keys.Order().ToArray(),
            member.ActivePackageDescriptorsById.Keys.Order().ToArray());
        Assert.Equal("StateChangedDuringObservation", member.ActiveClosure.UnknownReason);
        Assert.NotNull(member.RawPayloadDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_InPlacePayloadRewriteDuringAsyncDecodeReturnsChangedWithStableIdentity()
    {
        using var source = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        using var fixture = new PackageStoreFixture();
        var state = source.States["first"] with { ProtectionRecord = null, ProtectionBundle = null };
        await new StoreStateSerializer().SaveAsync(fixture.StateFilePath, state, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(fixture.StateFilePath);
        var files = NativeFiles();
        using var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!);
        var beforeIdentity = files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity;
        var decoder = new RewriteInPlaceOnReadSerializer(fixture.StateFilePath);
        var service = CreateService(files, decoder, new PreviewRootCatalog("default", fixture.RootPath),
            new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", "legacy", fixture.StateFilePath)]));

        var snapshot = await service.PreviewRootAsync("default", CancellationToken.None);
        var member = Assert.Single(snapshot.Members);
        var afterBytes = await File.ReadAllBytesAsync(fixture.StateFilePath);
        var afterIdentity = files.InspectChildNoFollow(parent, Path.GetFileName(fixture.StateFilePath))!.Identity;

        Assert.Equal(beforeIdentity, afterIdentity);
        Assert.Equal(originalBytes.Length, afterBytes.Length);
        Assert.NotEqual(originalBytes, afterBytes);
        Assert.Equal(PackageStoreEnrollmentPreviewObservation.Changed, member.Observation);
        Assert.Equal(state.ActiveVersionById, member.ActiveVersionById);
        Assert.Equal(state.LastKnownGoodById, member.LastKnownGoodById);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant(), member.RawPayloadDigest);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(afterBytes)).ToLowerInvariant(), member.RawPayloadDigest);
        Assert.Equal("StateChangedDuringObservation", member.ActiveClosure.UnknownReason);
    }

    [Fact]
    public async Task PreviewRootAsync_CancellationIsPropagatedBeforeObservation()
    {
        using var fixture = new PackageStoreFixture();
        var service = CreateService(NativeFiles(), new StoreStateSerializer(),
            new PreviewRootCatalog("default", fixture.RootPath), new TrustedPackageStoreMemberDeclarationDefinition(true,
                [new("default", "member", fixture.StateFilePath)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreviewRootAsync("default", cancellation.Token));
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_CancellationDuringCompleteDecodeReleasesCatalogOwnership()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var declarations = context.StatePaths.Select(pair => new TrustedPackageStoreMemberDeclaration(
            "default", pair.Key, pair.Value)).ToArray();
        var definition = new TrustedPackageStoreMemberDeclarationDefinition(true, declarations);
        var roots = new PreviewRootCatalog("default", context.Fixture.PackageInstallRoot);
        using var cancellation = new CancellationTokenSource();
        var interrupted = CreateService(context.Files, new CancelOnReadSerializer(cancellation), roots, definition);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.PreviewRootAsync("default", cancellation.Token));

        var retry = CreateService(context.Files, new StoreStateSerializer(), roots, definition);
        var snapshot = await retry.PreviewRootAsync("default", CancellationToken.None);
        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Complete, snapshot.Membership);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void DeclarationCatalog_FreezesMutableInterfaceDefinitionOnConstruction()
    {
        var first = new TrustedPackageStoreMemberDeclaration("default", "first", Path.GetFullPath("first.json"));
        var mutable = new MutableDefinition(true, [first]);
        var catalog = new TrustedPackageStoreMemberDeclarationCatalog(mutable);
        mutable.Replace(0, new TrustedPackageStoreMemberDeclaration("default", "changed",
            Path.GetFullPath("changed.json")));
        mutable.IsComplete = false;

        Assert.True(catalog.IsComplete);
        Assert.Equal("first", Assert.Single(catalog.ForRoot("default")).MemberId);
    }

    private static PackageStoreEnrollmentPreviewService CreateService(
        IPhysicalStoreFileSystem files,
        IStoreStateSerializer serializer,
        ITrustedPackageStoreRootCatalog roots,
        TrustedPackageStoreMemberDeclarationDefinition? definition)
    {
        var resolver = new PackageStoreRootResolver(roots, Options.Create(new PackageStoreMaintenanceOptions()));
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        return new PackageStoreEnrollmentPreviewService(files, registry, roots, resolver,
            new TrustedPackageStoreMemberDeclarationCatalog(definition), serializer);
    }

    private static IPhysicalStoreFileSystem NativeFiles()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static async Task WriteKnownEmptyProtectionInPlaceAsync(string path, StoreStateRecord prior,
        PhysicalRootIdentity rootIdentity, long revision)
    {
        var body = prior with { ProtectionRecord = null, ProtectionBundle = null };
        var active = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var recoverable = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(1, rootIdentity, 1, "member", revision,
            ProtectionDigest.StateBody(body), new string('0', 64), active, recoverable, [], false);
        var protection = new PackageProtectionRecord(1, rootIdentity, 1, "member", revision,
            candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), active, recoverable, [], false);
        var state = body with { ProtectionRecord = protection };
        using var payload = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None);
        await File.WriteAllBytesAsync(path, payload.ToArray());
    }

    private sealed class PreviewRootCatalog(string label, string path) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; } = Array.AsReadOnly([new TrustedPackageStoreRoot(label, path)]);
    }

    private sealed class MutableDefinition(bool complete, List<TrustedPackageStoreMemberDeclaration> declarations)
        : ITrustedPackageStoreMemberDeclarationDefinition
    {
        public bool IsComplete { get; set; } = complete;
        public IReadOnlyList<TrustedPackageStoreMemberDeclaration> Declarations => declarations;
        internal void Replace(int index, TrustedPackageStoreMemberDeclaration declaration) => declarations[index] = declaration;
    }

    private sealed class CountingPayloadSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        internal int ReadCalls { get; private set; }

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class ReplaceOnReadSerializer(string statePath, StoreStateRecord replacement)
        : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private bool _replaced;

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            var state = await _inner.ReadPayloadAsync(payload, cancellationToken);
            if (!_replaced)
            {
                _replaced = true;
                var temporary = statePath + ".preview-replacement";
                await _inner.SaveAsync(temporary, replacement, cancellationToken);
                File.Move(temporary, statePath, overwrite: true);
            }
            return state;
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class RewriteInPlaceOnReadSerializer(string statePath) : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private bool _rewritten;

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            var state = await _inner.ReadPayloadAsync(payload, cancellationToken);
            if (!_rewritten)
            {
                _rewritten = true;
                var bytes = await File.ReadAllBytesAsync(statePath, cancellationToken);
                var key = state.ActiveVersionById.Keys.First();
                var marker = Encoding.UTF8.GetBytes($"\"{key}\"");
                var offset = bytes.AsSpan().IndexOf(marker);
                if (offset < 0)
                    throw new InvalidOperationException("The fixture active package key was not found in the state payload.");
                bytes[offset + 1] = bytes[offset + 1] == (byte)'Z' ? (byte)'Y' : (byte)'Z';
                await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.Asynchronous);
                stream.SetLength(0);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return state;
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class CancelOnReadSerializer(CancellationTokenSource cancellation)
        : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private bool _canceled;

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            var state = await _inner.ReadPayloadAsync(payload, cancellationToken);
            if (!_canceled)
            {
                _canceled = true;
                cancellation.Cancel();
            }
            return state;
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }
}
