using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreLockTests
{
    private const string RootLockName = "root.lock";

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RootContentionRefusesBeforeMemberLockAndDoesNotCreateIt()
    {
        using var context = CreateContext(createMemberLock: false);
        await using var rootBusy = await HoldLockAsync(context, RootLockName);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => AcquireAsync(context, [context.StateSlot]));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Contains(RootLockName, error.Message, StringComparison.Ordinal);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.ControlDirectory, PhysicalStoreLock.GetMemberLockName(context.StateSlot)));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_MemberContentionReleasesRootForAnotherOwner()
    {
        using var context = CreateContext();
        var memberName = PhysicalStoreLock.GetMemberLockName(context.StateSlot);
        await using var memberBusy = await HoldLockAsync(context, memberName);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => AcquireAsync(context, [context.StateSlot]));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Contains(memberName, error.Message, StringComparison.Ordinal);
        await using (var rootProbe = await HoldLockAsync(context, RootLockName))
        {
        }

        await memberBusy.DisposeAsync();
        await using var acquired = await AcquireAsync(context, [context.StateSlot]);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RequiresExistingCanonicalRegularSingleLinkLockFiles()
    {
        using (var missing = CreateContext(createRootLock: false, createMemberLock: false))
        {
            await AssertUnknownAsync(() => AcquireAsync(missing, [missing.StateSlot]));
            Assert.Null(missing.FileSystem.InspectChildNoFollow(missing.ControlDirectory, RootLockName));
        }

        using (var directory = CreateContext(createRootLock: false, createMemberLock: false))
        using (directory.FileSystem.CreateDirectoryExclusiveAt(directory.ControlDirectory, RootLockName))
        {
            await AssertUnknownAsync(() => AcquireAsync(directory, [directory.StateSlot]));
            Assert.Equal(PhysicalStoreEntryKind.Directory, RequireEntry(directory, RootLockName).Kind);
        }

        using (var link = CreateContext(createRootLock: false, createMemberLock: false))
        {
            var targetPath = link.Fixture.GetPath("control/root-lock-target");
            File.WriteAllBytes(targetPath, []);
            File.CreateSymbolicLink(link.Fixture.GetPath($"control/{RootLockName}"), targetPath);

            await AssertUnknownAsync(() => AcquireAsync(link, [link.StateSlot]));
            Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, RequireEntry(link, RootLockName).Kind);
            Assert.Empty(File.ReadAllBytes(targetPath));
        }

        using (var hardlink = CreateContext(createRootLock: false, createMemberLock: false))
        {
            var targetName = "root-lock-target";
            CreateLockFile(hardlink, targetName);
            CreateHardLink(hardlink.Fixture.GetPath($"control/{targetName}"), hardlink.Fixture.GetPath($"control/{RootLockName}"));

            await AssertUnknownAsync(() => AcquireAsync(hardlink, [hardlink.StateSlot]));
            Assert.Equal(2UL, RequireEntry(hardlink, RootLockName).LinkCount);
            Assert.Equal(0L, RequireEntry(hardlink, targetName).Length);
        }

        using (var noncanonical = CreateContext(createMemberLock: false, rootLockName: "ROOT.LOCK"))
        {
            await AssertUnknownAsync(() => AcquireAsync(noncanonical, [noncanonical.StateSlot]));
            Assert.NotNull(noncanonical.FileSystem.InspectChildNoFollow(noncanonical.ControlDirectory, "ROOT.LOCK"));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_MissingOrUnsafeMemberLockReleasesRootAndDoesNotCreateMember()
    {
        using (var missing = CreateContext(createMemberLock: false))
        {
            var memberName = PhysicalStoreLock.GetMemberLockName(missing.StateSlot);
            await AssertUnknownAsync(() => AcquireAsync(missing, [missing.StateSlot]));
            Assert.Null(missing.FileSystem.InspectChildNoFollow(missing.ControlDirectory, memberName));
            await using (var rootProbe = await HoldLockAsync(missing, RootLockName))
            {
            }
        }

        using (var wrongKind = CreateContext(createMemberLock: false))
        using (wrongKind.FileSystem.CreateDirectoryExclusiveAt(
                   wrongKind.ControlDirectory,
                   PhysicalStoreLock.GetMemberLockName(wrongKind.StateSlot)))
        {
            await AssertUnknownAsync(() => AcquireAsync(wrongKind, [wrongKind.StateSlot]));
            await using (var rootProbe = await HoldLockAsync(wrongKind, RootLockName))
            {
            }
        }

        using (var link = CreateContext(createMemberLock: false))
        {
            var memberLockName = PhysicalStoreLock.GetMemberLockName(link.StateSlot);
            var targetPath = link.Fixture.GetPath("control/member-lock-target");
            File.WriteAllBytes(targetPath, []);
            File.CreateSymbolicLink(link.Fixture.GetPath($"control/{memberLockName}"), targetPath);

            await AssertUnknownAsync(() => AcquireAsync(link, [link.StateSlot]));
            Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, RequireEntry(link, memberLockName).Kind);
            await using (var rootProbe = await HoldLockAsync(link, RootLockName))
            {
            }
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_MemberLockMustUseItsExactCanonicalName()
    {
        using var context = CreateContext(createMemberLock: false);
        var expectedName = PhysicalStoreLock.GetMemberLockName(context.StateSlot);
        var differentSpelling = expectedName.ToUpperInvariant();
        CreateLockFile(context, differentSpelling);

        await AssertUnknownAsync(() => AcquireAsync(context, [context.StateSlot]));

        Assert.NotNull(context.FileSystem.InspectChildNoFollow(context.ControlDirectory, differentSpelling));
        await using var rootProbe = await HoldLockAsync(context, RootLockName);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_ReleasesAllLocksAfterOwnerDisposalAndAllowsNextOwner()
    {
        using var context = CreateContext();
        var first = await AcquireAsync(context, [context.StateSlot]);
        await first.DisposeAsync();

        await using var second = await AcquireAsync(context, [context.StateSlot]);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_MultipleMembersSucceedRegardlessOfCallerOrder()
    {
        using var context = CreateContext();
        var secondSlot = CreateAdditionalStateSlot(context, "state-two");
        CreateLockFile(context, PhysicalStoreLock.GetMemberLockName(secondSlot));

        var forward = await AcquireAsync(context, [context.StateSlot, secondSlot]);
        await forward.DisposeAsync();
        await using var reversed = await AcquireAsync(context, [secondSlot, context.StateSlot]);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RejectsDuplicateSlotsBeforeTakingRootLock()
    {
        using var context = CreateContext();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => AcquireAsync(context, [context.StateSlot, context.StateSlot]));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var rootProbe = await HoldLockAsync(context, RootLockName);
    }

    [SupportedPhysicalStoreFact]
    public void GetMemberLockName_IsDeterministicAndBindsTheFullSlotIdentity()
    {
        using var context = CreateContext();
        var name = PhysicalStoreLock.GetMemberLockName(context.StateSlot);
        var sameSlot = new StateSlotIdentity(
            context.StateSlot.ParentIdentity,
            context.StateSlot.NameSemantics,
            context.StateSlot.CanonicalBasename);
        var otherName = new StateSlotIdentity(
            context.StateSlot.ParentIdentity,
            context.StateSlot.NameSemantics,
            context.StateSlot.CanonicalBasename + ".other");
        var otherParent = new StateSlotIdentity(
            new PhysicalFileIdentity(
                context.StateSlot.ParentIdentity.Provider,
                context.StateSlot.ParentIdentity.VolumeOrDeviceId,
                context.StateSlot.ParentIdentity.FileId + "-other"),
            context.StateSlot.NameSemantics,
            context.StateSlot.CanonicalBasename);
        var otherProfile = new StateSlotIdentity(
            context.StateSlot.ParentIdentity,
            new PhysicalStoreNameSemantics(
                context.StateSlot.NameSemantics.ProfileId + "-other",
                context.StateSlot.NameSemantics.Encoding,
                context.StateSlot.NameSemantics.CaseSensitive,
                context.StateSlot.NameSemantics.NormalizationInsensitive),
            context.StateSlot.CanonicalBasename);

        Assert.Equal(name, PhysicalStoreLock.GetMemberLockName(sameSlot));
        Assert.NotEqual(name, PhysicalStoreLock.GetMemberLockName(otherName));
        Assert.NotEqual(name, PhysicalStoreLock.GetMemberLockName(otherParent));
        Assert.NotEqual(name, PhysicalStoreLock.GetMemberLockName(otherProfile));
        Assert.StartsWith("member-", name, StringComparison.Ordinal);
        Assert.EndsWith(".lock", name, StringComparison.Ordinal);
        Assert.Equal(76, name.Length);
    }

    private static Task<IAsyncDisposable> AcquireAsync(TestContext context, IReadOnlyList<StateSlotIdentity> slots)
        => new PhysicalStoreLock(context.FileSystem).AcquireAsync(context.ControlDirectory, slots, CancellationToken.None);

    private static async Task AssertUnknownAsync(Func<Task<IAsyncDisposable>> action)
    {
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(action);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    private static async Task<ExternalLock> HoldLockAsync(TestContext context, string name)
    {
        var file = context.FileSystem.OpenFileChildNoFollow(context.ControlDirectory, name, FileAccess.ReadWrite);
        IAsyncDisposable? nativeLock = null;
        try
        {
            nativeLock = await context.FileSystem.TryAcquireExclusiveLock(file);
            if (nativeLock is null)
                throw new InvalidOperationException($"The test could not acquire its own lock on '{name}'.");
            return new ExternalLock(file, nativeLock);
        }
        catch
        {
            if (nativeLock is not null)
                await nativeLock.DisposeAsync();
            file.Dispose();
            throw;
        }
    }

    private static TestContext CreateContext(
        bool createRootLock = true,
        bool createMemberLock = true,
        string rootLockName = RootLockName)
    {
        var fixture = new PackageStoreFixture();
        PhysicalStoreDirectoryHandle? controlDirectory = null;
        try
        {
            IPhysicalStoreFileSystem fileSystem = OperatingSystem.IsWindows()
                ? new WindowsPhysicalStoreFileSystem()
                : new UnixPhysicalStoreFileSystem();
            using var root = OpenFixtureRoot(fileSystem, fixture);
            controlDirectory = fileSystem.CreateDirectoryExclusiveAt(root, "control");
            using var stateDirectory = fileSystem.CreateDirectoryExclusiveAt(root, "lock-state");
            using (var stateFile = fileSystem.CreateFileExclusiveAt(stateDirectory, "state.json"))
                fileSystem.WriteNewControlFile(stateFile, "state"u8.ToArray());
            var slot = new PhysicalStoreIdentity(fileSystem).ObserveStateSlot(stateDirectory, "state.json").Slot;

            if (createRootLock)
                CreateLockFile(fileSystem, controlDirectory, rootLockName);
            if (createMemberLock)
                CreateLockFile(fileSystem, controlDirectory, PhysicalStoreLock.GetMemberLockName(slot));
            var context = new TestContext(fixture, fileSystem, controlDirectory, slot);
            controlDirectory = null;
            return context;
        }
        catch
        {
            controlDirectory?.Dispose();
            fixture.Dispose();
            throw;
        }
    }

    private static void CreateLockFile(TestContext context, string name)
        => CreateLockFile(context.FileSystem, context.ControlDirectory, name);

    private static StateSlotIdentity CreateAdditionalStateSlot(TestContext context, string directoryName)
    {
        using var root = OpenFixtureRoot(context.FileSystem, context.Fixture);
        using var stateDirectory = context.FileSystem.CreateDirectoryExclusiveAt(root, directoryName);
        using (var stateFile = context.FileSystem.CreateFileExclusiveAt(stateDirectory, "state.json"))
            context.FileSystem.WriteNewControlFile(stateFile, "state"u8.ToArray());
        return new PhysicalStoreIdentity(context.FileSystem).ObserveStateSlot(stateDirectory, "state.json").Slot;
    }

    private static void CreateLockFile(IPhysicalStoreFileSystem fileSystem, PhysicalStoreDirectoryHandle directory, string name)
    {
        using var file = fileSystem.CreateFileExclusiveAt(directory, name);
        fileSystem.WriteNewControlFile(file, ReadOnlyMemory<byte>.Empty);
    }

    private static PhysicalStoreEntryInfo RequireEntry(TestContext context, string name)
        => context.FileSystem.InspectChildNoFollow(context.ControlDirectory, name)
           ?? throw new Xunit.Sdk.XunitException($"Expected the owned lock fixture '{name}' to exist.");

    private static PhysicalStoreDirectoryHandle OpenFixtureRoot(IPhysicalStoreFileSystem fileSystem, PackageStoreFixture fixture)
        => PhysicalStoreTestDirectory.Open(fileSystem, fixture.RootPath);

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

    private sealed class TestContext(
        PackageStoreFixture fixture,
        IPhysicalStoreFileSystem fileSystem,
        PhysicalStoreDirectoryHandle controlDirectory,
        StateSlotIdentity stateSlot) : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = fixture;
        internal IPhysicalStoreFileSystem FileSystem { get; } = fileSystem;
        internal PhysicalStoreDirectoryHandle ControlDirectory { get; } = controlDirectory;
        internal StateSlotIdentity StateSlot { get; } = stateSlot;

        public void Dispose()
        {
            try
            {
                ControlDirectory.Dispose();
            }
            finally
            {
                Fixture.Dispose();
            }
        }
    }

    private sealed class ExternalLock(PhysicalStoreFileHandle file, IAsyncDisposable nativeLock) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Exception? failure = null;
            try
            {
                await nativeLock.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                file.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

public sealed class SupportedPhysicalStoreFactAttribute : FactAttribute
{
    public SupportedPhysicalStoreFactAttribute()
    {
        if (!UnixPhysicalStoreFileSystem.IsSupportedPlatform && !WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
        {
            Skip = "Physical store locking is qualified only on Darwin arm64, Linux x64/arm64, and Windows x64; this skip is not runtime acceptance.";
        }
    }
}
