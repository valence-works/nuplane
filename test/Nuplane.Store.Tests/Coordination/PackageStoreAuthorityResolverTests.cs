using System.Runtime.InteropServices;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAuthorityResolverTests
{
    private const long Epoch = 23;
    private const string MemberId = "member";

    [SupportedPhysicalStoreFact]
    public void Resolve_UnenrolledDirectoryAndArchive_ReplaysAbsoluteAndExplicitBaseComponents()
    {
        using var context = new Context();
        context.CreateDirectory("packages/nested");
        var packagePath = context.CreateDirectory("packages/target");
        var absoluteWithDots = Join(context.Fixture.RootPath, "packages", ".", "nested", "..", "target");
        var relativeWithDots = Join("packages", ".", "nested", "..", "target");

        var absolute = context.Resolve(absoluteWithDots, PhysicalStorePathTarget.PackageDirectory);
        AssertUnenrolledDirectory(context, absolute, packagePath);

        var relative = context.Resolve(relativeWithDots, PhysicalStorePathTarget.PackageDirectory,
            exactBaseLocator: context.Fixture.RootPath);
        AssertUnenrolledDirectory(context, relative, packagePath);

        if (OperatingSystem.IsWindows())
        {
            var alternateAbsolute = absoluteWithDots.Replace('\\', '/');
            var alternateBase = context.Fixture.RootPath.Replace('\\', '/');
            var alternateRelative = relativeWithDots.Replace('\\', '/');
            AssertUnenrolledDirectory(context,
                context.Resolve(alternateAbsolute, PhysicalStorePathTarget.PackageDirectory), packagePath);
            AssertUnenrolledDirectory(context,
                context.Resolve(alternateRelative, PhysicalStorePathTarget.PackageDirectory,
                    exactBaseLocator: alternateBase), packagePath);
        }

        var archivePath = Join(context.Fixture.RootPath, "packages", "archive.nupkg");
        File.WriteAllBytes(archivePath, "archive-payload-must-not-be-read"u8.ToArray());
        using var archive = context.Resolve(archivePath, PhysicalStorePathTarget.ArchiveFile);
        Assert.Null(archive.AuthorityRoot);
        Assert.Null(archive.RootIdentity);
        Assert.Null(archive.MembershipCandidate);
        Assert.Equal(PhysicalStoreEntryKind.RegularFile, context.Observed.InspectHandle(archive.Target).Kind);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        archive.Dispose();
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_MissingTargetsAndInvalidBaseForms_RefuseWithoutFilesystemSideEffects()
    {
        using var context = new Context();
        context.AssertRefused(Path.Combine(context.Fixture.PackageInstallRoot, "missing"),
            PhysicalStorePathTarget.PackageDirectory);
        context.AssertRefused("packages/target", PhysicalStorePathTarget.PackageDirectory);
        context.AssertRefused("target", PhysicalStorePathTarget.PackageDirectory, exactBaseLocator: "relative-base");
        context.AssertRefused(Path.Combine(context.Fixture.PackageInstallRoot, "target"),
            PhysicalStorePathTarget.PackageDirectory, exactBaseLocator: context.Fixture.RootPath);

        var directory = context.CreateDirectory("packages/directory-target");
        var archive = Path.Combine(context.Fixture.PackageInstallRoot, "archive-target.nupkg");
        File.WriteAllBytes(archive, "archive-payload-must-not-be-read"u8.ToArray());
        context.AssertRefused(directory, PhysicalStorePathTarget.ArchiveFile);
        context.AssertRefused(archive, PhysicalStorePathTarget.PackageDirectory);
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_ConfiguredRootAliasesAbsoluteAndRelative_ConvergeOnNativeRootIdentity()
    {
        using var context = new Context();
        var rootIdentity = context.PublishCompleteCandidate(context.Fixture.PackageInstallRoot);
        var absoluteAlias = Path.Combine(context.Fixture.RootPath, "absolute-store-alias");
        var relativeAlias = Path.Combine(context.Fixture.RootPath, "relative-store-alias");
        Directory.CreateSymbolicLink(absoluteAlias, context.Fixture.PackageInstallRoot);
        Directory.CreateSymbolicLink(relativeAlias, "packages");

        using (var absolute = context.Resolve(absoluteAlias, PhysicalStorePathTarget.ConfiguredRootDirectory,
                   requiredRoot: rootIdentity))
        {
            Assert.Equal(rootIdentity, absolute.RootIdentity);
            Assert.Equal(RootMembershipStatus.Complete, absolute.MembershipCandidate?.Status);
            Assert.Equal(rootIdentity.HandleIdentity, context.Observed.InspectHandle(absolute.Target).Identity);
            absolute.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        using (var relative = context.Resolve("relative-store-alias", PhysicalStorePathTarget.ConfiguredRootDirectory,
                   requiredRoot: rootIdentity, exactBaseLocator: context.Fixture.RootPath))
        {
            Assert.Equal(rootIdentity, relative.RootIdentity);
            Assert.Equal(RootMembershipStatus.Complete, relative.MembershipCandidate?.Status);
            Assert.Equal(rootIdentity.HandleIdentity, context.Observed.InspectHandle(relative.Target).Identity);
            relative.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        var insideTarget = context.CreateDirectory("packages/within-prefix/inside");
        var outsidePrefixAlias = Path.Combine(context.Fixture.RootPath, "ancestor-prefix-alias");
        Directory.CreateSymbolicLink(outsidePrefixAlias, ".");
        var absoluteAliasThroughPrefix = Path.Combine(context.Fixture.PackageInstallRoot, "absolute-alias-through-prefix");
        var absoluteTarget = Join(outsidePrefixAlias, "packages", "within-prefix", "inside");
        Directory.CreateSymbolicLink(absoluteAliasThroughPrefix, absoluteTarget);
        using (var throughPrefix = context.Resolve(absoluteAliasThroughPrefix,
                   PhysicalStorePathTarget.ConfiguredRootDirectory, requiredRoot: rootIdentity))
        {
            Assert.Equal(rootIdentity, throughPrefix.RootIdentity);
            Assert.Equal(RootMembershipStatus.Complete, throughPrefix.MembershipCandidate?.Status);
            using var expected = PhysicalStoreTestDirectory.Open(context.NativeFiles, insideTarget);
            Assert.Equal(context.NativeFiles.InspectHandle(expected).Identity,
                context.Observed.InspectHandle(throughPrefix.Target).Identity);
            throughPrefix.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        var child = context.CreateDirectory("packages/within-root/child");
        var repeatedLink = Path.Combine(Path.GetDirectoryName(child)!, "link");
        Directory.CreateSymbolicLink(repeatedLink, "child");
        var repeated = Join(Path.GetDirectoryName(child)!, "link", "..", "link");
        using (var repeatedAlias = context.Resolve(repeated, PhysicalStorePathTarget.ConfiguredRootDirectory,
                   requiredRoot: rootIdentity))
        {
            Assert.Equal(rootIdentity, repeatedAlias.RootIdentity);
            Assert.Equal(RootMembershipStatus.Complete, repeatedAlias.MembershipCandidate?.Status);
            using var expected = PhysicalStoreTestDirectory.Open(context.NativeFiles, child);
            Assert.Equal(context.NativeFiles.InspectHandle(expected).Identity,
                context.Observed.InspectHandle(repeatedAlias.Target).Identity);
            repeatedAlias.Revalidate();
        }
        context.AssertTrackedHandlesClosed();

        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.CreateCount);
        Assert.Equal(0, context.Observed.LockAttemptCount);
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_RequiredRootWithParentComponentsBeforeAuthority_ReplaysWithoutInventingAnEarlierBoundary()
    {
        using var context = new Context();
        var rootIdentity = context.PublishCompleteCandidate(context.Fixture.PackageInstallRoot);
        context.CreateDirectory("prefix-child");
        var rawRelative = Join("prefix-child", "..", "packages");
        var rawAbsolute = Join(context.Fixture.RootPath, rawRelative);
        var alias = Path.Combine(context.Fixture.RootPath, "prefix-alias");
        Directory.CreateSymbolicLink(alias, rawRelative);

        foreach (var (locator, capturedBase) in new (string, string?)[]
                 {
                     (rawAbsolute, null),
                     (rawRelative, context.Fixture.RootPath),
                     (alias, null),
                 })
        {
            using (var result = context.Resolve(locator, PhysicalStorePathTarget.ConfiguredRootDirectory,
                       requiredRoot: rootIdentity, exactBaseLocator: capturedBase))
            {
                Assert.Equal(rootIdentity, result.RootIdentity);
                Assert.Equal(rootIdentity.HandleIdentity, context.Observed.InspectHandle(result.Target).Identity);
                Assert.True(context.Observed.ParentOpenCount > 0,
                    "The prefix parent component must be replayed rather than lexically removed.");
                result.Revalidate();
                context.AssertNoResolverSideEffects();
            }

            context.AssertTrackedHandlesClosed();
        }

        context.AssertRefused(Join(context.Fixture.PackageInstallRoot, "..", "packages"),
            PhysicalStorePathTarget.ConfiguredRootDirectory, requiredRoot: rootIdentity);
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_FinalPackageAndArchiveLinksOrHardlinks_RefusesWithoutReadingPayload()
    {
        using var context = new Context();
        var targetDirectory = context.CreateDirectory("packages/real-package");
        var packageAlias = Path.Combine(context.Fixture.PackageInstallRoot, "package-alias");
        Directory.CreateSymbolicLink(packageAlias, targetDirectory);
        context.AssertRefused(packageAlias, PhysicalStorePathTarget.PackageDirectory);
        context.AssertRefused(packageAlias + Path.DirectorySeparatorChar, PhysicalStorePathTarget.PackageDirectory);
        context.AssertRefused(packageAlias + Path.DirectorySeparatorChar + ".", PhysicalStorePathTarget.PackageDirectory);

        var archiveTarget = Path.Combine(context.Fixture.PackageInstallRoot, "real.nupkg");
        var archiveAlias = Path.Combine(context.Fixture.PackageInstallRoot, "archive-alias.nupkg");
        File.WriteAllBytes(archiveTarget, "archive-target-payload"u8.ToArray());
        File.CreateSymbolicLink(archiveAlias, archiveTarget);
        context.AssertRefused(archiveAlias, PhysicalStorePathTarget.ArchiveFile);

        var archiveHardLink = Path.Combine(context.Fixture.PackageInstallRoot, "archive-hardlink.nupkg");
        CreateHardLink(archiveTarget, archiveHardLink);
        context.AssertRefused(archiveHardLink, PhysicalStorePathTarget.ArchiveFile);
    }

    [LinuxExt4CasefoldFact]
    public void Resolve_Ext4CasefoldDirectorySpellingFindsNativeAliasAndReservedAuthority()
    {
        using (var aliasContext = new Context())
        {
            UnixPhysicalStoreIdentityTests.EnableExt4Casefold(aliasContext.Fixture.PackageInstallRoot);
            const string storedName = "Package-\u00e9";
            const string aliasName = "pACKAGE-e\u0301";
            var storedPath = Directory.CreateDirectory(Path.Combine(aliasContext.Fixture.PackageInstallRoot, storedName)).FullName;

            using (var resolved = aliasContext.Resolve(Path.Combine(aliasContext.Fixture.PackageInstallRoot, aliasName),
                       PhysicalStorePathTarget.PackageDirectory))
            using (var expected = PhysicalStoreTestDirectory.Open(aliasContext.NativeFiles, storedPath))
            {
                Assert.Equal(aliasContext.NativeFiles.InspectHandle(expected).Identity,
                    aliasContext.Observed.InspectHandle(resolved.Target).Identity);
                Assert.Null(resolved.AuthorityRoot);
                Assert.Null(resolved.RootIdentity);
                Assert.Null(resolved.MembershipCandidate);
                resolved.Revalidate();
            }

            aliasContext.AssertTrackedHandlesClosed();
            Assert.Equal(0, aliasContext.Observed.PackagePayloadReadCount);
            Assert.Equal(0, aliasContext.Observed.CreateCount);
            Assert.Equal(0, aliasContext.Observed.LockAttemptCount);
        }

        using (var markerContext = new Context())
        {
            UnixPhysicalStoreIdentityTests.EnableExt4Casefold(markerContext.Fixture.PackageInstallRoot);
            Directory.CreateDirectory(Path.Combine(markerContext.Fixture.PackageInstallRoot, ".NUPLANE-STORE"));
            markerContext.AssertRefused(markerContext.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_InspectsReservedAuthorityBeforeFollowingAnEscapingAlias()
    {
        using var context = new Context();
        var rootIdentity = context.PublishCompleteCandidate(context.Fixture.PackageInstallRoot);
        var outside = context.CreateDirectory("outside");
        var destination = Path.Combine(outside, "package");
        Directory.CreateDirectory(destination);
        var escapeAlias = Path.Combine(context.Fixture.PackageInstallRoot, "escape-alias");
        Directory.CreateSymbolicLink(escapeAlias, outside);
        context.Observed.Reset();
        context.Observed.ObserveOrderFor(rootIdentity.HandleIdentity, "escape-alias");

        var exception = Assert.Throws<PackageStoreAdmissionException>(() => context.Resolver.Resolve(
            Join(context.Fixture.PackageInstallRoot, "escape-alias", "package"),
            PhysicalStorePathTarget.PackageDirectory));
        AssertRefusalReason(exception);
        context.AssertNoResolverSideEffects();
        context.AssertTrackedHandlesClosed();

        var events = context.Observed.Events.ToList();
        var markerIndex = events.FindIndex(item => item == "root-marker");
        var linkIndex = events.FindIndex(item => item == "link:escape-alias");
        Assert.True(markerIndex >= 0 && linkIndex >= 0 && markerIndex < linkIndex,
            $"The root's reserved authority must be inspected before the alias target is read. Events: {string.Join(",", events)}");
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_NestedDifferentAndEscapeThenReturnAuthorities_Refuses()
    {
        using (var nested = new Context())
        {
            nested.PublishCompleteCandidate(nested.Fixture.PackageInstallRoot);
            var nestedRoot = nested.CreateDirectory("packages/nested");
            nested.PublishCompleteCandidate(nestedRoot);
            nested.AssertRefused(Join(nested.Fixture.PackageInstallRoot, "nested"),
                PhysicalStorePathTarget.PackageDirectory);
        }

        using (var different = new Context())
        {
            different.PublishCompleteCandidate(different.Fixture.PackageInstallRoot);
            var otherRoot = different.CreateDirectory("other-store");
            different.PublishCompleteCandidate(otherRoot);
            var child = Directory.CreateDirectory(Path.Combine(otherRoot, "package")).FullName;
            var alias = Path.Combine(different.Fixture.PackageInstallRoot, "different-root-alias");
            Directory.CreateSymbolicLink(alias, otherRoot);
            different.AssertRefused(Join(different.Fixture.PackageInstallRoot, "different-root-alias", "package"),
                PhysicalStorePathTarget.PackageDirectory);
            Assert.True(Directory.Exists(child));
        }

        using (var escapeReturn = new Context())
        {
            escapeReturn.PublishCompleteCandidate(escapeReturn.Fixture.PackageInstallRoot);
            var child = escapeReturn.CreateDirectory("packages/child");
            var escapeAndReturn = Join(escapeReturn.Fixture.PackageInstallRoot, "..", "packages", "child");
            escapeReturn.AssertRefused(escapeAndReturn, PhysicalStorePathTarget.PackageDirectory);
            Assert.True(escapeReturn.Observed.ParentOpenCount > 0,
                "The literal parent component must be replayed through native parent handles.");

            var aliasPath = Path.Combine(escapeReturn.Fixture.PackageInstallRoot, "escape-return");
            Directory.CreateSymbolicLink(aliasPath, escapeAndReturn);
            escapeReturn.AssertRefused(aliasPath, PhysicalStorePathTarget.ConfiguredRootDirectory);
            Assert.True(escapeReturn.Observed.LinkReadCount > 0,
                "Configured-root resolution must replay the final alias target before refusing its authority escape.");
            Assert.True(Directory.Exists(child));
        }

        using (var cycles = new Context())
        {
            var selfCycle = Path.Combine(cycles.Fixture.PackageInstallRoot, "self-cycle");
            Directory.CreateSymbolicLink(selfCycle, "self-cycle");
            cycles.AssertRefused(selfCycle, PhysicalStorePathTarget.ConfiguredRootDirectory);

            var first = Path.Combine(cycles.Fixture.PackageInstallRoot, "cycle-one");
            var second = Path.Combine(cycles.Fixture.PackageInstallRoot, "cycle-two");
            Directory.CreateSymbolicLink(first, "cycle-two");
            Directory.CreateSymbolicLink(second, "cycle-one");
            cycles.AssertRefused(first, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_PresentMissingMalformedOrIncompleteControlNamespace_IsTypedUnknown()
    {
        using (var missingLedger = new Context())
        {
            missingLedger.CreateControlDirectory(missingLedger.Fixture.PackageInstallRoot);
            missingLedger.AssertRefused(missingLedger.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var malformed = new Context())
        {
            malformed.CreateMalformedCandidate(malformed.Fixture.PackageInstallRoot, "not-json"u8.ToArray());
            malformed.AssertRefused(malformed.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var badDigest = new Context())
        {
            badDigest.PublishCompleteCandidate(badDigest.Fixture.PackageInstallRoot, corruptLedgerDigest: true);
            badDigest.AssertRefused(badDigest.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var incomplete = new Context())
        {
            incomplete.PublishIncompleteCandidate(incomplete.Fixture.PackageInstallRoot);
            incomplete.AssertRefused(incomplete.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var wrongKind = new Context())
        {
            wrongKind.CreateWrongKindControlEntry(wrongKind.Fixture.PackageInstallRoot);
            wrongKind.AssertRefused(wrongKind.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var wrongRoot = new Context())
        {
            var wrongAuthority = wrongRoot.CreateDirectory("other-root");
            using var wrongAuthorityHandle = PhysicalStoreTestDirectory.Open(wrongRoot.NativeFiles, wrongAuthority);
            var wrongRootIdentity = new PhysicalRootIdentity(wrongRoot.NativeFiles.InspectHandle(wrongAuthorityHandle).Identity);
            wrongRoot.PublishCompleteCandidate(wrongRoot.Fixture.PackageInstallRoot, rootIdentityOverride: wrongRootIdentity);
            wrongRoot.AssertRefused(wrongRoot.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var requiredRootMismatch = new Context())
        {
            requiredRootMismatch.PublishCompleteCandidate(requiredRootMismatch.Fixture.PackageInstallRoot);
            var otherRoot = requiredRootMismatch.CreateDirectory("other-root");
            using var otherRootHandle = PhysicalStoreTestDirectory.Open(requiredRootMismatch.NativeFiles, otherRoot);
            var wrongRequiredRoot = new PhysicalRootIdentity(requiredRootMismatch.NativeFiles.InspectHandle(otherRootHandle).Identity);
            requiredRootMismatch.AssertRefused(requiredRootMismatch.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory, requiredRoot: wrongRequiredRoot);
        }

        using (var unsupportedSchema = new Context())
        {
            unsupportedSchema.PublishCompleteCandidate(unsupportedSchema.Fixture.PackageInstallRoot);
            unsupportedSchema.ChangeLedgerSchemaVersion(unsupportedSchema.Fixture.PackageInstallRoot, 2);
            unsupportedSchema.AssertRefused(unsupportedSchema.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_ReservedControlOrMembershipLedgerLinksAreNeverAuthority()
    {
        using (var linkedControl = new Context())
        {
            var target = linkedControl.CreateDirectory("control-target");
            Directory.CreateSymbolicLink(Path.Combine(linkedControl.Fixture.PackageInstallRoot,
                RootMembershipRegistry.ControlDirectoryName), target);
            linkedControl.AssertRefused(linkedControl.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var linkedLedger = new Context())
        {
            linkedLedger.PublishCompleteCandidate(linkedLedger.Fixture.PackageInstallRoot);
            linkedLedger.ReplaceLedgerWithSymbolicLink(linkedLedger.Fixture.PackageInstallRoot);
            linkedLedger.AssertRefused(linkedLedger.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }

        using (var hardLinkedLedger = new Context())
        {
            hardLinkedLedger.PublishCompleteCandidate(hardLinkedLedger.Fixture.PackageInstallRoot);
            hardLinkedLedger.ReplaceLedgerWithHardLink(hardLinkedLedger.Fixture.PackageInstallRoot);
            hardLinkedLedger.AssertRefused(hardLinkedLedger.Fixture.PackageInstallRoot,
                PhysicalStorePathTarget.ConfiguredRootDirectory);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Revalidate_RejectsIdenticalLedgerBytesPublishedWithANewNativeIdentity()
    {
        using var context = new Context();
        var rootPath = context.Fixture.PackageInstallRoot;
        var rootIdentity = context.PublishCompleteCandidate(rootPath);
        var ledgerPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName,
            RootMembershipRegistry.LedgerName);
        var replacementPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName, "membership.next");
        var originalBytes = File.ReadAllBytes(ledgerPath);
        PhysicalFileIdentity originalIdentity;
        using (var root = PhysicalStoreTestDirectory.Open(context.NativeFiles, rootPath))
        using (var control = context.NativeFiles.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName))
            originalIdentity = context.NativeFiles.InspectChildNoFollow(control, RootMembershipRegistry.LedgerName)!.Identity;

        using (var resolved = context.Resolve(rootPath, PhysicalStorePathTarget.ConfiguredRootDirectory,
                   requiredRoot: rootIdentity))
        {
            var originalDigest = Assert.IsType<RootMembershipRecord>(resolved.MembershipCandidate).LedgerDigest;
            using (var control = context.NativeFiles.OpenDirectoryChildNoFollow(resolved.AuthorityRoot!,
                       RootMembershipRegistry.ControlDirectoryName))
                Assert.Equal(originalIdentity, context.NativeFiles.InspectChildNoFollow(control,
                    RootMembershipRegistry.LedgerName)!.Identity);

            File.WriteAllBytes(replacementPath, originalBytes);
            var replaceError = Record.Exception(() => File.Move(replacementPath, ledgerPath, overwrite: true));
            if (replaceError is null)
            {
                using var root = PhysicalStoreTestDirectory.Open(context.NativeFiles, rootPath);
                using var control = context.NativeFiles.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
                var replacementIdentity = context.NativeFiles.InspectChildNoFollow(control, RootMembershipRegistry.LedgerName)!.Identity;
                Assert.NotEqual(originalIdentity, replacementIdentity);
                context.RefreshControlTracking(rootPath);

                var exception = Assert.Throws<PackageStoreAdmissionException>(resolved.Revalidate);
                AssertRefusalReason(exception);
                Assert.Equal(originalDigest, resolved.MembershipCandidate!.LedgerDigest);
            }
            else
            {
                Assert.True(OperatingSystem.IsWindows() && (replaceError is IOException or UnauthorizedAccessException),
                    $"Only a Windows non-delete-sharing ledger handle may block atomic replacement; got {replaceError.GetType().Name}.");
                resolved.Revalidate();
            }

            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            Assert.Equal(0, context.Observed.CreateCount);
            Assert.Equal(0, context.Observed.LockAttemptCount);
        }

        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_AliasOrDirectoryEdgeReplacementDuringReplay_RefusesAndClosesOwnedHandles()
    {
        using (var aliasRace = new Context())
        {
            aliasRace.CreateDirectory("packages/target-a");
            aliasRace.CreateDirectory("packages/target-b");
            var alias = Path.Combine(aliasRace.Fixture.PackageInstallRoot, "changing-alias");
            Directory.CreateSymbolicLink(alias, "target-a");
            var rootIdentity = aliasRace.NativeFiles.InspectHandle(aliasRace.PackageRoot).Identity;
            var aliasMutationRan = false;
            aliasRace.Observed.Reset();
            aliasRace.Observed.AfterLinkRead = (parent, name, _) =>
            {
                if (name == "changing-alias" && aliasRace.NativeFiles.InspectHandle(parent).Identity == rootIdentity)
                {
                    aliasMutationRan = true;
                    aliasRace.Observed.AfterLinkRead = null;
                    Directory.Delete(alias);
                    Directory.CreateSymbolicLink(alias, "target-b");
                }
            };

            var exception = Assert.Throws<PackageStoreAdmissionException>(() => aliasRace.Resolver.Resolve(
                alias, PhysicalStorePathTarget.ConfiguredRootDirectory));
            AssertRefusalReason(exception);
            Assert.True(aliasMutationRan, "The configured-root path must read the alias before the test replaces it.");
            aliasRace.AssertNoResolverSideEffects();
            aliasRace.AssertTrackedHandlesClosed();
        }

        using (var edgeRace = new Context())
        {
            var edgePath = edgeRace.CreateDirectory("packages/edge");
            var movedPath = edgePath + "-moved";
            var rootIdentity = edgeRace.NativeFiles.InspectHandle(edgeRace.PackageRoot).Identity;
            var edgeMutationRan = false;
            edgeRace.Observed.Reset();
            edgeRace.Observed.AfterInspectChild = (parent, name, info) =>
            {
                if (name == "edge" && info?.Kind == PhysicalStoreEntryKind.Directory &&
                    edgeRace.NativeFiles.InspectHandle(parent).Identity == rootIdentity)
                {
                    edgeMutationRan = true;
                    edgeRace.Observed.AfterInspectChild = null;
                    Directory.Move(edgePath, movedPath);
                    Directory.CreateDirectory(edgePath);
                }
            };

            var exception = Assert.Throws<PackageStoreAdmissionException>(() => edgeRace.Resolver.Resolve(
                Join(edgeRace.Fixture.PackageInstallRoot, "edge"), PhysicalStorePathTarget.PackageDirectory));
            AssertRefusalReason(exception);
            Assert.True(edgeMutationRan, "The observed directory edge must be replaced before the held open is checked.");
            edgeRace.AssertNoResolverSideEffects();
            edgeRace.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void Revalidate_DetectsNewAuthorityReplacedDirectoryAndChangedAlias()
    {
        using (var newAuthority = new Context())
        {
            var child = newAuthority.CreateDirectory("packages/child");
            using var result = newAuthority.Resolve(child, PhysicalStorePathTarget.PackageDirectory);
            newAuthority.CreateControlDirectory(newAuthority.Fixture.PackageInstallRoot);
            var exception = Assert.Throws<PackageStoreAdmissionException>(result.Revalidate);
            AssertRefusalReason(exception);
            Assert.Equal(0, newAuthority.Observed.PackagePayloadReadCount);
            Assert.Equal(0, newAuthority.Observed.CreateCount);
        }

        using (var replacedDirectory = new Context())
        {
            var target = replacedDirectory.CreateDirectory("packages/target");
            using var result = replacedDirectory.Resolve(target, PhysicalStorePathTarget.PackageDirectory);
            var moved = target + "-moved";
            var rename = Record.Exception(() => Directory.Move(target, moved));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(rename is IOException or UnauthorizedAccessException,
                    "A held Windows directory handle without delete sharing must prevent replacing the observed edge.");
                result.Revalidate();
            }
            else
            {
                Assert.Null(rename);
                Directory.CreateDirectory(target);
                var exception = Assert.Throws<PackageStoreAdmissionException>(result.Revalidate);
                AssertRefusalReason(exception);
            }

            Assert.Equal(0, replacedDirectory.Observed.PackagePayloadReadCount);
        }

        using (var changedAlias = new Context())
        {
            changedAlias.PublishCompleteCandidate(changedAlias.Fixture.PackageInstallRoot);
            var secondRoot = changedAlias.CreateDirectory("other-store");
            changedAlias.PublishCompleteCandidate(secondRoot);
            var alias = Path.Combine(changedAlias.Fixture.RootPath, "root-alias");
            Directory.CreateSymbolicLink(alias, changedAlias.Fixture.PackageInstallRoot);
            var result = changedAlias.Resolve(alias, PhysicalStorePathTarget.ConfiguredRootDirectory);
            try
            {
                Directory.Delete(alias);
                Directory.CreateSymbolicLink(alias, secondRoot);
                var exception = Assert.Throws<PackageStoreAdmissionException>(result.Revalidate);
                AssertRefusalReason(exception);
                Assert.Equal(0, changedAlias.Observed.PackagePayloadReadCount);
            }
            finally
            {
                result.Dispose();
            }

            changedAlias.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void Revalidate_AfterResultDisposal_RefusesExpiredEvidence()
    {
        using var context = new Context();
        var target = context.CreateDirectory("packages/target");
        var result = context.Resolve(target, PhysicalStorePathTarget.PackageDirectory);

        result.Dispose();

        Assert.Throws<ObjectDisposedException>(result.Revalidate);
        context.AssertTrackedHandlesClosed();
    }

    private static void AssertUnenrolledDirectory(Context context, ResolvedPackageStorePath result, string expectedPath)
    {
        using (result)
        {
            Assert.Null(result.AuthorityRoot);
            Assert.Null(result.RootIdentity);
            Assert.Null(result.MembershipCandidate);
            using var expected = PhysicalStoreTestDirectory.Open(context.NativeFiles, expectedPath);
            Assert.Equal(context.NativeFiles.InspectHandle(expected).Identity,
                context.Observed.InspectHandle(result.Target).Identity);
            result.Revalidate();
        }

        context.AssertTrackedHandlesClosed();
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.CreateCount);
        Assert.Equal(0, context.Observed.LockAttemptCount);
    }

    private static string Join(params string[] components)
        => string.Join(Path.DirectorySeparatorChar, components);

    private static void AssertRefusalReason(PackageStoreAdmissionException exception)
        => Assert.Contains(exception.Reason, new[]
        {
            PackageStoreAdmissionReason.UnknownAuthority,
            PackageStoreAdmissionReason.RootMismatch,
            PackageStoreAdmissionReason.IncompleteEnrollment,
        });

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : LinkUnix(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {result}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkDarwin(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkLinux(string existingPath, string newPath);

    private static int LinkUnix(string existingPath, string newPath)
        => OperatingSystem.IsMacOS() ? LinkDarwin(existingPath, newPath) : LinkLinux(existingPath, newPath);

    private sealed class Context : IDisposable
    {
        private readonly HashSet<PhysicalFileIdentity> _controlLedgerIdentities = [];
        private bool _disposed;

        internal Context()
        {
            Fixture = new PackageStoreFixture();
            NativeFiles = OperatingSystem.IsWindows()
                ? new WindowsPhysicalStoreFileSystem()
                : new UnixPhysicalStoreFileSystem();
            Observed = new ObservingFileSystem(NativeFiles, _controlLedgerIdentities);
            PackageRoot = PhysicalStoreTestDirectory.Open(NativeFiles, Fixture.PackageInstallRoot);
            Registry = new RootMembershipRegistry(NativeFiles, new StoreStateSerializer());
            Resolver = new PackageStoreAuthorityResolver(Observed, Registry);
            PrepareStructuralStateSlot();
        }

        internal PackageStoreFixture Fixture { get; }
        internal IPhysicalStoreFileSystem NativeFiles { get; }
        internal ObservingFileSystem Observed { get; }
        internal PhysicalStoreDirectoryHandle PackageRoot { get; }
        internal RootMembershipRegistry Registry { get; }
        internal PackageStoreAuthorityResolver Resolver { get; }

        internal string CreateDirectory(string relativePath) => Fixture.CreateDirectory(relativePath);

        internal ResolvedPackageStorePath Resolve(string locator, PhysicalStorePathTarget target,
            PhysicalRootIdentity? requiredRoot = null, string? exactBaseLocator = null)
        {
            Observed.Reset();
            return Resolver.Resolve(locator, target, requiredRoot, exactBaseLocator);
        }

        internal PhysicalRootIdentity PublishCompleteCandidate(string rootPath, bool corruptLedgerDigest = false,
            PhysicalRootIdentity? rootIdentityOverride = null)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            var actualRootIdentity = new PhysicalRootIdentity(NativeFiles.InspectHandle(root).Identity);
            var rootIdentity = rootIdentityOverride ?? actualRootIdentity;
            var stateParentPath = Path.GetDirectoryName(Fixture.StateFilePath)!;
            using var stateParent = PhysicalStoreTestDirectory.Open(NativeFiles, stateParentPath);
            var stateObservation = new PhysicalStoreIdentity(NativeFiles).ObserveStateSlot(stateParent,
                Path.GetFileName(Fixture.StateFilePath));
            var body = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
            var protectionCandidate = new PackageProtectionRecord(1, rootIdentity, Epoch, MemberId, 1,
                ProtectionDigest.StateBody(body), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, rootIdentity, Epoch, MemberId, 1,
                protectionCandidate.StateBodyDigest, ProtectionDigest.Protection(protectionCandidate),
                closure, closure, [], false);
            var member = new RootMemberRecord(MemberId, Fixture.StateFilePath,
                new RootMemberRecord.AcknowledgedBinding(stateObservation.Slot, stateObservation.FileIdentity, protection));
            var incompleteDigest = new RootMembershipRecord(1, rootIdentity, Epoch, RootMembershipStatus.Complete,
                [member], [MemberId], [], null, new string('0', 64));
            var candidate = RootMembershipRegistry.Rebuild(incompleteDigest, RootMembershipStatus.Complete, [member], null);
            var bytes = new RootMembershipPayloadSerializer().Serialize(candidate);
            if (corruptLedgerDigest)
            {
                var json = Encoding.UTF8.GetString(bytes);
                var replacement = new string(candidate.LedgerDigest[0] == '0' ? '1' : '0', candidate.LedgerDigest.Length);
                bytes = Encoding.UTF8.GetBytes(json.Replace(candidate.LedgerDigest, replacement, StringComparison.Ordinal));
            }

            using var control = NativeFiles.CreateDirectoryExclusiveAt(root, RootMembershipRegistry.ControlDirectoryName);
            CreateControlFile(control, "root.lock", []);
            CreateControlFile(control, RootMembershipRegistry.LedgerName, bytes);
            ObserveControl(control, RootMembershipRegistry.LedgerName);
            return actualRootIdentity;
        }

        internal void PublishIncompleteCandidate(string rootPath)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            var rootIdentity = new PhysicalRootIdentity(NativeFiles.InspectHandle(root).Identity);
            var declared = new RootMemberRecord(MemberId, Fixture.StateFilePath, new RootMemberRecord.DeclaredBinding());
            Registry.InitializeIncomplete(root, rootIdentity, Epoch, [declared],
                quiescentCutoverConfirmed: true, CancellationToken.None);
            using var control = NativeFiles.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
            ObserveControl(control, RootMembershipRegistry.LedgerName);
        }

        internal void CreateControlDirectory(string rootPath)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            using var control = NativeFiles.CreateDirectoryExclusiveAt(root, RootMembershipRegistry.ControlDirectoryName);
        }

        internal void CreateMalformedCandidate(string rootPath, byte[] bytes)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            using var control = NativeFiles.CreateDirectoryExclusiveAt(root, RootMembershipRegistry.ControlDirectoryName);
            CreateControlFile(control, RootMembershipRegistry.LedgerName, bytes);
            ObserveControl(control, RootMembershipRegistry.LedgerName);
        }

        internal void CreateWrongKindControlEntry(string rootPath)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            CreateControlFile(root, RootMembershipRegistry.ControlDirectoryName, "wrong-kind"u8.ToArray());
        }

        internal void AssertRefused(string locator, PhysicalStorePathTarget target, string? exactBaseLocator = null,
            PhysicalRootIdentity? requiredRoot = null)
        {
            Observed.Reset();
            var exception = Assert.Throws<PackageStoreAdmissionException>(() => Resolver.Resolve(
                locator, target, requiredRoot, exactBaseLocator));
            AssertRefusalReason(exception);
            AssertNoResolverSideEffects();
            AssertTrackedHandlesClosed();
        }

        internal void AssertNoResolverSideEffects()
        {
            Assert.Equal(0, Observed.PackagePayloadReadCount);
            Assert.Equal(0, Observed.CreateCount);
            Assert.Equal(0, Observed.WriteCount);
            Assert.Equal(0, Observed.LockAttemptCount);
        }

        internal void AssertTrackedHandlesClosed() => Observed.AssertTrackedHandlesClosed();

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { PackageRoot.Dispose(); }
            finally { Fixture.Dispose(); }
        }

        private void PrepareStructuralStateSlot()
        {
            using var parent = PhysicalStoreTestDirectory.Open(NativeFiles, Path.GetDirectoryName(Fixture.StateFilePath)!);
            using var file = NativeFiles.CreateFileExclusiveAt(parent, Path.GetFileName(Fixture.StateFilePath));
            NativeFiles.WriteNewControlFile(file, "structural-candidate-only"u8.ToArray());
        }

        private void ObserveControl(PhysicalStoreDirectoryHandle control, string ledgerName)
        {
            var ledger = NativeFiles.InspectChildNoFollow(control, ledgerName);
            if (ledger is not null)
                _controlLedgerIdentities.Add(ledger.Identity);
        }

        private void CreateControlFile(PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
        {
            using var file = NativeFiles.CreateFileExclusiveAt(parent, name);
            NativeFiles.WriteNewControlFile(file, bytes);
        }

        internal void ReplaceLedgerWithSymbolicLink(string rootPath)
        {
            var controlPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName);
            var ledgerPath = Path.Combine(controlPath, RootMembershipRegistry.LedgerName);
            var targetPath = Path.Combine(Fixture.RootPath, "linked-membership.json");
            File.WriteAllBytes(targetPath, File.ReadAllBytes(ledgerPath));
            File.Delete(ledgerPath);
            File.CreateSymbolicLink(ledgerPath, targetPath);
            RefreshControlTracking(rootPath);
        }

        internal void ReplaceLedgerWithHardLink(string rootPath)
        {
            var controlPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName);
            var ledgerPath = Path.Combine(controlPath, RootMembershipRegistry.LedgerName);
            var targetPath = Path.Combine(Fixture.RootPath, "hardlinked-membership.json");
            File.WriteAllBytes(targetPath, File.ReadAllBytes(ledgerPath));
            File.Delete(ledgerPath);
            CreateHardLink(targetPath, ledgerPath);
            RefreshControlTracking(rootPath);
        }

        internal void ChangeLedgerSchemaVersion(string rootPath, int schemaVersion)
        {
            var ledgerPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName,
                RootMembershipRegistry.LedgerName);
            var payload = File.ReadAllText(ledgerPath);
            var updated = payload.Replace("\"schemaVersion\":1", $"\"schemaVersion\":{schemaVersion}", StringComparison.Ordinal);
            Assert.NotEqual(payload, updated);
            File.WriteAllText(ledgerPath, updated);
        }

        internal void RefreshControlTracking(string rootPath)
        {
            using var root = PhysicalStoreTestDirectory.Open(NativeFiles, rootPath);
            using var control = NativeFiles.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
            ObserveControl(control, RootMembershipRegistry.LedgerName);
        }
    }

    private sealed class ObservingFileSystem(
        IPhysicalStoreFileSystem inner,
        IReadOnlySet<PhysicalFileIdentity> ledgerIdentities)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem
    {
        private readonly List<PhysicalStoreHandle> _openedHandles = [];
        private readonly List<string> _events = [];
        private PhysicalFileIdentity? _orderedRootIdentity;
        private string? _orderedLinkName;

        internal int PackagePayloadReadCount { get; private set; }
        internal int CreateCount { get; private set; }
        internal int WriteCount { get; private set; }
        internal int LockAttemptCount { get; private set; }
        internal int ParentOpenCount { get; private set; }
        internal int LinkReadCount { get; private set; }
        internal IReadOnlyList<string> Events => _events;
        internal Action<PhysicalStoreDirectoryHandle, string, PhysicalStoreEntryInfo?>? AfterInspectChild { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? AfterLinkRead { get; set; }

        internal void Reset()
        {
            _openedHandles.Clear();
            _events.Clear();
            PackagePayloadReadCount = 0;
            CreateCount = 0;
            WriteCount = 0;
            LockAttemptCount = 0;
            ParentOpenCount = 0;
            LinkReadCount = 0;
            AfterInspectChild = null;
            AfterLinkRead = null;
            _orderedRootIdentity = null;
            _orderedLinkName = null;
        }

        internal void ObserveOrderFor(PhysicalFileIdentity rootIdentity, string linkName)
        {
            _orderedRootIdentity = rootIdentity;
            _orderedLinkName = linkName;
        }

        internal void AssertTrackedHandlesClosed()
        {
            foreach (var handle in _openedHandles)
            {
                var exception = Assert.Throws<PackageStoreAdmissionException>(() => inner.InspectHandle(handle));
                Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, exception.Reason);
            }
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
            => Track(inner.OpenNamespaceRoot(anchor));

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var parentIdentity = inner.InspectHandle(parent).Identity;
            if (_orderedRootIdentity == parentIdentity && singleName == RootMembershipRegistry.ControlDirectoryName)
                _events.Add("root-marker");
            var result = inner.InspectChildNoFollow(parent, singleName);
            AfterInspectChild?.Invoke(parent, singleName, result);
            return result;
        }

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(inner.OpenDirectoryChildNoFollow(parent, singleName));

        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
        {
            ParentOpenCount++;
            return Track(inner.OpenParentDirectory(directory));
        }

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => Track(inner.OpenFileChildNoFollow(parent, singleName, access));

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
        {
            LinkReadCount++;
            var parentIdentity = inner.InspectHandle(parent).Identity;
            if (_orderedRootIdentity == parentIdentity && _orderedLinkName == singleName)
                _events.Add($"link:{singleName}");
            var target = inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
            AfterLinkRead?.Invoke(parent, singleName, target);
            return target;
        }

        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);

        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            CreateCount++;
            return Track(inner.CreateDirectoryExclusiveAt(parent, singleName));
        }

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            CreateCount++;
            return Track(inner.CreateFileExclusiveAt(parent, singleName));
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (!ledgerIdentities.Contains(inner.InspectHandle(file).Identity))
                PackagePayloadReadCount++;
            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            WriteCount++;
            inner.WriteNewControlFile(file, contents);
        }

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            LockAttemptCount++;
            return inner.TryAcquireExclusiveLock(file);
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);

        private T Track<T>(T handle) where T : PhysicalStoreHandle
        {
            _openedHandles.Add(handle);
            return handle;
        }
    }
}
