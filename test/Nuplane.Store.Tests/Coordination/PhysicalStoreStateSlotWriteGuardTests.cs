using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;
using Xunit.Abstractions;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreStateSlotWriteGuardTests
{
    private readonly ITestOutputHelper _output;

    public PhysicalStoreStateSlotWriteGuardTests(ITestOutputHelper output) => _output = output;

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_CreatesStableGuardThatContendsAcrossIndependentHandlesAndReleases()
    {
        using var context = CreateContext();
        using var secondParent = OpenStateParent(context.FileSystem, context.Fixture);
        var guardName = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);

        var first = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var originalInfo = RequireEntry(context, guardName);
        Assert.Equal(PhysicalStoreEntryKind.RegularFile, originalInfo.Kind);
        Assert.Equal(1UL, originalInfo.LinkCount);
        Assert.Equal(0L, originalInfo.Length);
        first.RequireGroupBindingMarkerAbsent();

        var busy = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => guard.AcquireAsync(secondParent, context.Slot, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, busy.Reason);
        Assert.Equal(originalInfo.Identity, RequireEntry(context, guardName).Identity);

        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var next = await guard.AcquireAsync(secondParent, context.Slot, CancellationToken.None);
        Assert.Equal(originalInfo.Identity, RequireEntry(context, guardName).Identity);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RefusesWrongParentAndNameProfileBeforeCreatingGuard()
    {
        using var context = CreateContext();
        using var otherParent = OpenOtherParent(context);
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);

        var wrongParent = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => guard.AcquireAsync(otherParent, context.Slot, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrongParent.Reason);
        Assert.Null(context.FileSystem.InspectChildNoFollow(otherParent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName));

        var wrongProfile = new StateSlotIdentity(
            context.Slot.ParentIdentity,
            new PhysicalStoreNameSemantics(
                context.Slot.NameSemantics.ProfileId + "-different",
                context.Slot.NameSemantics.Encoding,
                context.Slot.NameSemantics.CaseSensitive,
                context.Slot.NameSemantics.NormalizationInsensitive),
            context.Slot.CanonicalBasename);
        var profileError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => guard.AcquireAsync(context.Parent, wrongProfile, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, profileError.Reason);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName));
    }

    [SupportedPhysicalStoreFact]
    public async Task GetSlotDigest_BindsEveryExactStateSlotIdentityFieldAndSlotsHaveIndependentDirectories()
    {
        using var context = CreateContext();
        var slot = context.Slot;
        var baseline = PhysicalStoreStateSlotWriteGuard.GetSlotDigest(slot);
        var variants = new[]
        {
            new StateSlotIdentity(new PhysicalFileIdentity(slot.ParentIdentity.Provider + "x", slot.ParentIdentity.VolumeOrDeviceId, slot.ParentIdentity.FileId), slot.NameSemantics, slot.CanonicalBasename),
            new StateSlotIdentity(new PhysicalFileIdentity(slot.ParentIdentity.Provider, slot.ParentIdentity.VolumeOrDeviceId + "x", slot.ParentIdentity.FileId), slot.NameSemantics, slot.CanonicalBasename),
            new StateSlotIdentity(new PhysicalFileIdentity(slot.ParentIdentity.Provider, slot.ParentIdentity.VolumeOrDeviceId, slot.ParentIdentity.FileId + "x"), slot.NameSemantics, slot.CanonicalBasename),
            new StateSlotIdentity(slot.ParentIdentity, new PhysicalStoreNameSemantics(slot.NameSemantics.ProfileId + "x", slot.NameSemantics.Encoding, slot.NameSemantics.CaseSensitive, slot.NameSemantics.NormalizationInsensitive), slot.CanonicalBasename),
            new StateSlotIdentity(slot.ParentIdentity, new PhysicalStoreNameSemantics(slot.NameSemantics.ProfileId, slot.NameSemantics.Encoding == PhysicalStoreNameEncoding.Utf8 ? PhysicalStoreNameEncoding.Utf16LittleEndian : PhysicalStoreNameEncoding.Utf8, slot.NameSemantics.CaseSensitive, slot.NameSemantics.NormalizationInsensitive), slot.CanonicalBasename),
            new StateSlotIdentity(slot.ParentIdentity, new PhysicalStoreNameSemantics(slot.NameSemantics.ProfileId, slot.NameSemantics.Encoding, !slot.NameSemantics.CaseSensitive, slot.NameSemantics.NormalizationInsensitive), slot.CanonicalBasename),
            new StateSlotIdentity(slot.ParentIdentity, new PhysicalStoreNameSemantics(slot.NameSemantics.ProfileId, slot.NameSemantics.Encoding, slot.NameSemantics.CaseSensitive, !slot.NameSemantics.NormalizationInsensitive), slot.CanonicalBasename),
            new StateSlotIdentity(slot.ParentIdentity, slot.NameSemantics, slot.CanonicalBasename + ".other")
        };

        Assert.Equal(64, baseline.Length);
        Assert.All(variants, variant => Assert.NotEqual(baseline, PhysicalStoreStateSlotWriteGuard.GetSlotDigest(variant)));
        Assert.Equal("guard.lock", PhysicalStoreStateSlotWriteGuard.GuardLeafName);
        Assert.Equal("group.binding", PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName);

        await using var owner = await new PhysicalStoreStateSlotWriteGuard(context.FileSystem)
            .AcquireAsync(context.Parent, slot, CancellationToken.None);
        var secondSlot = CreateAdditionalSlot(context, "other-state.json");
        await using var independent = await new PhysicalStoreStateSlotWriteGuard(context.FileSystem)
            .AcquireAsync(context.Parent, secondSlot, CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_CleansUpNativeLockAfterCancellationAndKeepsStableControlTree()
    {
        using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        var wrapped = new DelegatingPhysicalStoreFileSystem(context.FileSystem)
        {
            AfterLockAcquired = cancellation.Cancel
        };
        var guard = new PhysicalStoreStateSlotWriteGuard(wrapped);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.AcquireAsync(context.Parent, context.Slot, cancellation.Token));

        var guardName = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
        Assert.Equal(0L, RequireEntry(context, guardName).Length);
        await using (var next = await new PhysicalStoreStateSlotWriteGuard(context.FileSystem)
                         .AcquireAsync(context.Parent, context.Slot, CancellationToken.None))
        {
            next.Revalidate();
        }

        var controlPath = context.Fixture.GetPath($"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}");
        var movedPath = context.Fixture.GetPath("state/canceled-control-tree-held");
        Directory.Move(controlPath, movedPath);
        Assert.True(Directory.Exists(movedPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RefusesUnsafeOccupiedGuardLeavesWithoutChangingThem()
    {
        using (var wrongKind = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
            using var slotDirectory = CreateSlotControlDirectory(wrongKind, wrongKind.Slot);
            using (wrongKind.FileSystem.CreateDirectoryExclusiveAt(slotDirectory, name))
            {
                await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(wrongKind.FileSystem)
                    .AcquireAsync(wrongKind.Parent, wrongKind.Slot, CancellationToken.None));
                Assert.Equal(PhysicalStoreEntryKind.Directory, RequireEntry(wrongKind, name).Kind);
            }
        }

        using (var nonEmpty = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
            using var slotDirectory = CreateSlotControlDirectory(nonEmpty, nonEmpty.Slot);
            using (var file = nonEmpty.FileSystem.CreateFileExclusiveAt(slotDirectory, name))
                nonEmpty.FileSystem.WriteNewControlFile(file, "collision"u8.ToArray());
            var before = File.ReadAllBytes(GetSlotControlPath(nonEmpty, nonEmpty.Slot, name));
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(nonEmpty.FileSystem)
                .AcquireAsync(nonEmpty.Parent, nonEmpty.Slot, CancellationToken.None));
            Assert.Equal(before, File.ReadAllBytes(GetSlotControlPath(nonEmpty, nonEmpty.Slot, name)));
        }

        using (var symbolic = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
            using var slotDirectory = CreateSlotControlDirectory(symbolic, symbolic.Slot);
            var target = GetSlotControlPath(symbolic, symbolic.Slot, "guard-target");
            File.WriteAllBytes(target, []);
            File.CreateSymbolicLink(GetSlotControlPath(symbolic, symbolic.Slot, name), target);
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(symbolic.FileSystem)
                .AcquireAsync(symbolic.Parent, symbolic.Slot, CancellationToken.None));
            Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, RequireEntry(symbolic, name).Kind);
            Assert.Empty(File.ReadAllBytes(target));
        }

        using (var hardlink = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
            var targetName = "guard-target";
            using var slotDirectory = CreateSlotControlDirectory(hardlink, hardlink.Slot);
            using (var file = hardlink.FileSystem.CreateFileExclusiveAt(slotDirectory, targetName))
                hardlink.FileSystem.WriteNewControlFile(file, ReadOnlyMemory<byte>.Empty);
            CreateHardLink(GetSlotControlPath(hardlink, hardlink.Slot, targetName), GetSlotControlPath(hardlink, hardlink.Slot, name));
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(hardlink.FileSystem)
                .AcquireAsync(hardlink.Parent, hardlink.Slot, CancellationToken.None));
            Assert.Equal(2UL, RequireEntry(hardlink, name).LinkCount);
        }

        using (var alias = CreateContext())
        {
            var semantics = ((IPhysicalStoreNameFileSystem)alias.FileSystem).ObserveDirectoryNameSemantics(alias.Parent);
            if (!semantics.CaseSensitive)
            {
                var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
                var aliasName = name.ToUpperInvariant();
                using var slotDirectory = CreateSlotControlDirectory(alias, alias.Slot);
                using (var file = alias.FileSystem.CreateFileExclusiveAt(slotDirectory, aliasName))
                    alias.FileSystem.WriteNewControlFile(file, ReadOnlyMemory<byte>.Empty);
                await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(alias.FileSystem)
                    .AcquireAsync(alias.Parent, alias.Slot, CancellationToken.None));
                Assert.NotNull(alias.FileSystem.InspectChildNoFollow(slotDirectory, aliasName));
            }
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RefusesUnsafeControlNamespaceAndSlotDirectoryEntries()
    {
        using (var namespaceFile = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.ControlDirectoryName;
            var path = namespaceFile.Fixture.GetPath($"state/{name}");
            File.WriteAllBytes(path, "preserve"u8.ToArray());
            var before = File.ReadAllBytes(path);
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(namespaceFile.FileSystem)
                .AcquireAsync(namespaceFile.Parent, namespaceFile.Slot, CancellationToken.None));
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        using (var slotFile = CreateContext())
        {
            using (var root = slotFile.FileSystem.CreateDirectoryExclusiveAt(
                       slotFile.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName))
            using (var file = slotFile.FileSystem.CreateFileExclusiveAt(root, slotFile.Slot.CanonicalBasename))
                slotFile.FileSystem.WriteNewControlFile(file, "preserve"u8.ToArray());
            var path = slotFile.Fixture.GetPath(
                $"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}/{slotFile.Slot.CanonicalBasename}");
            var before = File.ReadAllBytes(path);
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(slotFile.FileSystem)
                .AcquireAsync(slotFile.Parent, slotFile.Slot, CancellationToken.None));
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        using (var linkedRoot = CreateContext())
        {
            var target = linkedRoot.Fixture.CreateDirectory("control-target");
            var path = linkedRoot.Fixture.GetPath($"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}");
            Directory.CreateSymbolicLink(path, target);
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(linkedRoot.FileSystem)
                .AcquireAsync(linkedRoot.Parent, linkedRoot.Slot, CancellationToken.None));
            Assert.True(new DirectoryInfo(path).LinkTarget is not null);
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
    }

    [SupportedCaseInsensitivePhysicalStoreFact]
    public async Task AcquireAsync_RefusesNativeAliasOfReservedControlNamespace()
    {
        using (var occupiedAlias = CreateContext())
        {
            var exactName = PhysicalStoreStateSlotWriteGuard.ControlDirectoryName;
            var aliasName = exactName.ToUpperInvariant();
            using var aliasedDirectory = occupiedAlias.FileSystem.CreateDirectoryExclusiveAt(occupiedAlias.Parent, aliasName);
            var originalIdentity = occupiedAlias.FileSystem.InspectHandle(aliasedDirectory).Identity;

            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(occupiedAlias.FileSystem)
                .AcquireAsync(occupiedAlias.Parent, occupiedAlias.Slot, CancellationToken.None));

            var stillPresent = occupiedAlias.FileSystem.InspectChildNoFollow(occupiedAlias.Parent, exactName);
            Assert.NotNull(stillPresent);
            Assert.Equal(originalIdentity, stillPresent.Identity);
            var canonical = ((IPhysicalStoreDirectoryNameFileSystem)occupiedAlias.FileSystem)
                .ObserveCanonicalDirectoryNameNoFollow(occupiedAlias.Parent, exactName, originalIdentity);
            Assert.Equal(aliasName, canonical.Basename);
        }

        using (var reservedTargetAlias = CreateContext())
        {
            var semantics = ((IPhysicalStoreNameFileSystem)reservedTargetAlias.FileSystem)
                .ObserveDirectoryNameSemantics(reservedTargetAlias.Parent);
            var reservedSlot = new StateSlotIdentity(
                reservedTargetAlias.Slot.ParentIdentity, semantics,
                PhysicalStoreStateSlotWriteGuard.ControlDirectoryName.ToUpperInvariant());
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(reservedTargetAlias.FileSystem)
                .AcquireAsync(reservedTargetAlias.Parent, reservedSlot, CancellationToken.None));
            var controlPath = reservedTargetAlias.Fixture.GetPath(
                $"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}");
            Assert.Empty(Directory.GetFileSystemEntries(controlPath));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RefusesControlDirectoryProfileMismatchBeforeCreatingGuard()
    {
        using (var rootMismatch = CreateContext())
        {
            using var slotDirectory = CreateSlotControlDirectory(rootMismatch, rootMismatch.Slot);
            using var rootDirectory = rootMismatch.FileSystem.OpenDirectoryChildNoFollow(
                rootMismatch.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName);
            var wrapped = new DelegatingPhysicalStoreFileSystem(rootMismatch.FileSystem)
            {
                DifferentProfileForDirectoryIdentity = rootMismatch.FileSystem.InspectHandle(rootDirectory).Identity
            };
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(wrapped)
                .AcquireAsync(rootMismatch.Parent, rootMismatch.Slot, CancellationToken.None));
            Assert.Null(rootMismatch.FileSystem.InspectChildNoFollow(
                slotDirectory, PhysicalStoreStateSlotWriteGuard.GuardLeafName));
        }

        using (var slotMismatch = CreateContext())
        {
            using var slotDirectory = CreateSlotControlDirectory(slotMismatch, slotMismatch.Slot);
            var wrapped = new DelegatingPhysicalStoreFileSystem(slotMismatch.FileSystem)
            {
                DifferentProfileForDirectoryIdentity = slotMismatch.FileSystem.InspectHandle(slotDirectory).Identity
            };
            await AssertUnknownAsync(() => new PhysicalStoreStateSlotWriteGuard(wrapped)
                .AcquireAsync(slotMismatch.Parent, slotMismatch.Slot, CancellationToken.None));
            Assert.Null(slotMismatch.FileSystem.InspectChildNoFollow(
                slotDirectory, PhysicalStoreStateSlotWriteGuard.GuardLeafName));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task Lease_RevalidatesControlNamespaceAndSlotDirectoryReplacementOrWindowsPins()
    {
        using (var rootContext = CreateContext())
        {
            await using var owner = await new PhysicalStoreStateSlotWriteGuard(rootContext.FileSystem)
                .AcquireAsync(rootContext.Parent, rootContext.Slot, CancellationToken.None);
            var rootPath = rootContext.Fixture.GetPath($"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}");
            var movedPath = rootContext.Fixture.GetPath("state/control-namespace-held");
            var moved = TryMoveDirectory(rootPath, movedPath);
            if (OperatingSystem.IsWindows())
            {
                Assert.False(moved);
                owner.Revalidate();
            }
            else
            {
                Assert.True(moved);
                Directory.CreateDirectory(rootPath);
                var error = Assert.Throws<PackageStoreAdmissionException>(owner.Revalidate);
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            }
        }

        using (var slotContext = CreateContext())
        {
            await using var owner = await new PhysicalStoreStateSlotWriteGuard(slotContext.FileSystem)
                .AcquireAsync(slotContext.Parent, slotContext.Slot, CancellationToken.None);
            var rootPath = slotContext.Fixture.GetPath($"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}");
            var slotPath = Path.Combine(rootPath, slotContext.Slot.CanonicalBasename);
            var movedPath = Path.Combine(rootPath, slotContext.Slot.CanonicalBasename + ".held");
            var moved = TryMoveDirectory(slotPath, movedPath);
            if (OperatingSystem.IsWindows())
            {
                Assert.False(moved);
                owner.Revalidate();
            }
            else
            {
                Assert.True(moved);
                Directory.CreateDirectory(slotPath);
                var error = Assert.Throws<PackageStoreAdmissionException>(owner.Revalidate);
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            }
        }
    }

    [SupportedCaseInsensitivePhysicalStoreFact]
    public async Task ProspectiveNativeAliases_ContendForOneGuardAndRejectMarkerBoundToOtherExactSpelling()
    {
        using var context = CreateContext();
        var semantics = ((IPhysicalStoreNameFileSystem)context.FileSystem).ObserveDirectoryNameSemantics(context.Parent);
        var aliases = new List<(string First, string Second)>();
        if (!semantics.CaseSensitive)
            aliases.Add(("prospective-state.json", "PROSPECTIVE-STATE.JSON"));
        if (semantics.NormalizationInsensitive)
            aliases.Add(("prospective-state-é.json", "prospective-state-e\u0301.json"));
        Assert.NotEmpty(aliases);
        _output.WriteLine(
            $"Native profile {semantics.ProfileId}; case-sensitive={semantics.CaseSensitive}; normalization-insensitive={semantics.NormalizationInsensitive}; aliases={string.Join(" | ", aliases.Select(alias => $"{alias.First} -> {alias.Second}"))}");

        foreach (var (firstName, aliasName) in aliases)
        {
            var first = new StateSlotIdentity(context.Slot.ParentIdentity, semantics, firstName);
            var alias = new StateSlotIdentity(context.Slot.ParentIdentity, semantics, aliasName);
            Assert.NotEqual(first.CanonicalBasename, alias.CanonicalBasename);

            var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);
            var guardName = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
            var markerName = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
            PhysicalStoreGroupBindingMarker binding;
            await using (var firstOwner = await guard.AcquireAsync(context.Parent, first, CancellationToken.None))
            {
                binding = firstOwner.EnsureGroupBindingMarker(Guid.NewGuid(), new string('9', 64), CancellationToken.None);
                Assert.Equal(PhysicalStoreStateSlotWriteGuard.GetSlotDigest(first), binding.StateSlotIdentityDigest);
                var busy = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                    () => guard.AcquireAsync(context.Parent, alias, CancellationToken.None));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, busy.Reason);
                Assert.Equal(0L, RequireEntry(context, first, guardName).Length);
            }

            await using var aliasOwner = await guard.AcquireAsync(context.Parent, alias, CancellationToken.None);
            var wrongSlot = Assert.Throws<PackageStoreAdmissionException>(() => aliasOwner.ReadGroupBindingMarker());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrongSlot.Reason);
            var present = Assert.Throws<PackageStoreAdmissionException>(aliasOwner.RequireGroupBindingMarkerAbsent);
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, present.Reason);

            Assert.Equal(0L, RequireEntry(context, alias, guardName).Length);
            Assert.NotNull(RequireEntry(context, first, markerName));
            Assert.Equal(binding.StateSlotIdentityDigest, PhysicalStoreStateSlotWriteGuard.GetSlotDigest(first));
            Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, firstName));
            Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, aliasName));
        }
    }

    [SupportedCaseSensitivePhysicalStoreFact]
    public async Task DistinctNamesOnCaseSensitiveParent_KeepIndependentControlDirectories()
    {
        using var context = CreateContext();
        var semantics = ((IPhysicalStoreNameFileSystem)context.FileSystem).ObserveDirectoryNameSemantics(context.Parent);
        Assert.True(semantics.CaseSensitive);
        var first = new StateSlotIdentity(context.Slot.ParentIdentity, semantics, "separate-state.json");
        var second = new StateSlotIdentity(context.Slot.ParentIdentity, semantics, "SEPARATE-STATE.JSON");
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);

        await using var firstOwner = await guard.AcquireAsync(context.Parent, first, CancellationToken.None);
        await using var secondOwner = await guard.AcquireAsync(context.Parent, second, CancellationToken.None);
        Assert.Null(firstOwner.ReadGroupBindingMarker());
        Assert.Null(secondOwner.ReadGroupBindingMarker());
    }

    [SupportedPhysicalStoreFact]
    public async Task Lease_RevalidatesGuardIdentityReplacementAndExpiresAfterIdempotentDisposal()
    {
        using var context = CreateContext();
        var wrapped = new DelegatingPhysicalStoreFileSystem(context.FileSystem);
        var guard = new PhysicalStoreStateSlotWriteGuard(wrapped);
        var owner = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var name = PhysicalStoreStateSlotWriteGuard.GuardLeafName;
        wrapped.ReplaceObservedIdentityForName = name;

        var changed = Assert.Throws<PackageStoreAdmissionException>(owner.Revalidate);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, changed.Reason);

        wrapped.ReplaceObservedIdentityForName = null;
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        var expired = Assert.Throws<PackageStoreAdmissionException>(owner.Revalidate);
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task Lease_StaysBoundToHeldParentAcrossRenameOrUsesWindowsSharePin()
    {
        using var context = CreateContext();
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);
        await using var owner = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var originalPath = context.Fixture.GetPath("state");
        var movedPath = context.Fixture.GetPath("state-held");
        var renamed = false;
        try
        {
            Directory.Move(originalPath, movedPath);
            renamed = true;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // The Windows directory handle intentionally denies delete sharing while this owner is alive.
        }

        if (renamed)
            Directory.CreateDirectory(originalPath);

        var marker = owner.EnsureGroupBindingMarker(Guid.NewGuid(), new string('a', 64), CancellationToken.None);
        Assert.Equal(PhysicalStoreStateSlotWriteGuard.GetSlotDigest(context.Slot), marker.StateSlotIdentityDigest);
        var markerName = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
        var markerPath = Path.Combine(renamed ? movedPath : originalPath,
            PhysicalStoreStateSlotWriteGuard.ControlDirectoryName, context.Slot.CanonicalBasename, markerName);
        Assert.True(File.Exists(markerPath));
        if (renamed)
            Assert.False(File.Exists(Path.Combine(originalPath,
                PhysicalStoreStateSlotWriteGuard.ControlDirectoryName, context.Slot.CanonicalBasename, markerName)));
        owner.Revalidate();
    }

    [SupportedPhysicalStoreFact]
    public async Task EnsureGroupBindingMarker_IsDurableIdempotentAndRejectsConflictingBinding()
    {
        using var context = CreateContext();
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);
        var markerName = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
        var logicalMemberId = Guid.NewGuid();
        var participantDigest = new string('b', 64);
        PhysicalFileIdentity markerIdentity;

        await using (var owner = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None))
        {
            var created = owner.EnsureGroupBindingMarker(logicalMemberId, participantDigest, CancellationToken.None);
            markerIdentity = RequireEntry(context, markerName).Identity;
            Assert.Equal(PhysicalStoreStateSlotWriteGuard.GetSlotDigest(context.Slot), created.StateSlotIdentityDigest);
            Assert.Equal(logicalMemberId, created.LogicalMemberId);
            Assert.Equal(participantDigest, created.ParticipantSetDigest);
            Assert.Equal(created, owner.ReadGroupBindingMarker());
            Assert.Equal(created, owner.EnsureGroupBindingMarker(logicalMemberId, participantDigest, CancellationToken.None));
            Assert.Throws<PackageStoreAdmissionException>(owner.RequireGroupBindingMarkerAbsent);
            owner.Revalidate();

            var markerPath = GetSlotControlPath(context, context.Slot, markerName);
            var originalBytes = File.ReadAllBytes(markerPath);
            var conflict = Assert.Throws<PackageStoreAdmissionException>(() =>
                owner.EnsureGroupBindingMarker(Guid.NewGuid(), participantDigest, CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, conflict.Reason);
            Assert.Equal(originalBytes, File.ReadAllBytes(markerPath));
        }

        await using var reopenedOwner = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var reopened = reopenedOwner.ReadGroupBindingMarker();
        Assert.NotNull(reopened);
        Assert.Equal(logicalMemberId, reopened.LogicalMemberId);
        Assert.Equal(participantDigest, reopened.ParticipantSetDigest);
        Assert.Equal(markerIdentity, RequireEntry(context, markerName).Identity);
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadGroupBindingMarker_RefusesMalformedUnsupportedTruncatedAndWrongSlotBytes()
    {
        foreach (var bytes in InvalidMarkerPayloads())
        {
            using var context = CreateContext();
            var markerName = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
            using var slotDirectory = CreateSlotControlDirectory(context, context.Slot);
            using (var file = context.FileSystem.CreateFileExclusiveAt(slotDirectory, markerName))
                context.FileSystem.WriteNewControlFile(file, bytes);

            await using var owner = await new PhysicalStoreStateSlotWriteGuard(context.FileSystem)
                .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
            var error = Assert.Throws<PackageStoreAdmissionException>(() => owner.ReadGroupBindingMarker());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            Assert.Throws<PackageStoreAdmissionException>(owner.RequireGroupBindingMarkerAbsent);
            Assert.Equal(bytes, File.ReadAllBytes(GetSlotControlPath(context, context.Slot, markerName)));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadGroupBindingMarker_RefusesUnsafeMarkerEntries()
    {
        using (var directory = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
            using var slotDirectory = CreateSlotControlDirectory(directory, directory.Slot);
            using (directory.FileSystem.CreateDirectoryExclusiveAt(slotDirectory, name))
            await using (var owner = await new PhysicalStoreStateSlotWriteGuard(directory.FileSystem)
                             .AcquireAsync(directory.Parent, directory.Slot, CancellationToken.None))
            {
                var error = Assert.Throws<PackageStoreAdmissionException>(() => owner.ReadGroupBindingMarker());
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
                Assert.Equal(PhysicalStoreEntryKind.Directory, RequireEntry(directory, name).Kind);
            }
        }

        using (var symbolic = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
            using var slotDirectory = CreateSlotControlDirectory(symbolic, symbolic.Slot);
            var target = GetSlotControlPath(symbolic, symbolic.Slot, "marker-target");
            File.WriteAllBytes(target, []);
            File.CreateSymbolicLink(GetSlotControlPath(symbolic, symbolic.Slot, name), target);
            await using var owner = await new PhysicalStoreStateSlotWriteGuard(symbolic.FileSystem)
                .AcquireAsync(symbolic.Parent, symbolic.Slot, CancellationToken.None);
            var error = Assert.Throws<PackageStoreAdmissionException>(() => owner.ReadGroupBindingMarker());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, RequireEntry(symbolic, name).Kind);
            Assert.Empty(File.ReadAllBytes(target));
        }

        using (var hardlink = CreateContext())
        {
            var name = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
            var targetName = "marker-target";
            var valid = PhysicalStoreGroupBindingMarker.Encode(PhysicalStoreGroupBindingMarker.Create(
                PhysicalStoreStateSlotWriteGuard.GetSlotDigest(hardlink.Slot), Guid.NewGuid(), new string('c', 64)));
            using var slotDirectory = CreateSlotControlDirectory(hardlink, hardlink.Slot);
            using (var file = hardlink.FileSystem.CreateFileExclusiveAt(slotDirectory, targetName))
                hardlink.FileSystem.WriteNewControlFile(file, valid);
            CreateHardLink(GetSlotControlPath(hardlink, hardlink.Slot, targetName), GetSlotControlPath(hardlink, hardlink.Slot, name));

            await using var owner = await new PhysicalStoreStateSlotWriteGuard(hardlink.FileSystem)
                .AcquireAsync(hardlink.Parent, hardlink.Slot, CancellationToken.None);
            var error = Assert.Throws<PackageStoreAdmissionException>(() => owner.ReadGroupBindingMarker());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            Assert.Equal(2UL, RequireEntry(hardlink, name).LinkCount);
        }

    }

    [SupportedCaseInsensitivePhysicalStoreFact]
    public async Task ReadGroupBindingMarker_RefusesNativeAliasOfFixedMarkerLeaf()
    {
        using var context = CreateContext();
        var name = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
        var aliasName = name.ToUpperInvariant();
        var valid = PhysicalStoreGroupBindingMarker.Encode(PhysicalStoreGroupBindingMarker.Create(
            PhysicalStoreStateSlotWriteGuard.GetSlotDigest(context.Slot), Guid.NewGuid(), new string('d', 64)));
        using var slotDirectory = CreateSlotControlDirectory(context, context.Slot);
        using (var file = context.FileSystem.CreateFileExclusiveAt(slotDirectory, aliasName))
            context.FileSystem.WriteNewControlFile(file, valid);

        await using var owner = await new PhysicalStoreStateSlotWriteGuard(context.FileSystem)
            .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var error = Assert.Throws<PackageStoreAdmissionException>(() => owner.ReadGroupBindingMarker());
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(valid, File.ReadAllBytes(GetSlotControlPath(context, context.Slot, aliasName)));
        Assert.NotNull(context.FileSystem.InspectChildNoFollow(slotDirectory, aliasName));
    }

    [SupportedPhysicalStoreFact]
    public async Task Revalidate_RefusesReplacementMarkerIdentityEvenWhenBytesMatch()
    {
        using var context = CreateContext();
        var guard = new PhysicalStoreStateSlotWriteGuard(context.FileSystem);
        await using var owner = await guard.AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        var name = PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName;
        var marker = owner.EnsureGroupBindingMarker(Guid.NewGuid(), new string('e', 64), CancellationToken.None);
        var originalIdentity = RequireEntry(context, name).Identity;
        var originalPath = GetSlotControlPath(context, context.Slot, name);
        var savedPath = GetSlotControlPath(context, context.Slot, name + ".saved");
        var bytes = File.ReadAllBytes(originalPath);

        File.Move(originalPath, savedPath);
        File.WriteAllBytes(originalPath, bytes);
        Assert.NotEqual(originalIdentity, RequireEntry(context, name).Identity);

        var error = Assert.Throws<PackageStoreAdmissionException>(owner.Revalidate);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(marker.StateSlotIdentityDigest, PhysicalStoreStateSlotWriteGuard.GetSlotDigest(context.Slot));
        Assert.Equal(bytes, File.ReadAllBytes(originalPath));
    }

    private static IEnumerable<byte[]> InvalidMarkerPayloads()
    {
        yield return [];
        yield return "truncated"u8.ToArray();

        var valid = PhysicalStoreGroupBindingMarker.Encode(PhysicalStoreGroupBindingMarker.Create(
            new string('f', 64), Guid.NewGuid(), new string('0', 64)));
        var unsupportedVersion = valid.ToArray();
        var versionOffset = Encoding.ASCII.GetByteCount("NUPLANE-STATE-GROUP\0");
        BinaryPrimitives.WriteInt32BigEndian(unsupportedVersion.AsSpan(versionOffset, sizeof(int)), 2);
        yield return unsupportedVersion;
        yield return [.. valid, 0];

        var contextSlot = new string('1', 64);
        yield return PhysicalStoreGroupBindingMarker.Encode(PhysicalStoreGroupBindingMarker.Create(
            contextSlot, Guid.NewGuid(), new string('0', 64)));
    }

    private static StoreContext CreateContext()
    {
        var fixture = new PackageStoreFixture();
        IPhysicalStoreFileSystem files = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        PhysicalStoreDirectoryHandle? parent = null;
        try
        {
            using var root = PhysicalStoreTestDirectory.Open(files, fixture.RootPath);
            parent = files.OpenDirectoryChildNoFollow(root, "state");
            using (var state = files.CreateFileExclusiveAt(parent, "store-state.json"))
                files.WriteNewControlFile(state, "state"u8.ToArray());
            var slot = new PhysicalStoreIdentity(files).ObserveStateSlot(parent, "store-state.json").Slot;
            var context = new StoreContext(fixture, files, parent, slot);
            parent = null;
            return context;
        }
        catch
        {
            parent?.Dispose();
            fixture.Dispose();
            throw;
        }
    }

    private static PhysicalStoreDirectoryHandle OpenStateParent(IPhysicalStoreFileSystem files, PackageStoreFixture fixture)
    {
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.RootPath);
        return files.OpenDirectoryChildNoFollow(root, "state");
    }

    private static PhysicalStoreDirectoryHandle OpenOtherParent(StoreContext context)
    {
        var path = context.Fixture.CreateDirectory("other");
        return PhysicalStoreTestDirectory.Open(context.FileSystem, path);
    }

    private static StateSlotIdentity CreateAdditionalSlot(StoreContext context, string basename)
    {
        using (var file = context.FileSystem.CreateFileExclusiveAt(context.Parent, basename))
            context.FileSystem.WriteNewControlFile(file, "another-state"u8.ToArray());
        return new PhysicalStoreIdentity(context.FileSystem).ObserveStateSlot(context.Parent, basename).Slot;
    }

    private static PhysicalStoreEntryInfo RequireEntry(StoreContext context, string name)
        => RequireEntry(context, context.Slot, name);

    private static PhysicalStoreEntryInfo RequireEntry(StoreContext context, StateSlotIdentity slot, string name)
    {
        using var slotDirectory = OpenSlotControlDirectory(context, slot);
        return context.FileSystem.InspectChildNoFollow(slotDirectory, name)
           ?? throw new Xunit.Sdk.XunitException($"Expected the owned state-slot fixture '{name}' to exist.");
    }

    private static PhysicalStoreDirectoryHandle CreateSlotControlDirectory(StoreContext context, StateSlotIdentity slot)
    {
        var rootInfo = context.FileSystem.InspectChildNoFollow(context.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName);
        using var root = rootInfo is null
            ? context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName)
            : context.FileSystem.OpenDirectoryChildNoFollow(context.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName);
        var slotInfo = context.FileSystem.InspectChildNoFollow(root, slot.CanonicalBasename);
        return slotInfo is null
            ? context.FileSystem.CreateDirectoryExclusiveAt(root, slot.CanonicalBasename)
            : context.FileSystem.OpenDirectoryChildNoFollow(root, slot.CanonicalBasename);
    }

    private static PhysicalStoreDirectoryHandle OpenSlotControlDirectory(StoreContext context, StateSlotIdentity slot)
    {
        using var root = context.FileSystem.OpenDirectoryChildNoFollow(
            context.Parent, PhysicalStoreStateSlotWriteGuard.ControlDirectoryName);
        return context.FileSystem.OpenDirectoryChildNoFollow(root, slot.CanonicalBasename);
    }

    private static string GetSlotControlPath(StoreContext context, StateSlotIdentity slot, string name)
        => context.Fixture.GetPath($"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}/{slot.CanonicalBasename}/{name}");

    private static bool TryMoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.Move(sourcePath, destinationPath);
            return true;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    private static async Task AssertUnknownAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(action);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var created = OperatingSystem.IsWindows()
            ? WindowsCreateHardLink(newPath, existingPath, IntPtr.Zero)
            : (OperatingSystem.IsMacOS() ? DarwinLink(existingPath, newPath) : LinuxLink(existingPath, newPath)) == 0;
        if (!created)
            throw new IOException($"The owned hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int DarwinLink(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxLink(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsCreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private sealed class StoreContext(
        PackageStoreFixture fixture,
        IPhysicalStoreFileSystem fileSystem,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot) : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = fixture;
        internal IPhysicalStoreFileSystem FileSystem { get; } = fileSystem;
        internal PhysicalStoreDirectoryHandle Parent { get; } = parent;
        internal StateSlotIdentity Slot { get; } = slot;

        public void Dispose()
        {
            try
            {
                Parent.Dispose();
            }
            finally
            {
                Fixture.Dispose();
            }
        }
    }

    private sealed class DelegatingPhysicalStoreFileSystem :
        IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryNameFileSystem
    {
        private readonly IPhysicalStoreFileSystem _inner;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStoreDirectoryNameFileSystem _directoryNames;

        internal DelegatingPhysicalStoreFileSystem(IPhysicalStoreFileSystem inner)
        {
            _inner = inner;
            _names = inner as IPhysicalStoreNameFileSystem ?? throw new InvalidOperationException("The test provider has no native name support.");
            _directoryNames = inner as IPhysicalStoreDirectoryNameFileSystem ?? throw new InvalidOperationException("The test provider has no native directory-name support.");
        }

        internal Action? AfterLockAcquired { get; set; }
        internal string? ReplaceObservedIdentityForName { get; set; }
        internal PhysicalFileIdentity? DifferentProfileForDirectoryIdentity { get; set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var info = _inner.InspectChildNoFollow(parent, singleName);
            if (!string.Equals(ReplaceObservedIdentityForName, singleName, StringComparison.Ordinal) || info is null)
                return info;
            var replacement = new PhysicalFileIdentity(info.Identity.Provider, info.Identity.VolumeOrDeviceId,
                info.Identity.FileId + "-observed-replacement");
            return new PhysicalStoreEntryInfo(info.Kind, replacement, info.LinkCount, info.Length, info.ReparseTag);
        }
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => _inner.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => _inner.OpenFileChildNoFollow(parent, singleName, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => _inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => _inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.CreateFileExclusiveAt(parent, singleName);
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes) => _inner.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => _inner.WriteNewControlFile(file, contents);

        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var owner = await _inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (owner is not null)
                AfterLockAcquired?.Invoke();
            return owner;
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
        {
            var semantics = _names.ObserveDirectoryNameSemantics(parent);
            if (DifferentProfileForDirectoryIdentity is { } identity &&
                _inner.InspectHandle(parent).Identity == identity)
            {
                return new PhysicalStoreNameSemantics(
                    semantics.ProfileId + "-different", semantics.Encoding,
                    semantics.CaseSensitive, semantics.NormalizationInsensitive);
            }

            return semantics;
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);

        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryNames.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
    }
}

public sealed class SupportedCaseInsensitivePhysicalStoreFactAttribute : FactAttribute
{
    public SupportedCaseInsensitivePhysicalStoreFactAttribute()
    {
        if (!UnixPhysicalStoreFileSystem.IsSupportedPlatform && !WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Physical store operations are qualified only on Darwin arm64, Linux x64/arm64, and Windows x64.";
        else if (OperatingSystem.IsLinux() &&
                 !string.Equals(Environment.GetEnvironmentVariable("NUPLANE_REQUIRE_EXT4_CASEFOLD"), "1", StringComparison.Ordinal))
            Skip = "Native case-alias proof requires a case-insensitive volume or the dedicated ext4 casefold lane.";
    }
}

public sealed class SupportedCaseSensitivePhysicalStoreFactAttribute : FactAttribute
{
    public SupportedCaseSensitivePhysicalStoreFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !UnixPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Case-sensitive alias proof is qualified only on the Linux x64/arm64 lane.";
        else if (string.Equals(Environment.GetEnvironmentVariable("NUPLANE_REQUIRE_EXT4_CASEFOLD"), "1", StringComparison.Ordinal))
            Skip = "This lane requires a case-insensitive ext4 casefold volume.";
    }
}
