using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Unix")]
public sealed class UnixPhysicalStorePublicationTests
{
    [SupportedUnixFact]
    public void Publish_ReplacesExistingSlotAndPreservesItsIdentity()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.ReplacesExistingSlotAndPreservesItsIdentity(context);
    }

    [SupportedUnixFact]
    public void Publish_AbsentSlotAndRefusesOccupiedTarget()
    {
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.PublishesToAbsentSlotWithoutReplacingAnything(context);
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.AbsentPublicationRefusesOccupiedTargetAndPreservesBothFiles(context);
    }

    [SupportedUnixFact]
    public void Publish_RefusesStaleSourceAndDestinationIdentities()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.StaleSourceAndDestinationIdentitiesRefuseWithoutMutation(context);
    }

    [SupportedUnixFact]
    public void PublishAndRemove_RefuseSymbolicAndHardLinks()
    {
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.LinkEntriesAreRefusedForPublishAndRemove(context);
        using (var context = CreateContext())
            PhysicalStorePublicationTestCases.HardLinkedEntriesAreRefusedForPublishAndRemove(context, CreateHardLink);
    }

    [SupportedUnixFact]
    public void Remove_RequiresExactRegularSingleLinkIdentity()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.RemovalRequiresExactRegularSingleLinkIdentity(context);
    }

    [SupportedUnixFact]
    public void Publish_RefusesAliasSpellingForeignAndClosedParents()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.RefusesAliasSpellingAndForeignOrClosedParent(context);
    }

    [SupportedUnixFact]
    public void Publish_UsesHeldDirectoryAfterTextualParentReplacement()
    {
        using var context = CreateContext();
        PhysicalStorePublicationTestCases.UnixHeldParentPublishesIntoOriginalDirectoryAfterTextualReplacement(context);
    }

    [SupportedUnixFact]
    public void NativeNoReplace_RefusesOccupiedTargetWithoutManagedPreInspection()
    {
        using var fixture = new PackageStoreFixture();
        var parentPath = fixture.CreateDirectory("parent");
        var prior = "prior-state"u8.ToArray();
        var staged = "next-state"u8.ToArray();
        File.WriteAllBytes(Path.Combine(parentPath, "state.json"), prior);
        File.WriteAllBytes(Path.Combine(parentPath, "state.next"), staged);

        var platform = UnixNative.GetPlatform()!.Value;
        using var parent = OpenDirectoryPath(platform, parentPath);
        var parentFd = checked((int)parent.DangerousGetHandle());
        var priorBefore = RequireStat(platform, parentFd, "state.json");
        var stagedBefore = RequireStat(platform, parentFd, "state.next");

        var error = Assert.Throws<UnixNativeCallException>(
            () => UnixNative.PublishNoReplaceAt(platform, parentFd, "state.next", "state.json"));
        Assert.Equal(17, error.Error); // EEXIST on the qualified Darwin and Linux ABIs.

        Assert.Equal(priorBefore, RequireStat(platform, parentFd, "state.json"));
        Assert.Equal(stagedBefore, RequireStat(platform, parentFd, "state.next"));
        Assert.Equal(prior, File.ReadAllBytes(Path.Combine(parentPath, "state.json")));
        Assert.Equal(staged, File.ReadAllBytes(Path.Combine(parentPath, "state.next")));
    }

    [LinuxExt4CasefoldFact]
    public void Publish_Ext4CasefoldAliasesRequireCanonicalNames()
    {
        using (var context = CreateContext(enableCasefold: true))
            PhysicalStorePublicationTestCases.RefusesAliasSpellingAndForeignOrClosedParent(context);
        using (var context = CreateContext(enableCasefold: true))
            PhysicalStorePublicationTestCases.ReplacesExistingSlotAndPreservesItsIdentity(context);
    }

    internal static PhysicalStorePublicationTestContext CreateContext(bool enableCasefold = false)
    {
        var fixture = new PackageStoreFixture();
        PhysicalStoreDirectoryHandle? parent = null;
        try
        {
            var adapter = new UnixPhysicalStoreFileSystem();
            using (var root = OpenFixtureRoot(adapter, fixture))
                parent = adapter.CreateDirectoryExclusiveAt(root, "parent");
            if (enableCasefold)
                UnixPhysicalStoreIdentityTests.EnableExt4Casefold(fixture.GetPath("parent"));

            return new PhysicalStorePublicationTestContext(
                fixture,
                adapter,
                (IPhysicalStorePublicationFileSystem)adapter,
                parent ?? throw new InvalidOperationException("The fixture parent was not created."));
        }
        catch
        {
            parent?.Dispose();
            fixture.Dispose();
            throw;
        }
    }

    private static PhysicalStoreDirectoryHandle OpenFixtureRoot(UnixPhysicalStoreFileSystem adapter, PackageStoreFixture fixture)
    {
        var canonicalPath = ResolveRealPath(fixture.RootPath);
        var current = adapter.OpenNamespaceRoot("/");
        try
        {
            foreach (var component in canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = adapter.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static string ResolveRealPath(string path)
    {
        var resolved = OperatingSystem.IsMacOS()
            ? DarwinRealPath(path, IntPtr.Zero)
            : LinuxRealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException($"The owned fixture path could not be physically resolved (native error {Marshal.GetLastPInvokeError()}).");

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The native path resolver returned no path.");
        }
        finally
        {
            if (OperatingSystem.IsMacOS())
                DarwinFree(resolved);
            else
                LinuxFree(resolved);
        }
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsMacOS()
            ? DarwinLink(existingPath, newPath)
            : LinuxLink(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    private static UnixMetadata RequireStat(UnixPlatform platform, int parentFd, string name)
    {
        var result = UnixNative.StatAt(platform, parentFd, name);
        Assert.Equal(UnixStatResultStatus.Success, result.Status);
        return result.Metadata;
    }

    internal static SafeFileHandle OpenDirectoryPath(UnixPlatform platform, string path)
    {
        var canonicalPath = ResolveRealPath(path);
        var current = new SafeFileHandle((IntPtr)UnixNative.OpenNamespaceRoot(platform), ownsHandle: true);
        try
        {
            foreach (var component in canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = new SafeFileHandle(
                    (IntPtr)UnixNative.OpenDirectoryAt(platform, checked((int)current.DangerousGetHandle()), component),
                    ownsHandle: true);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr DarwinRealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LinuxRealPath(string path, IntPtr resolvedPath);

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int DarwinLink(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxLink(string existingPath, string newPath);

    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    private static extern void DarwinFree(IntPtr value);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void LinuxFree(IntPtr value);
}
