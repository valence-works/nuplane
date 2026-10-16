using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class UnixPhysicalStoreStateFileTests
{
    [SupportedUnixFact] public void Read_HardlinkKeepsSelectedEntryBinding() => PhysicalStoreStateFileTestCases.ReadHardlink(static () => UnixPhysicalStorePublicationTests.CreateContext(), UnixPhysicalStorePublicationTests.CreateHardLink);
    [SupportedUnixFact] public void Read_RefusesLinksChangedEntriesAndQuota() => PhysicalStoreStateFileTestCases.ReadRefusals(static () => UnixPhysicalStorePublicationTests.CreateContext());
    [SupportedUnixFact] public void Read_StreamRetainsNativeParentAndFile() => PhysicalStoreStateFileTestCases.RetainedHandles(static () => UnixPhysicalStorePublicationTests.CreateContext());
    [SupportedUnixFact] public void Publish_HardlinkReplacesOnlySelectedEntry() => PhysicalStoreStateFileTestCases.PublishHardlink(static () => UnixPhysicalStorePublicationTests.CreateContext(), UnixPhysicalStorePublicationTests.CreateHardLink);
    [SupportedUnixFact] public void Publish_SymlinkReplacesOnlyLexicalEntry() => PhysicalStoreStateFileTestCases.PublishSymlink(static () => UnixPhysicalStorePublicationTests.CreateContext());
    [SupportedUnixFact] public void Publish_RefusesWrongPriorAndOccupiedAbsentSlot() => PhysicalStoreStateFileTestCases.PublicationRefusals(static () => UnixPhysicalStorePublicationTests.CreateContext());
    [SupportedUnixFact] public Task LegacySave_HardlinkReplacesOnlySelectedEntry() => PhysicalStoreStateFileTestCases.LegacyHardlink(static () => UnixPhysicalStorePublicationTests.CreateContext(), UnixPhysicalStorePublicationTests.CreateHardLink);
    [SupportedUnixFact] public Task LegacySave_DirectSymbolicLinkReplacesOnlyLexicalEntry() => PhysicalStoreStateFileTestCases.LegacySymbolicLink(static () => UnixPhysicalStorePublicationTests.CreateContext(), PhysicalStoreStateFileTestCases.LegacySymbolicLinkExpectation.ReplacesLexicalEntry);
    [SupportedUnixFact] public Task LegacySave_TwoLinkChainReplacesOnlyLexicalEntry() => PhysicalStoreStateFileTestCases.LegacySymbolicLink(static () => UnixPhysicalStorePublicationTests.CreateContext(), PhysicalStoreStateFileTestCases.LegacySymbolicLinkExpectation.ReplacesLexicalEntry, includeIntermediateLink: true);
    [SupportedUnixFact] public Task LegacySave_InvalidReferentsPreserveOriginalEntries() => PhysicalStoreStateFileTestCases.LegacyInvalidReferents(static () => UnixPhysicalStorePublicationTests.CreateContext());
}

