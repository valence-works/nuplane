using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class PackageStoreAuthorityResolverTests
{
    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_ExistingAndAbsentLeaf_UsesMetadataWithoutCreatingParents()
    {
        using var context = new Context();

        using (var existing = context.ResolveStateSlotForWrite(context.Fixture.StateFilePath))
        {
            Assert.NotNull(existing.ExistingFileIdentity);
            Assert.Equal(Path.GetFileName(context.Fixture.StateFilePath), existing.RequestedBasename);
            existing.RevalidateRetainedParent();
        }
        context.AssertTrackedHandlesClosed();

        var absentPath = Path.Combine(context.Fixture.PackageInstallRoot, "absent-state.json");
        using (var absent = context.ResolveStateSlotForWrite(absentPath))
        {
            Assert.Null(absent.ExistingFileIdentity);
            Assert.Equal(Path.GetFileName(absentPath), absent.RequestedBasename);
            absent.RevalidateRetainedParent();
        }

        Assert.Equal(0, context.Observed.CreateCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.WriteCount);
        Assert.Equal(0, context.Observed.LockAttemptCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_NestedMissingParents_CreatesByHeldHandlesAndRetainsBothResolutions()
    {
        using var context = new Context();
        var statePath = Path.Combine(context.Fixture.RootPath, "new-state", "nested", "parent", "state.json");

        using (var resolved = context.ResolveStateSlotForWrite(statePath))
        {
            var parentPath = Path.GetDirectoryName(statePath)!;
            Assert.True(Directory.Exists(parentPath));
            Assert.Null(resolved.ExistingFileIdentity);
            Assert.Equal(3, context.Observed.CreateCount);
            Assert.Equal(context.Observed.InspectHandle(resolved.Parent).Identity, resolved.Slot.ParentIdentity);
            resolved.RevalidateRetainedParent();
        }

        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        Assert.Equal(0, context.Observed.WriteCount);
        Assert.Equal(0, context.Observed.LockAttemptCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_FinalLeafReplacement_DoesNotPinTheReplaceableFileIdentity()
    {
        using var context = new Context();
        PhysicalFileIdentity? oldIdentity;
        using (var resolved = context.ResolveStateSlotForWrite(context.Fixture.StateFilePath))
        {
            oldIdentity = resolved.ExistingFileIdentity;
            var replacementPath = context.Fixture.StateFilePath + ".replacement";
            File.WriteAllBytes(replacementPath, "replacement-state-bytes"u8.ToArray());
            File.Move(replacementPath, context.Fixture.StateFilePath, overwrite: true);

            resolved.RevalidateRetainedParent();
            Assert.Equal(oldIdentity, resolved.ExistingFileIdentity);
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        }
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_AliasRetargetToSameParent_RefusesOriginalEvidenceReplay()
    {
        using var context = new Context();
        var target = context.CreateDirectory("alias-target");
        var alias = Path.Combine(context.Fixture.RootPath, "state-parent-alias");
        var aliasTarget = Path.Combine(context.Fixture.RootPath, "state-parent-alias-target");
        Directory.CreateSymbolicLink(alias, target);
        Directory.CreateSymbolicLink(aliasTarget, target);
        var statePath = Path.Combine(alias, "created-parent", "state.json");
        using (var resolved = context.ResolveStateSlotForWrite(statePath))
        {
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, aliasTarget);

            Assert.Throws<PackageStoreAdmissionException>(() => resolved.RevalidateRetainedParent());
        }
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_ProfileChangeOrNewReservedAuthority_RefusesRetainedParent()
    {
        using (var context = new Context())
        {
            var statePath = Path.Combine(context.Fixture.RootPath, "profile-parent", "state.json");
            using (var resolved = context.ResolveStateSlotForWrite(statePath))
            {
                var original = context.Observed.ObserveDirectoryNameSemantics(resolved.Parent);
                context.Observed.NameSemanticsOverride = new PhysicalStoreNameSemantics(
                    "changed-profile-for-test", original.Encoding, original.CaseSensitive, original.NormalizationInsensitive);

                Assert.Throws<PackageStoreAdmissionException>(() => resolved.RevalidateRetainedParent());
            }
            context.Observed.NameSemanticsOverride = null;
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var statePath = Path.Combine(context.Fixture.RootPath, "authority-parent", "state.json");
            using (var resolved = context.ResolveStateSlotForWrite(statePath))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(statePath)!, RootMembershipRegistry.ControlDirectoryName));

                Assert.Throws<PackageStoreAdmissionException>(() => resolved.RevalidateRetainedParent());
            }
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            context.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_CompleteRootAuthority_RefusesMissingParentCreationBeforeCreate()
    {
        using var context = new Context();
        context.PublishCompleteCandidate(context.Fixture.PackageInstallRoot);
        var statePath = Path.Combine(context.Fixture.PackageInstallRoot, "new-state-parent", "state.json");

        Assert.Throws<PackageStoreAdmissionException>(() =>
        {
            using var unexpected = context.ResolveStateSlotForWrite(statePath);
        });

        Assert.False(Directory.Exists(Path.GetDirectoryName(statePath)));
        Assert.Equal(0, context.Observed.CreateCount);
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_ExactNativeDirectoryCollision_AdoptsOnlyOrdinaryDirectory()
    {
        using (var context = new Context())
        {
            var racedPath = Path.Combine(context.Fixture.RootPath, "raced-directory");
            context.Observed.BeforeCreateDirectory = (_, name) =>
            {
                if (name == "raced-directory")
                    Directory.CreateDirectory(racedPath);
            };

            using (var resolved = context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json")))
            {
                Assert.Equal(Path.GetFileName(racedPath), new DirectoryInfo(racedPath).Name);
                resolved.RevalidateRetainedParent();
            }

            Assert.Equal(1, context.Observed.CreateCount);
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var racedPath = Path.Combine(context.Fixture.RootPath, "raced-link");
            var targetPath = context.CreateDirectory("link-target");
            context.Observed.BeforeCreateDirectory = (_, name) =>
            {
                if (name == "raced-link")
                    Directory.CreateSymbolicLink(racedPath, targetPath);
            };

            Assert.Throws<PackageStoreAdmissionException>(() =>
            {
                using var unexpected = context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json"));
            });

            Assert.NotNull(new DirectoryInfo(racedPath).LinkTarget);
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var racedPath = Path.Combine(context.Fixture.RootPath, "raced-file");
            context.Observed.BeforeCreateDirectory = (_, name) =>
            {
                if (name == "raced-file")
                    File.WriteAllBytes(racedPath, "not-a-directory"u8.ToArray());
            };

            Assert.Throws<PackageStoreAdmissionException>(() =>
            {
                using var unexpected = context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json"));
            });

            Assert.True(File.Exists(racedPath));
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            context.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_EntryAppearingBeforeNativeCreate_RefusesWithoutTypedCollision()
    {
        using (var context = new Context())
        {
            var racedPath = Path.Combine(context.Fixture.RootPath, "occupied-during-preflight");
            var matchingInspections = 0;
            context.Observed.AfterInspectChild = (_, name, _) =>
            {
                if (name == "occupied-during-preflight" && ++matchingInspections == 1)
                    Directory.CreateDirectory(racedPath);
            };

            Assert.Throws<PackageStoreAdmissionException>(() =>
                context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json")));

            Assert.True(Directory.Exists(racedPath));
            Assert.Equal(0, context.Observed.CreateCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        {
            var racedPath = Path.Combine(context.Fixture.RootPath, "occupied-during-replay");
            var matchingInspections = 0;
            context.Observed.AfterInspectChild = (_, name, _) =>
            {
                if (name == "occupied-during-replay" && ++matchingInspections == 3)
                    Directory.CreateDirectory(racedPath);
            };

            Assert.Throws<PackageStoreAdmissionException>(() =>
                context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json")));

            Assert.True(Directory.Exists(racedPath));
            Assert.Equal(0, context.Observed.CreateCount);
            context.AssertTrackedHandlesClosed();
        }
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_UntypedCreateFailureAfterDirectoryAppears_PropagatesWithoutAdoption()
    {
        using var context = new Context();
        var racedPath = Path.Combine(context.Fixture.RootPath, "untyped-race");
        context.Observed.BeforeCreateDirectory = (_, name) =>
        {
            if (name != "untyped-race")
                return;
            Directory.CreateDirectory(racedPath);
            throw new IOException("Injected failure without a native collision result.");
        };

        var exception = Assert.Throws<IOException>(() =>
            context.ResolveStateSlotForWrite(Path.Combine(racedPath, "state.json")));

        Assert.Equal("Injected failure without a native collision result.", exception.Message);
        Assert.True(Directory.Exists(racedPath));
        Assert.Equal(0, context.Observed.PackagePayloadReadCount);
        context.AssertTrackedHandlesClosed();
    }

    [SupportedPhysicalStoreFact]
    public void ResolveStateSlotForWrite_PostCreateFailureAndCancellation_PreserveCreatedParentsAndUnwindHandles()
    {
        using (var context = new Context())
        {
            var parentPath = Path.Combine(context.Fixture.RootPath, "failed-after-create");
            context.Observed.AfterCreateDirectory = (_, name, _) =>
            {
                if (name == "failed-after-create")
                    throw new IOException("Injected post-create failure.");
            };

            var exception = Assert.Throws<IOException>(() =>
                context.ResolveStateSlotForWrite(Path.Combine(parentPath, "state.json")));

            Assert.Equal("Injected post-create failure.", exception.Message);
            Assert.True(Directory.Exists(parentPath));
            Assert.Equal(1, context.Observed.CreateCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        using (var cancellation = new CancellationTokenSource())
        {
            var first = Path.Combine(context.Fixture.RootPath, "created-before-cancel");
            var second = Path.Combine(first, "not-created-after-cancel");
            context.Observed.AfterCreateDirectory = (_, name, _) =>
            {
                if (name == "created-before-cancel")
                    cancellation.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() =>
                context.ResolveStateSlotForWrite(Path.Combine(second, "state.json"), cancellation.Token));

            Assert.True(Directory.Exists(first));
            Assert.False(Directory.Exists(second));
            Assert.Equal(1, context.Observed.CreateCount);
            Assert.Equal(0, context.Observed.PackagePayloadReadCount);
            context.AssertTrackedHandlesClosed();
        }

        using (var context = new Context())
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var missing = Path.Combine(context.Fixture.RootPath, "never-created", "state.json");

            Assert.Throws<OperationCanceledException>(() =>
                context.ResolveStateSlotForWrite(missing, cancellation.Token));

            Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
            Assert.Equal(0, context.Observed.CreateCount);
            context.AssertTrackedHandlesClosed();
        }
    }
}
