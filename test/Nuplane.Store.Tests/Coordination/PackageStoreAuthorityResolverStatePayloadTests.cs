using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class PackageStoreAuthorityResolverTests
{
    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_DirectAndHardLinkedFilesAreDescriptiveWithoutOpeningPayload()
    {
        using var context = new Context();
        var payload = Path.Combine(context.Fixture.PackageInstallRoot, "state-payload.json");
        var hardLink = Path.Combine(context.Fixture.PackageInstallRoot, "state-payload-hardlink.json");
        File.WriteAllBytes(payload, "state-payload"u8.ToArray());
        CreateHardLink(payload, hardLink);

        context.Observed.ResetForCall();
        using (var direct = context.Resolver.ResolveStatePayloadReferentMetadata(payload))
        {
            Assert.Equal(Path.GetFileName(payload), direct.RequestedSlot.Name);
            Assert.Equal(Path.GetFileName(payload), direct.FinalPayloadSlot.Name);
            Assert.Empty(direct.TerminalLinkSlots);
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, direct.FinalPayloadSlot.Entry?.Kind);
            Assert.Equal(2UL, direct.FinalPayloadSlot.Entry?.LinkCount);
            Assert.Null(direct.AuthorityRoot);
            Assert.Null(direct.RootIdentity);
            Assert.Null(direct.MembershipCandidate);
            direct.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        context.Observed.ResetForCall();
        using (var linked = context.Resolver.ResolveStatePayloadReferentMetadata(hardLink))
        {
            Assert.NotEqual(Path.GetFileName(payload), linked.FinalPayloadSlot.Name);
            Assert.Equal(2UL, linked.FinalPayloadSlot.Entry?.LinkCount);
            linked.Revalidate();
        }

        Assert.Equal(0, context.Observed.FileOpenCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.CreateCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_ExistingRegularLeafIdentityIsNotPinnedByParentReplay()
    {
        using var context = new Context();
        var payload = Path.Combine(context.Fixture.PackageInstallRoot, "replaceable-state-payload.json");
        var replacement = payload + ".replacement";
        File.WriteAllBytes(payload, "old-state"u8.ToArray());
        context.Observed.ResetForCall();

        using (var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(payload))
        {
            var descriptiveIdentity = resolution.FinalPayloadSlot.Entry!.Identity;
            File.WriteAllBytes(replacement, "new-state"u8.ToArray());
            File.Move(replacement, payload, overwrite: true);

            resolution.Revalidate();
            var current = context.NativeFiles.InspectChildNoFollow(resolution.FinalParent,
                resolution.FinalPayloadSlot.Name);
            Assert.NotNull(current);
            Assert.NotEqual(descriptiveIdentity, current.Identity);
        }

        Assert.Equal(0, context.Observed.FileOpenCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_RelativeAndAbsoluteTerminalChainsExcludeParentAliases()
    {
        using var context = new Context();
        var packageRoot = context.Fixture.PackageInstallRoot;
        var referentDirectory = Directory.CreateDirectory(Path.Combine(packageRoot, "referent-directory")).FullName;
        var payload = Path.Combine(referentDirectory, "state-payload.json");
        File.WriteAllBytes(payload, "payload"u8.ToArray());
        var middle = Path.Combine(packageRoot, "middle-state-link");
        var outer = Path.Combine(packageRoot, "outer-state-link");
        File.CreateSymbolicLink(middle, Path.Combine("referent-directory", "state-payload.json"));
        File.CreateSymbolicLink(outer, "middle-state-link");

        context.Observed.ResetForCall();
        using (var chain = context.Resolver.ResolveStatePayloadReferentMetadata(outer))
        {
            Assert.Equal(new[] { "outer-state-link", "middle-state-link" },
                chain.TerminalLinkSlots.Select(static slot => slot.Name));
            Assert.Equal("middle-state-link", chain.RequestedSlot.LinkTargetText);
            Assert.Equal(Path.GetFileName(payload), chain.FinalPayloadSlot.Name);
            chain.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        var absolute = Path.Combine(packageRoot, "absolute-state-link");
        File.CreateSymbolicLink(absolute, payload);
        context.Observed.ResetForCall();
        using (var absoluteChain = context.Resolver.ResolveStatePayloadReferentMetadata(absolute))
        {
            var linkSlot = Assert.Single(absoluteChain.TerminalLinkSlots);
            Assert.False(string.IsNullOrEmpty(linkSlot.LinkTargetText));
            Assert.Equal(Path.GetFileName(payload), absoluteChain.FinalPayloadSlot.Name);
            absoluteChain.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        var parentAlias = Path.Combine(context.Fixture.RootPath, "state-parent-directory-link");
        Directory.CreateSymbolicLink(parentAlias, "packages");
        context.Observed.ResetForCall();
        using (var parentAliasPath = context.Resolver.ResolveStatePayloadReferentMetadata(
                   Path.Combine(parentAlias, "referent-directory", "state-payload.json")))
        {
            Assert.Empty(parentAliasPath.TerminalLinkSlots);
            Assert.Equal(Path.GetFileName(payload), parentAliasPath.RequestedSlot.Name);
            parentAliasPath.Revalidate();
        }

        Assert.Equal(0, context.Observed.FileOpenCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_AbsentLeafAndDanglingChainReplayAbsenceWithoutCreatingParents()
    {
        using var context = new Context();
        var absent = Path.Combine(context.Fixture.PackageInstallRoot, "absent-state-payload.json");
        context.Observed.ResetForCall();
        using (var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(absent))
        {
            Assert.Null(resolution.FinalPayloadSlot.Entry);
            Assert.False(Directory.Exists(Path.Combine(context.Fixture.PackageInstallRoot, "absent-parent")));
            resolution.Revalidate();
            File.WriteAllBytes(absent, "appeared"u8.ToArray());
            Assert.Throws<PackageStoreAdmissionException>(() => resolution.Revalidate());
        }
        context.AssertTrackedHandlesClosed();

        var missingParent = Path.Combine(context.Fixture.RootPath, "absent-state-parent", "state-payload.json");
        Assert.Throws<PackageStoreAdmissionException>(() =>
            context.Resolver.ResolveStatePayloadReferentMetadata(missingParent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingParent)));
        Assert.Equal(0, context.Observed.CreateCount);
        context.AssertTrackedHandlesClosed();

        var directoryTarget = context.CreateDirectory("state-payload-directory-target");
        Assert.Throws<PackageStoreAdmissionException>(() =>
            context.Resolver.ResolveStatePayloadReferentMetadata(directoryTarget));
        var directoryAlias = Path.Combine(context.Fixture.PackageInstallRoot, "state-directory-link");
        Directory.CreateSymbolicLink(directoryAlias, directoryTarget);
        Assert.Throws<PackageStoreAdmissionException>(() =>
            context.Resolver.ResolveStatePayloadReferentMetadata(directoryAlias));
        context.AssertTrackedHandlesClosed();

        var alias = Path.Combine(context.Fixture.PackageInstallRoot, "dangling-state-link");
        File.CreateSymbolicLink(alias, "missing-state-payload.json");
        context.Observed.ResetForCall();
        using (var dangling = context.Resolver.ResolveStatePayloadReferentMetadata(alias))
        {
            Assert.Single(dangling.TerminalLinkSlots);
            Assert.Equal("missing-state-payload.json", dangling.TerminalLinkSlots[0].LinkTargetText);
            Assert.Null(dangling.FinalPayloadSlot.Entry);
            dangling.Revalidate();
        }

        Assert.Equal(0, context.Observed.FileOpenCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.CreateCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_TerminalDotsAndSeparatorsRequireDirectories()
    {
        using var context = new Context();
        var packageRoot = context.Fixture.PackageInstallRoot;
        var separator = Path.DirectorySeparatorChar;
        var existing = Path.Combine(packageRoot, "terminal-dot-state.json");
        var absent = Path.Combine(packageRoot, "terminal-dot-absent-state.json");
        File.WriteAllBytes(existing, "state"u8.ToArray());

        var existingAlias = Path.Combine(packageRoot, "terminal-dot-existing-alias");
        var absentAlias = Path.Combine(packageRoot, "terminal-dot-absent-alias");
        File.CreateSymbolicLink(existingAlias, Path.GetFileName(existing) + separator + ".");
        File.CreateSymbolicLink(absentAlias, Path.GetFileName(absent) + separator + ".");

        var refusedPaths = new[]
        {
            existing + separator + ".",
            existing + separator,
            absent + separator + ".",
            absent + separator,
            existingAlias,
            absentAlias
        };

        context.Observed.ResetForCall();
        foreach (var path in refusedPaths)
        {
            Assert.Throws<PackageStoreAdmissionException>(() =>
            {
                using var unexpected = context.Resolver.ResolveStatePayloadReferentMetadata(path);
            });
            context.AssertTrackedHandlesClosed();
        }

        Assert.True(File.Exists(existing));
        Assert.False(File.Exists(absent));
        Assert.Equal(0, context.Observed.FileOpenCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.CreateCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_CycleAliasRetargetProfileAndAuthorityChangesRefuseAndRelease()
    {
        using (var context = new Context())
        {
            var cycle = Path.Combine(context.Fixture.PackageInstallRoot, "state-payload-cycle");
            File.CreateSymbolicLink(cycle, "state-payload-cycle");
            Assert.Throws<PackageStoreAdmissionException>(() =>
                context.Resolver.ResolveStatePayloadReferentMetadata(cycle));
            Assert.Equal(0, context.Observed.FileOpenCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var first = Path.Combine(context.Fixture.PackageInstallRoot, "state-a.json");
            var second = Path.Combine(context.Fixture.PackageInstallRoot, "state-b.json");
            File.WriteAllBytes(first, "a"u8.ToArray());
            File.WriteAllBytes(second, "b"u8.ToArray());
            var alias = Path.Combine(context.Fixture.PackageInstallRoot, "state-alias.json");
            File.CreateSymbolicLink(alias, "state-a.json");

            context.Observed.ResetForCall();
            using (var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(alias))
            {
                File.Delete(alias);
                File.CreateSymbolicLink(alias, "state-b.json");
                Assert.Throws<PackageStoreAdmissionException>(() => resolution.Revalidate());
            }

            Assert.Equal(0, context.Observed.FileOpenCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var path = Path.Combine(context.Fixture.PackageInstallRoot, "profile-state.json");
            File.WriteAllBytes(path, "state"u8.ToArray());
            context.Observed.ResetForCall();
            using (var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(path))
            {
                var original = context.Observed.ObserveDirectoryNameSemantics(resolution.FinalParent);
                context.Observed.NameSemanticsOverride = new PhysicalStoreNameSemantics(
                    "changed-profile-for-state-test", original.Encoding, original.CaseSensitive,
                    original.NormalizationInsensitive);
                Assert.Throws<PackageStoreAdmissionException>(() => resolution.Revalidate());
            }

            context.Observed.NameSemanticsOverride = null;
            Assert.Equal(0, context.Observed.FileOpenCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var path = Path.Combine(context.Fixture.PackageInstallRoot, "authority-state.json");
            File.WriteAllBytes(path, "state"u8.ToArray());
            context.Observed.ResetForCall();
            using (var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(path))
            {
                Directory.CreateDirectory(Path.Combine(context.Fixture.PackageInstallRoot,
                    RootMembershipRegistry.ControlDirectoryName));
                Assert.Throws<PackageStoreAdmissionException>(() => resolution.Revalidate());
            }

            Assert.Equal(0, context.Observed.FileOpenCount);
            context.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStatePayloadReferentMetadata_DisposeExpiresParentEvidence()
    {
        using var context = new Context();
        var path = Path.Combine(context.Fixture.PackageInstallRoot, "disposed-state.json");
        File.WriteAllBytes(path, "state"u8.ToArray());
        context.Observed.ResetForCall();

        var resolution = context.Resolver.ResolveStatePayloadReferentMetadata(path);
        resolution.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resolution.Revalidate());
        Assert.Equal(0, context.Observed.FileOpenCount);
        context.AssertTrackedHandlesClosed();
    }
}