public sealed class WindowsPhysicalStoreStateFileTests
{
    [SupportedWindowsFact] public void Read_HardlinkKeepsSelectedEntryBinding() => PhysicalStoreStateFileTestCases.ReadHardlink(WindowsPhysicalStorePublicationTests.CreateContext, WindowsPhysicalStorePublicationTests.CreateHardLink);
    [SupportedWindowsFact] public void Read_RefusesLinksChangedEntriesAndQuota() => PhysicalStoreStateFileTestCases.ReadRefusals(WindowsPhysicalStorePublicationTests.CreateContext);
    [SupportedWindowsFact] public void Read_StreamRetainsNativeParentAndFile() => PhysicalStoreStateFileTestCases.RetainedHandles(WindowsPhysicalStorePublicationTests.CreateContext);
    [SupportedWindowsFact] public void Publish_HardlinkReplacesOnlySelectedEntry() => PhysicalStoreStateFileTestCases.PublishHardlink(WindowsPhysicalStorePublicationTests.CreateContext, WindowsPhysicalStorePublicationTests.CreateHardLink);
    [SupportedWindowsFact] public void Publish_SymlinkReplacesOnlyLexicalEntry() => PhysicalStoreStateFileTestCases.PublishSymlink(WindowsPhysicalStorePublicationTests.CreateContext);
    [SupportedWindowsFact] public void Publish_RefusesWrongPriorAndOccupiedAbsentSlot() => PhysicalStoreStateFileTestCases.PublicationRefusals(WindowsPhysicalStorePublicationTests.CreateContext);
    [SupportedWindowsFact] public Task LegacySave_HardlinkReplacesOnlySelectedEntry() => PhysicalStoreStateFileTestCases.LegacyHardlink(WindowsPhysicalStorePublicationTests.CreateContext, WindowsPhysicalStorePublicationTests.CreateHardLink);
    [SupportedWindowsFact] public Task LegacySave_DirectSymbolicLinkRefusesReplacementAndPreservesEntries() => PhysicalStoreStateFileTestCases.LegacySymbolicLink(WindowsPhysicalStorePublicationTests.CreateContext, PhysicalStoreStateFileTestCases.LegacySymbolicLinkExpectation.RefusesReplacementAndPreservesEntries);
    [SupportedWindowsFact] public Task LegacySave_TwoLinkChainRefusesUnsupportedSymbolicLinkReplacement() => PhysicalStoreStateFileTestCases.LegacySymbolicLink(WindowsPhysicalStorePublicationTests.CreateContext, PhysicalStoreStateFileTestCases.LegacySymbolicLinkExpectation.RefusesReplacementAndPreservesEntries, includeIntermediateLink: true);
    [SupportedWindowsFact] public Task LegacySave_InvalidReferentsPreserveOriginalEntries() => PhysicalStoreStateFileTestCases.LegacyInvalidReferents(WindowsPhysicalStorePublicationTests.CreateContext);
}

internal static class PhysicalStoreStateFileTestCases
{
    private static readonly byte[] Prior = "old-state"u8.ToArray();
    private static readonly byte[] Next = "new-state"u8.ToArray();

    internal enum LegacySymbolicLinkExpectation
    {
        ReplacesLexicalEntry,
        RefusesReplacementAndPreservesEntries
    }

