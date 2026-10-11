using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

internal sealed class PhysicalStorePublicationTestContext : IDisposable
{
    internal PhysicalStorePublicationTestContext(
        PackageStoreFixture fixture,
        IPhysicalStoreFileSystem fileSystem,
        IPhysicalStorePublicationFileSystem publication,
        PhysicalStoreDirectoryHandle parent)
    {
        Fixture = fixture;
        FileSystem = fileSystem;
        Publication = publication;
        Parent = parent;
        ParentPath = fixture.GetPath("parent");
    }

    internal PackageStoreFixture Fixture { get; }
    internal IPhysicalStoreFileSystem FileSystem { get; }
    internal IPhysicalStorePublicationFileSystem Publication { get; }
    internal PhysicalStoreDirectoryHandle Parent { get; }
    internal string ParentPath { get; }

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

internal static class PhysicalStorePublicationTestCases
{
    private static readonly byte[] PriorContents = "prior-state"u8.ToArray();
    private static readonly byte[] NextContents = "next-state"u8.ToArray();

    internal static void ReplacesExistingSlotAndPreservesItsIdentity(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "state.json", PriorContents);
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));
        var slotBefore = new PhysicalStoreIdentity(context.FileSystem).ObserveStateSlot(context.Parent, "state.json").Slot;
        var stagedIdentity = CreateFile(context, "state.next", NextContents);

        var result = context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", priorIdentity);

        Assert.Equal(PhysicalStoreEntryKind.RegularFile, result.Kind);
        Assert.Equal(stagedIdentity, result.Identity);
        Assert.NotEqual(priorIdentity, result.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "state.next"));
        Assert.Equal(NextContents, ReadFile(context, "state.json"));
        var slotAfter = new PhysicalStoreIdentity(context.FileSystem).ObserveStateSlot(context.Parent, "state.json");
        Assert.Equal(slotBefore, slotAfter.Slot);
        Assert.Equal(stagedIdentity, slotAfter.FileIdentity);
    }

    internal static void PublishesToAbsentSlotWithoutReplacingAnything(PhysicalStorePublicationTestContext context)
    {
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "state.json"));
        var stagedIdentity = CreateFile(context, "state.next", NextContents);

        var result = context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", expectedDestinationIdentity: null);

        Assert.Equal(PhysicalStoreEntryKind.RegularFile, result.Kind);
        Assert.Equal(stagedIdentity, result.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "state.next"));
        Assert.Equal(NextContents, ReadFile(context, "state.json"));
        var slot = new PhysicalStoreIdentity(context.FileSystem).ObserveStateSlot(context.Parent, "state.json");
        Assert.Equal(stagedIdentity, slot.FileIdentity);
    }

    internal static void AbsentPublicationRefusesOccupiedTargetAndPreservesBothFiles(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "state.json", PriorContents);
        var stagedIdentity = CreateFile(context, "state.next", NextContents);

        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", expectedDestinationIdentity: null));

        Assert.Equal(priorIdentity, Info(context, "state.json").Identity);
        Assert.Equal(stagedIdentity, Info(context, "state.next").Identity);
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));
        Assert.Equal(NextContents, ReadFile(context, "state.next"));
    }

    internal static void StaleSourceAndDestinationIdentitiesRefuseWithoutMutation(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "state.json", PriorContents);
        var staleStageIdentity = CreateFile(context, "stale-source", "old-stage"u8.ToArray());
        var currentStageIdentity = CreateFile(context, "state.next", NextContents);

        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", staleStageIdentity, "state.json", priorIdentity));
        Assert.Equal(currentStageIdentity, Info(context, "state.next").Identity);
        Assert.Equal("old-stage"u8.ToArray(), ReadFile(context, "stale-source"));
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));

        var staleDestinationIdentity = CreateFile(context, "stale-destination", "old-destination"u8.ToArray());
        var currentDestinationIdentity = priorIdentity;
        var secondStageIdentity = CreateFile(context, "state.next-2", NextContents);

        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next-2", secondStageIdentity, "state.json", staleDestinationIdentity));
        Assert.Equal(currentDestinationIdentity, Info(context, "state.json").Identity);
        Assert.Equal(secondStageIdentity, Info(context, "state.next-2").Identity);
        Assert.Equal("old-destination"u8.ToArray(), ReadFile(context, "stale-destination"));
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));
        Assert.Equal(NextContents, ReadFile(context, "state.next-2"));
    }

    internal static void LinkEntriesAreRefusedForPublishAndRemove(PhysicalStorePublicationTestContext context)
    {
        CreateFile(context, "target", PriorContents);
        CreateFile(context, "state.json", PriorContents);
        File.CreateSymbolicLink(context.Fixture.GetPath("parent/state.next"), "target");
        var symlinkStageIdentity = Info(context, "state.next").Identity;
        var destinationIdentity = Info(context, "state.json").Identity;

        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", symlinkStageIdentity, "state.json", destinationIdentity));
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, Info(context, "state.next").Kind);

        File.Delete(Path.Combine(context.ParentPath, "state.next"));
        CreateFile(context, "state.next", NextContents);
        File.Delete(context.Fixture.GetPath("parent/state.json"));
        File.CreateSymbolicLink(context.Fixture.GetPath("parent/state.json"), "target");
        var symlinkDestinationIdentity = Info(context, "state.json").Identity;
        var ordinaryStageIdentity = Info(context, "state.next").Identity;
        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", ordinaryStageIdentity, "state.json", symlinkDestinationIdentity));
        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "state.json", symlinkDestinationIdentity));
        Assert.Equal(NextContents, ReadFile(context, "state.next"));
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, Info(context, "state.json").Kind);
        Assert.Equal(PriorContents, File.ReadAllBytes(context.Fixture.GetPath("parent/target")));

        File.CreateSymbolicLink(context.Fixture.GetPath("parent/artifact-link"), "target");
        var artifactLinkIdentity = Info(context, "artifact-link").Identity;
        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "artifact-link", artifactLinkIdentity));
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, Info(context, "artifact-link").Kind);
        Assert.Equal(PriorContents, File.ReadAllBytes(context.Fixture.GetPath("parent/target")));
    }

    internal static void HardLinkedEntriesAreRefusedForPublishAndRemove(
        PhysicalStorePublicationTestContext context,
        Action<string, string> createHardLink)
    {
        CreateFile(context, "target", PriorContents);
        CreateFile(context, "stage-target", NextContents);
        createHardLink(context.Fixture.GetPath("parent/stage-target"), context.Fixture.GetPath("parent/state.next"));
        CreateFile(context, "state.json", PriorContents);
        var stagedIdentity = Info(context, "state.next").Identity;
        var destinationIdentity = Info(context, "state.json").Identity;

        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", destinationIdentity));
        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "state.next", stagedIdentity));

        Assert.Equal(2UL, Info(context, "state.next").LinkCount);
        Assert.Equal(NextContents, File.ReadAllBytes(context.Fixture.GetPath("parent/state.next")));
        Assert.Equal(PriorContents, File.ReadAllBytes(context.Fixture.GetPath("parent/target")));
        Assert.Equal(PriorContents, ReadFile(context, "state.json"));

        File.Delete(context.Fixture.GetPath("parent/state.next"));
        CreateFile(context, "state.next", NextContents);
        File.Delete(context.Fixture.GetPath("parent/state.json"));
        createHardLink(context.Fixture.GetPath("parent/target"), context.Fixture.GetPath("parent/state.json"));
        var ordinaryStageIdentity = Info(context, "state.next").Identity;
        var hardLinkedDestinationIdentity = Info(context, "state.json").Identity;
        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", ordinaryStageIdentity, "state.json", hardLinkedDestinationIdentity));
        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "state.json", hardLinkedDestinationIdentity));

        Assert.Equal(2UL, Info(context, "state.json").LinkCount);
        Assert.Equal(NextContents, ReadFile(context, "state.next"));
        Assert.Equal(PriorContents, File.ReadAllBytes(context.Fixture.GetPath("parent/target")));
    }

    internal static void RemovalRequiresExactRegularSingleLinkIdentity(PhysicalStorePublicationTestContext context)
    {
        var artifactIdentity = CreateFile(context, "artifact", NextContents);
        var wrongIdentity = new PhysicalFileIdentity(
            artifactIdentity.Provider,
            artifactIdentity.VolumeOrDeviceId,
            artifactIdentity.FileId + "-wrong");

        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "artifact", wrongIdentity));
        AssertRefused(() => context.Publication.RemoveControlFileAt(
            context.Parent, "missing", artifactIdentity));

        using (var directory = context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, "directory-artifact"))
        {
            var directoryIdentity = context.FileSystem.InspectHandle(directory).Identity;
            AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "directory-artifact", directoryIdentity));
        }

        Assert.Equal(artifactIdentity, Info(context, "artifact").Identity);
        Assert.Equal(NextContents, ReadFile(context, "artifact"));
        Assert.Equal(PhysicalStoreEntryKind.Directory, Info(context, "directory-artifact").Kind);
        context.Publication.RemoveControlFileAt(context.Parent, "artifact", artifactIdentity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "artifact"));
    }

    internal static void RefusesAliasSpellingAndForeignOrClosedParent(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "State.JSON", PriorContents);
        var stagedIdentity = CreateFile(context, "state.next", NextContents);
        var spelling = ((IPhysicalStoreNameFileSystem)context.FileSystem)
            .ObserveCanonicalFileNameNoFollow(context.Parent, "State.JSON", priorIdentity);
        var alternate = context.FileSystem.InspectChildNoFollow(context.Parent, "state.json");
        if (spelling.Semantics.CaseSensitive)
            Assert.Null(alternate); // A distinct absent slot cannot satisfy the supplied prior identity.
        else
            Assert.Equal(priorIdentity, Assert.IsType<PhysicalStoreEntryInfo>(alternate).Identity);
        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", priorIdentity));
        Assert.Equal(PriorContents, ReadFile(context, "State.JSON"));
        Assert.Equal(NextContents, ReadFile(context, "state.next"));

        var foreign = CreateForeignAdapter(context);
        AssertRefused(() => foreign.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "State.JSON", priorIdentity));
        AssertRefused(() => foreign.Publication.RemoveControlFileAt(context.Parent, "State.JSON", priorIdentity));

        context.Parent.Dispose();
        AssertRefused(() => context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "State.JSON", priorIdentity));
        AssertRefused(() => context.Publication.RemoveControlFileAt(context.Parent, "State.JSON", priorIdentity));
        Assert.Equal(PriorContents, File.ReadAllBytes(context.Fixture.GetPath("parent/State.JSON")));
        Assert.Equal(NextContents, File.ReadAllBytes(context.Fixture.GetPath("parent/state.next")));
    }

    internal static void UnixHeldParentPublishesIntoOriginalDirectoryAfterTextualReplacement(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "state.json", PriorContents);
        var stagedIdentity = CreateFile(context, "state.next", NextContents);
        var movedPath = context.Fixture.GetPath("parent-moved");

        Directory.Move(context.ParentPath, movedPath);
        Directory.CreateDirectory(context.ParentPath);
        File.WriteAllBytes(context.Fixture.GetPath("parent/state.json"), "replacement-parent"u8.ToArray());

        var result = context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", priorIdentity);

        Assert.Equal(stagedIdentity, result.Identity);
        Assert.Equal(NextContents, File.ReadAllBytes(Path.Combine(movedPath, "state.json")));
        Assert.Equal("replacement-parent"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/state.json")));
    }

    internal static void WindowsHeldParentPreventsTextualRename(PhysicalStorePublicationTestContext context)
    {
        var priorIdentity = CreateFile(context, "state.json", PriorContents);
        var stagedIdentity = CreateFile(context, "state.next", NextContents);
        var movedPath = context.Fixture.GetPath("parent-moved");

        var renameError = Record.Exception(() => Directory.Move(context.ParentPath, movedPath));
        Assert.True(renameError is IOException or UnauthorizedAccessException, "The held Windows parent should refuse a textual rename.");

        var result = context.Publication.PublishControlFileAt(
            context.Parent, "state.next", stagedIdentity, "state.json", priorIdentity);
        Assert.Equal(stagedIdentity, result.Identity);
        Assert.Equal(NextContents, ReadFile(context, "state.json"));
    }

    private static (IPhysicalStoreFileSystem FileSystem, IPhysicalStorePublicationFileSystem Publication) CreateForeignAdapter(
        PhysicalStorePublicationTestContext context)
    {
        if (context.FileSystem is UnixPhysicalStoreFileSystem)
        {
            var adapter = new UnixPhysicalStoreFileSystem();
            return (adapter, adapter);
        }

        var windows = new WindowsPhysicalStoreFileSystem();
        return (windows, windows);
    }

    private static PhysicalFileIdentity CreateFile(PhysicalStorePublicationTestContext context, string name, byte[] contents)
    {
        using var file = context.FileSystem.CreateFileExclusiveAt(context.Parent, name);
        context.FileSystem.WriteNewControlFile(file, contents);
        return context.FileSystem.InspectHandle(file).Identity;
    }

    private static PhysicalStoreEntryInfo Info(PhysicalStorePublicationTestContext context, string name)
        => context.FileSystem.InspectChildNoFollow(context.Parent, name)
           ?? throw new Xunit.Sdk.XunitException($"Expected owned fixture entry '{name}' to exist.");

    private static byte[] ReadFile(PhysicalStorePublicationTestContext context, string name)
    {
        using var file = context.FileSystem.OpenFileChildNoFollow(context.Parent, name, FileAccess.Read);
        return context.FileSystem.ReadControlFile(file, 4096);
    }

    private static void AssertRefused(Action action)
        => Assert.Throws<PackageStoreAdmissionException>(action);
}
