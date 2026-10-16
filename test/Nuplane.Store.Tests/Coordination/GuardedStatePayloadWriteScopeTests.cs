using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class GuardedStatePayloadWriteScopeTests
{
    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_CreatesAndReplacesWhileCallerRetainsGuard()
    {
        using var c = new Context();
        await using (var scope = await c.AcquireAsync())
        {
            var outcome = await c.Writer.WriteAsync(scope, State("1.0.0"), CancellationToken.None);
            Assert.Equal(outcome.StateIdentity, c.Entry("state.json")!.Identity);
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => c.AcquireAsync());
            scope.PrimaryLease.Revalidate();
        }
        await using (var scope = await c.AcquireAsync())
            await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
        Assert.Equal("2.0.0", (await c.Serializer.LoadAsync(c.Path("state.json"), CancellationToken.None)).ActiveVersionById["pkg"]);
        Assert.Empty(Directory.GetFiles(c.Native.ParentPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(c.Native.ParentPath, "*.bak"));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_HardlinkReplacesOnlySelectedEntry()
    {
        using var c = new Context();
        await c.SeedAsync("state.json");
        if (OperatingSystem.IsWindows())
            WindowsPhysicalStorePublicationTests.CreateHardLink(c.Path("state.json"), c.Path("sibling.json"));
        else
            UnixPhysicalStorePublicationTests.CreateHardLink(c.Path("state.json"), c.Path("sibling.json"));
        var original = c.Entry("state.json")!;
        var bytes = File.ReadAllBytes(c.Path("state.json"));
        await using (var scope = await c.AcquireAsync())
            await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
        Assert.NotEqual(original.Identity, c.Entry("state.json")!.Identity);
        Assert.Equal(original.Identity, c.Entry("sibling.json")!.Identity);
        Assert.Equal(bytes, File.ReadAllBytes(c.Path("sibling.json")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_ChainPreservesUnselectedEntriesAndQualifiedPlatformBehavior()
    {
        using var c = new Context();
        await c.ChainAsync();
        var outer = c.Entry("state.json")!;
        var middle = c.Entry("middle.json")!;
        var target = c.Entry("target.json")!;
        var bytes = File.ReadAllBytes(c.Path("target.json"));
        await using (var scope = await c.AcquireAsync())
        {
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAsync<IOException>(() => c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None));
                Assert.Equal(outer, c.Entry("state.json"));
                Assert.Equal("middle.json", new FileInfo(c.Path("state.json")).LinkTarget);
            }
            else
            {
                var result = await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
                Assert.Equal(result.StateIdentity, c.Entry("state.json")!.Identity);
                Assert.Equal(PhysicalStoreEntryKind.RegularFile, c.Entry("state.json")!.Kind);
                await scope.RevalidateAsync(CancellationToken.None);
            }
            foreach (var name in Context.ChainNames)
                await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => c.Guard.AcquireAsync(c.Native.Parent, c.Slot(name), CancellationToken.None));
        }
        Assert.Equal(middle, c.Entry("middle.json"));
        Assert.Equal("target.json", new FileInfo(c.Path("middle.json")).LinkTarget);
        Assert.Equal(target, c.Entry("target.json"));
        Assert.Equal(bytes, File.ReadAllBytes(c.Path("target.json")));
        Assert.Empty(Directory.GetFiles(c.Native.ParentPath, "*.tmp"));
        Assert.Empty(Directory.GetFiles(c.Native.ParentPath, "*.bak"));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_EveryPreexistingChainMarkerRefusesBeforePayload()
    {
        foreach (var name in Context.ChainNames)
        {
            using var c = new Context();
            await c.ChainAsync();
            await using (await c.Guard.AcquireAsync(c.Native.Parent, c.Slot(name), CancellationToken.None)) { }
            File.WriteAllBytes(c.Marker(name), [1]);
            var outer = c.Entry("state.json");
            var bytes = File.ReadAllBytes(c.Path("target.json"));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            {
                await using var scope = await c.AcquireAsync();
                await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
            });
            Assert.Equal(0, c.Codec.ReadCalls);
            Assert.Equal(0, c.Codec.WriteCalls);
            Assert.Equal(outer, c.Entry("state.json"));
            Assert.Equal(bytes, File.ReadAllBytes(c.Path("target.json")));
            await using var released = await c.Guard.AcquireAsync(c.Native.Parent, c.Slot("state.json"), CancellationToken.None);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_EveryMarkerAppearingAfterAcquireRefusesBeforePayload()
    {
        foreach (var name in Context.ChainNames)
        {
            using var c = new Context();
            await c.ChainAsync();
            var outer = c.Entry("state.json");
            await using var scope = await c.AcquireAsync();
            File.WriteAllBytes(c.Marker(name), [1]);
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None));
            Assert.Equal(0, c.Codec.ReadCalls);
            Assert.Equal(0, c.Codec.WriteCalls);
            Assert.Equal(outer, c.Entry("state.json"));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_ContentionReleasesEarlierGuardsBeforePayload()
    {
        foreach (var name in Context.ChainNames.Skip(1))
        {
            using var c = new Context();
            await c.ChainAsync();
            await using var contender = await c.Guard.AcquireAsync(c.Native.Parent, c.Slot(name), CancellationToken.None);
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => c.AcquireAsync());
            Assert.Equal(0, c.Codec.ReadCalls);
            Assert.Equal(0, c.Codec.WriteCalls);
            await using var primary = await c.Guard.AcquireAsync(c.Native.Parent, c.Slot("state.json"), CancellationToken.None);
            if (name == "target.json")
            {
                await using var middle = await c.Guard.AcquireAsync(c.Native.Parent, c.Slot("middle.json"), CancellationToken.None);
            }
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_RetargetAfterOpenRefusesBeforeStagingAndKeepsHeldPayloadBinding()
    {
        using var c = new Context();
        await c.ChainAsync();
        await c.SeedAsync("other.json", "3.0.0");
        var original = c.Entry("state.json");
        var target = File.ReadAllBytes(c.Path("target.json"));
        var other = File.ReadAllBytes(c.Path("other.json"));
        c.Codec.BeforeRead = _ =>
        {
            File.Delete(c.Path("middle.json"));
            File.CreateSymbolicLink(c.Path("middle.json"), "other.json");
        };
        await using var scope = await c.AcquireAsync();
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None));
        Assert.Equal(1, c.Codec.ReadCalls);
        Assert.Equal("1.0.0", c.Codec.LastRead!.ActiveVersionById["pkg"]);
        Assert.Equal(0, c.Codec.WriteCalls);
        Assert.Equal(original, c.Entry("state.json"));
        Assert.Equal(target, File.ReadAllBytes(c.Path("target.json")));
        Assert.Equal(other, File.ReadAllBytes(c.Path("other.json")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedWrite_InvalidReferentsNeverReplaceOriginalEntry()
    {
        foreach (var scenario in new[] { "dangling", "cycle", "malformed" })
        {
            using var c = new Context();
            if (scenario == "malformed")
                File.WriteAllText(c.Path("target.json"), "invalid JSON");
            File.CreateSymbolicLink(c.Path("state.json"), scenario == "cycle" ? "state.json" : "target.json");
            var original = c.Entry("state.json");
            async Task Write()
            {
                await using var scope = await c.AcquireAsync();
                await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
            }
            if (scenario == "malformed")
                await Assert.ThrowsAsync<System.Text.Json.JsonException>(Write);
            else if (scenario == "dangling")
                await Assert.ThrowsAsync<FileNotFoundException>(Write);
            else
                await Assert.ThrowsAsync<PackageStoreAdmissionException>(Write);
            Assert.Equal(original, c.Entry("state.json"));
            Assert.Equal(0, c.Codec.WriteCalls);
            Assert.Equal(scenario == "malformed" ? 1 : 0, c.Codec.ReadCalls);
        }
    }

    [SupportedUnixFact]
    public async Task ScopedWrite_PostPublicationReplayStillRejectsChangedIntermediateAlias()
    {
        using var c = new Context();
        await c.ChainAsync();
        await c.SeedAsync("other.json", "3.0.0");
        await using var scope = await c.AcquireAsync();
        await c.Writer.WriteAsync(scope, State("2.0.0"), CancellationToken.None);
        File.Delete(c.Path("middle.json"));
        File.CreateSymbolicLink(c.Path("middle.json"), "other.json");
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () => await scope.RevalidateAsync(CancellationToken.None));
        Assert.Equal("2.0.0", (await c.Serializer.LoadAsync(c.Path("state.json"), CancellationToken.None)).ActiveVersionById["pkg"]);
        Assert.Equal("1.0.0", (await c.Serializer.LoadAsync(c.Path("target.json"), CancellationToken.None)).ActiveVersionById["pkg"]);
    }

    private static StoreStateRecord State(string version)
        => StoreStateRecord.Empty() with { ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg"] = version } };

    private sealed class Context : IDisposable
    {
        internal static readonly string[] ChainNames = ["state.json", "middle.json", "target.json"];
        internal PhysicalStorePublicationTestContext Native { get; } = OperatingSystem.IsWindows()
            ? WindowsPhysicalStorePublicationTests.CreateContext()
            : UnixPhysicalStorePublicationTests.CreateContext();
        internal StoreStateSerializer Serializer { get; } = new();
        internal TrackingCodec Codec { get; } = new();
        internal PhysicalStoreStateSlotWriteGuard Guard => new(Native.FileSystem);
        internal GuardedNativeStoreStateWriter Writer => new(Native.FileSystem, Codec);
        internal string Path(string name) => System.IO.Path.Combine(Native.ParentPath, name);
        internal PhysicalStoreEntryInfo? Entry(string name) => Native.FileSystem.InspectChildNoFollow(Native.Parent, name);
        internal StateSlotIdentity Slot(string name) => new(Native.FileSystem.InspectHandle(Native.Parent).Identity,
            ((IPhysicalStoreNameFileSystem)Native.FileSystem).ObserveDirectoryNameSemantics(Native.Parent), name);
        internal string Marker(string name) => System.IO.Path.Combine(Native.ParentPath,
            PhysicalStoreStateSlotWriteGuard.ControlDirectoryName, name, PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName);
        internal Task<GuardedStatePayloadWriteScope> AcquireAsync()
            => GuardedStatePayloadWriteScope.AcquireAsync(Native.FileSystem,
                new PackageStoreAuthorityResolver(Native.FileSystem, new RootMembershipRegistry(Native.FileSystem, Serializer)),
                Path("state.json"), CancellationToken.None);
        internal Task SeedAsync(string name, string version = "1.0.0")
            => Serializer.SaveAsync(Path(name), State(version), CancellationToken.None);
        internal async Task ChainAsync()
        {
            await SeedAsync("target.json");
            File.CreateSymbolicLink(Path("middle.json"), "target.json");
            File.CreateSymbolicLink(Path("state.json"), "middle.json");
        }
        public void Dispose() => Native.Dispose();
    }

    private sealed class TrackingCodec : IPackageProtectionBundleStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal StoreStateRecord? LastRead { get; private set; }
        internal Action<Stream>? BeforeRead { get; set; }
        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken token) => _inner.LoadAsync(path, token);
        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken token) => _inner.SaveAsync(path, state, token);
        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken token)
        {
            ReadCalls++;
            BeforeRead?.Invoke(payload);
            LastRead = await _inner.ReadPayloadAsync(payload, token);
            return LastRead;
        }
        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken token)
        {
            WriteCalls++;
            return _inner.WritePayloadAsync(payload, state, token);
        }
    }
}