    internal static void ReadHardlink(Func<PhysicalStorePublicationTestContext> create, Action<string, string> hardlink)
    {
        using var c = create();
        Write(c, "state.json", Prior);
        hardlink(Path.Combine(c.ParentPath, "state.json"), Path.Combine(c.ParentPath, "sibling.json"));
        Assert.Throws<PackageStoreAdmissionException>(() => c.FileSystem.OpenFileChildNoFollow(c.Parent, "state.json", FileAccess.Read));
        using var stream = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), Entry(c, "state.json"), Prior.Length);
        Assert.Equal(Prior, Read(stream));
        stream.Position = 0;
        File.Move(Path.Combine(c.ParentPath, "state.json"), Path.Combine(c.ParentPath, "moved.json"));
        Assert.Throws<PackageStoreAdmissionException>(() => stream.ReadByte());
        Assert.Throws<PackageStoreAdmissionException>(() => stream.Dispose());
        Assert.Equal(Prior, File.ReadAllBytes(Path.Combine(c.ParentPath, "sibling.json")));
    }

    internal static void ReadRefusals(Func<PhysicalStorePublicationTestContext> create)
    {
        using var c = create();
        Write(c, "state.json", Prior);
        var expected = Entry(c, "state.json");
        Assert.Throws<PackageStoreAdmissionException>(() =>
        {
            using var unexpected = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), expected, Prior.Length - 1);
        });
        var wrong = new PhysicalStoreEntryInfo(expected.Kind, expected.Identity, expected.LinkCount, expected.Length + 1);
        Assert.Throws<PackageStoreAdmissionException>(() =>
        {
            using var unexpected = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), wrong, long.MaxValue);
        });
        var wrongIdentity = new PhysicalFileIdentity(expected.Identity.Provider, expected.Identity.VolumeOrDeviceId, expected.Identity.FileId + "-wrong");
        var stale = new PhysicalStoreEntryInfo(expected.Kind, wrongIdentity, expected.LinkCount, expected.Length);
        Assert.Throws<PackageStoreAdmissionException>(() =>
        {
            using var unexpected = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), stale, long.MaxValue);
        });
        File.CreateSymbolicLink(Path.Combine(c.ParentPath, "link.json"), "state.json");
        Assert.Throws<PackageStoreAdmissionException>(() =>
        {
            using var unexpected = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c, "link.json"), Entry(c, "link.json"), long.MaxValue);
        });
        using var stream = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), expected, long.MaxValue);
        File.WriteAllBytes(Path.Combine(c.ParentPath, "state.json"), [.. Prior, 1]);
        Assert.Throws<PackageStoreAdmissionException>(() => stream.ReadByte());
        Assert.Throws<PackageStoreAdmissionException>(() => stream.Dispose());
    }

    internal static void RetainedHandles(Func<PhysicalStorePublicationTestContext> create)
    {
        using var c = create();
        Write(c, "state.json", Prior);
        using var stream = StateFiles(c).OpenStatePayloadReadStream(c.Parent, Slot(c), Entry(c, "state.json"), long.MaxValue);
        c.Parent.Dispose();
        Assert.Equal(Prior, Read(stream));
    }

    internal static void PublishHardlink(Func<PhysicalStorePublicationTestContext> create, Action<string, string> hardlink)
    {
        using var c = create();
        Write(c, "state.json", Prior);
        hardlink(Path.Combine(c.ParentPath, "state.json"), Path.Combine(c.ParentPath, "sibling.json"));
        var prior = Entry(c, "state.json");
        var staged = Write(c, "state.next", Next);
        Assert.Throws<PackageStoreAdmissionException>(() => c.Publication.PublishControlFileAt(c.Parent, "state.next", staged.Identity, "state.json", prior.Identity));
        var published = StateFiles(c).PublishStateFileAt(c.Parent, "state.next", staged.Identity, Slot(c), prior);
        Assert.Equal(staged.Identity, published.Identity);
        Assert.Equal(1UL, published.LinkCount);
        Assert.Equal(Next, File.ReadAllBytes(Path.Combine(c.ParentPath, "state.json")));
        Assert.Equal(Prior, File.ReadAllBytes(Path.Combine(c.ParentPath, "sibling.json")));
        Assert.Equal(prior.Identity, Entry(c, "sibling.json").Identity);
        Assert.Null(c.FileSystem.InspectChildNoFollow(c.Parent, "state.next"));
    }

    internal static void PublishSymlink(Func<PhysicalStorePublicationTestContext> create)
    {
        using var c = create();
        var target = Write(c, "target.json", Prior);
        File.CreateSymbolicLink(Path.Combine(c.ParentPath, "state.json"), "target.json");
        var prior = Entry(c, "state.json");
        var staged = Write(c, "state.next", Next);
        Assert.Throws<PackageStoreAdmissionException>(() => c.Publication.PublishControlFileAt(c.Parent, "state.next", staged.Identity, "state.json", prior.Identity));
        var published = StateFiles(c).PublishStateFileAt(c.Parent, "state.next", staged.Identity, Slot(c), prior);
        Assert.Equal(staged.Identity, published.Identity);
        Assert.Equal(PhysicalStoreEntryKind.RegularFile, published.Kind);
        Assert.Equal(Next, File.ReadAllBytes(Path.Combine(c.ParentPath, "state.json")));
        Assert.Equal(Prior, File.ReadAllBytes(Path.Combine(c.ParentPath, "target.json")));
        Assert.Equal(target.Identity, Entry(c, "target.json").Identity);
    }

    internal static void PublicationRefusals(Func<PhysicalStorePublicationTestContext> create)
    {
        using var c = create();
        var staged = Write(c, "state.next", Next);
        var wrongPrior = Write(c, "other.json", Prior);
        Assert.Throws<PackageStoreAdmissionException>(() => StateFiles(c).PublishStateFileAt(c.Parent, "state.next", staged.Identity, Slot(c), wrongPrior));
        var prior = Write(c, "state.json", Prior);
        Assert.Throws<PackageStoreAdmissionException>(() => StateFiles(c).PublishStateFileAt(c.Parent, "state.next", staged.Identity, Slot(c), null));
        Assert.Throws<PackageStoreAdmissionException>(() => StateFiles(c).PublishStateFileAt(c.Parent, "state.next", prior.Identity, Slot(c), prior));
        Assert.Equal(Prior, File.ReadAllBytes(Path.Combine(c.ParentPath, "state.json")));
        Assert.Equal(Next, File.ReadAllBytes(Path.Combine(c.ParentPath, "state.next")));
        var created = StateFiles(c).PublishStateFileAt(c.Parent, "state.next", staged.Identity, Slot(c, "new-state.json"), null);
        Assert.Equal(staged.Identity, created.Identity);
        Assert.Equal(Next, File.ReadAllBytes(Path.Combine(c.ParentPath, "new-state.json")));
        Assert.Equal(Prior, File.ReadAllBytes(Path.Combine(c.ParentPath, "state.json")));
    }

    private static IPhysicalStoreStateFileSystem StateFiles(PhysicalStorePublicationTestContext c)
        => (IPhysicalStoreStateFileSystem)c.FileSystem;

    internal static async Task LegacyHardlink(Func<PhysicalStorePublicationTestContext> create, Action<string, string> hardlink)
    {
        using var c = create();
        var serializer = new StoreStateSerializer();
        var path = Path.Combine(c.ParentPath, "state.json");
        var sibling = Path.Combine(c.ParentPath, "sibling.json");
        await serializer.SaveAsync(path, LegacyState("1.0.0"), CancellationToken.None);
        var priorBytes = File.ReadAllBytes(path);
        hardlink(path, sibling);
        var priorIdentity = Entry(c, "sibling.json").Identity;

        await serializer.SaveAsync(path, LegacyState("2.0.0"), CancellationToken.None);

        Assert.Equal("2.0.0", (await serializer.LoadAsync(path, CancellationToken.None)).ActiveVersionById["pkg"]);
        Assert.Equal(priorBytes, File.ReadAllBytes(sibling));
        Assert.Equal(priorIdentity, Entry(c, "sibling.json").Identity);
        Assert.NotEqual(priorIdentity, Entry(c, "state.json").Identity);
    }

    internal static async Task LegacySymbolicLink(Func<PhysicalStorePublicationTestContext> create,
        LegacySymbolicLinkExpectation expectation, bool includeIntermediateLink = false)
    {
        using var c = create();
        var serializer = new StoreStateSerializer();
        var target = Path.Combine(c.ParentPath, "target.json");
        var middle = Path.Combine(c.ParentPath, "middle.json");
        var outer = Path.Combine(c.ParentPath, "state.json");
        await serializer.SaveAsync(target, LegacyState("1.0.0"), CancellationToken.None);
        var targetBefore = Entry(c, "target.json");
        var bytesBefore = File.ReadAllBytes(target);
        if (includeIntermediateLink)
            File.CreateSymbolicLink(middle, "target.json");
        var middleBefore = includeIntermediateLink ? Entry(c, "middle.json") : null;
        var linkTarget = includeIntermediateLink ? "middle.json" : "target.json";
        File.CreateSymbolicLink(outer, linkTarget);
        var outerBefore = Entry(c, "state.json");
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, outerBefore.Kind);
        Assert.Equal(linkTarget, new FileInfo(outer).LinkTarget);
        Assert.Equal("1.0.0", (await serializer.LoadAsync(outer, CancellationToken.None)).ActiveVersionById["pkg"]);

        if (expectation == LegacySymbolicLinkExpectation.RefusesReplacementAndPreservesEntries)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                serializer.SaveAsync(outer, LegacyState("2.0.0"), CancellationToken.None));
            Assert.Equal(outerBefore, Entry(c, "state.json"));
            Assert.Equal(linkTarget, new FileInfo(outer).LinkTarget);
        }
        else
        {
            await serializer.SaveAsync(outer, LegacyState("2.0.0"), CancellationToken.None);
            var replacement = Entry(c, "state.json");
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, replacement.Kind);
            Assert.NotEqual(outerBefore.Identity, replacement.Identity);
            Assert.Null(new FileInfo(outer).LinkTarget);
            Assert.Equal("2.0.0", (await serializer.LoadAsync(outer, CancellationToken.None)).ActiveVersionById["pkg"]);
        }

        if (includeIntermediateLink)
        {
            Assert.Equal(middleBefore, Entry(c, "middle.json"));
            Assert.Equal("target.json", new FileInfo(middle).LinkTarget);
        }
        Assert.Equal(targetBefore, Entry(c, "target.json"));
        Assert.Equal(bytesBefore, File.ReadAllBytes(target));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("1.0.0", (await serializer.LoadAsync(target, CancellationToken.None)).ActiveVersionById["pkg"]);
        Assert.Empty(Directory.GetFiles(c.ParentPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(c.ParentPath, "*.bak"));
    }

    internal static async Task LegacyInvalidReferents(Func<PhysicalStorePublicationTestContext> create)
    {
        foreach (var scenario in new[] { "dangling", "cycle", "malformed" })
        {
            using var c = create();
            var target = Path.Combine(c.ParentPath, "target.json");
            var outer = Path.Combine(c.ParentPath, "state.json");
            if (scenario == "malformed")
                File.WriteAllText(target, "invalid JSON");
            File.CreateSymbolicLink(outer, scenario == "cycle" ? "state.json" : "target.json");
            var before = Entry(c, "state.json");
            var serializer = new StoreStateSerializer();
            if (scenario == "malformed")
                await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => serializer.SaveAsync(outer, LegacyState("2.0.0"), CancellationToken.None));
            else
                await Assert.ThrowsAnyAsync<IOException>(() => serializer.SaveAsync(outer, LegacyState("2.0.0"), CancellationToken.None));
            Assert.Equal(before, Entry(c, "state.json"));
            Assert.Equal(scenario == "cycle" ? "state.json" : "target.json", new FileInfo(outer).LinkTarget);
            if (scenario == "malformed")
                Assert.Equal("invalid JSON", File.ReadAllText(target));
        }
    }

    private static StoreStateRecord LegacyState(string version)
        => StoreStateRecord.Empty() with { ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg"] = version } };

    private static StateSlotIdentity Slot(PhysicalStorePublicationTestContext c, string name = "state.json")
        => new(c.FileSystem.InspectHandle(c.Parent).Identity,
            ((IPhysicalStoreNameFileSystem)c.FileSystem).ObserveDirectoryNameSemantics(c.Parent), name);

    private static PhysicalStoreEntryInfo Entry(PhysicalStorePublicationTestContext c, string name)
        => c.FileSystem.InspectChildNoFollow(c.Parent, name) ?? throw new InvalidOperationException("Expected owned state fixture entry.");

    private static PhysicalStoreEntryInfo Write(PhysicalStorePublicationTestContext c, string name, byte[] bytes)
    {
        using var file = c.FileSystem.CreateFileExclusiveAt(c.Parent, name);
        c.FileSystem.WriteNewControlFile(file, bytes);
        return c.FileSystem.InspectHandle(file);
    }

    private static byte[] Read(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
