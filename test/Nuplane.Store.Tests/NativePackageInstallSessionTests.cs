using System.IO.Compression;
using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Tests.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests;

[Trait("Platform", "Native")]
public sealed class NativePackageInstallSessionTests
{
    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_StagesVerifiesAndPublishesCompleteArchiveThroughNativeHandles()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var destination = InstallPath(context, "Native.Package");
        var payload = "native package payload"u8.ToArray();
        var archiveBytes = CreateArchive(("lib/net10.0/Native.Package.dll", payload));
        var callbackCount = 0;

        var installed = await NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot,
            destination,
            borrow,
            async (stream, token) =>
            {
                callbackCount++;
                await stream.WriteAsync(archiveBytes, token);
            },
            CancellationToken.None);

        Assert.Equal(destination, installed);
        Assert.Equal(1, callbackCount);
        Assert.True(PackageInstallStore.IsInstalled(destination, borrow));
        Assert.Equal("sha512:" + Convert.ToBase64String(SHA512.HashData(archiveBytes)),
            await PackageInstallStore.ReadContentHashAsync(destination, borrow, CancellationToken.None));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "lib", "net10.0", "Native.Package.dll")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(destination, PackageInstallStore.CompletionMarkerFileName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RefusesWrongRootAndExpiredBorrowBeforeArchiveCallback()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var foreignFixture = new PackageStoreFixture();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admission.Owner);
        using var borrow = owner.Borrow();
        var callbackCount = 0;
        Task WriteArchive(Stream stream, CancellationToken token)
        {
            callbackCount++;
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            foreignFixture.PackageInstallRoot,
            Path.Combine(foreignFixture.PackageInstallRoot, "feed", "Wrong.Root", "1.0.0"),
            borrow,
            WriteArchive,
            CancellationToken.None));
        Assert.Equal(0, callbackCount);

        borrow.Dispose();
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot,
            InstallPath(context, "Expired.Borrow"),
            borrow,
            WriteArchive,
            CancellationToken.None));
        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RejectsNestedReservedAuthorityParentBeforeRequestingArchive()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var callbackCount = 0;
        foreach (var (reservedName, prefixCreated) in new[]
                 {
                     (RootMembershipRegistry.ControlDirectoryName, false),
                     (".tmp", true)
                 })
        {
            var prefix = Path.Combine(context.Fixture.PackageInstallRoot, "new-prefix-" + reservedName.Trim('.'));
            var authorityDirectory = Path.Combine(prefix, reservedName);
            var destination = Path.Combine(authorityDirectory, "Nested.Package", "1.0.0");
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
                context.Fixture.PackageInstallRoot, destination, borrow,
                (_, _) => { callbackCount++; return Task.CompletedTask; }, CancellationToken.None));

            Assert.Equal(prefixCreated, Directory.Exists(prefix));
            Assert.False(Directory.Exists(authorityDirectory));
            Assert.False(Directory.Exists(destination));
        }

        var finalParent = Path.Combine(context.Fixture.PackageInstallRoot, "final-reserved-parent");
        var finalReserved = Path.Combine(finalParent, ".tmp");
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, finalReserved, borrow,
            (_, _) => { callbackCount++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.True(Directory.Exists(finalParent));
        Assert.False(Directory.Exists(finalReserved));

        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RejectsNestedReservedAuthorityArchivePathWithoutFinalInstall()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var destination = InstallPath(context, "Reserved.Archive.Path");
        var archiveBytes = CreateArchive(($"ordinary/{RootMembershipRegistry.ControlDirectoryName}/owned.txt", "no"u8.ToArray()));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, destination, borrow,
            async (stream, token) => await stream.WriteAsync(archiveBytes, token), CancellationToken.None));

        Assert.False(Directory.Exists(destination));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_RejectsTraversalFileDirectoryCollisionAndLinkEntriesBeforePublication()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var invalidArchives = new[]
        {
            CreateArchive(("../escaped.txt", "outside"u8.ToArray())),
            CreateArchive(("collision", "file"u8.ToArray()), ("collision/child.txt", "child"u8.ToArray())),
            CreateArchive((PackageInstallStore.CompletionMarkerFileName + "/child.txt", "reserved"u8.ToArray())),
            CreateArchive(("C:/drive.txt", "rooted"u8.ToArray())),
            CreateSymbolicLinkArchive(),
            CreateCorruptPayloadArchive()
        };

        for (var index = 0; index < invalidArchives.Length; index++)
        {
            var destination = InstallPath(context, $"Invalid.Archive.{index}");
            var archiveBytes = invalidArchives[index];
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
                context.Fixture.PackageInstallRoot, destination, borrow,
                async (stream, token) => await stream.WriteAsync(archiveBytes, token), CancellationToken.None));
            Assert.False(Directory.Exists(destination));
        }

        Assert.False(File.Exists(Path.Combine(context.Fixture.RootPath, "escaped.txt")));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_HandlesSharedParentSpellingAccordingToNativeNameProfile()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var names = Assert.IsAssignableFrom<IPhysicalStoreNameFileSystem>(context.Files);
        bool caseSensitive;
        using (var feed = PhysicalStoreTestDirectory.Open(context.Files, Path.Combine(context.Fixture.PackageInstallRoot, "feed")))
        {
            caseSensitive = names.ObserveDirectoryNameSemantics(feed).CaseSensitive;
        }

        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var destination = InstallPath(context, "Folded.Alias");
        var archiveBytes = CreateArchive(
            ("Foo/a.txt", "first"u8.ToArray()),
            ("foo/b.txt", "second"u8.ToArray()));

        Task<string> Acquire() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, destination, borrow,
            async (stream, token) => await stream.WriteAsync(archiveBytes, token), CancellationToken.None);

        if (caseSensitive)
        {
            Assert.Equal(destination, await Acquire());
            Assert.True(PackageInstallStore.IsInstalled(destination, borrow));
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destination, "Foo", "a.txt")));
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(destination, "foo", "b.txt")));
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => Acquire());
            Assert.Contains("native name alias", refusal.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(destination));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_ParentReplacementIsBlockedOrRefusedBeforePublication()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var originalParent = Path.Combine(context.Fixture.PackageInstallRoot, "feed", "Moved.Parent");
        var detachedParent = originalParent + ".detached";
        var destination = Path.Combine(originalParent, "1.0.0");
        var archiveBytes = CreateArchive(("lib/Moved.dll", "payload"u8.ToArray()));
        var callbackCount = 0;

        Task Acquire() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, destination, borrow,
            async (stream, token) =>
            {
                callbackCount++;
                Directory.Move(originalParent, detachedParent);
                await stream.WriteAsync(archiveBytes, token);
            }, CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            // The held native archive prevents this rename on Windows before path replay runs.
            var refusal = await Assert.ThrowsAsync<IOException>(Acquire);
            Assert.Equal(32, refusal.HResult & 0xffff); // ERROR_SHARING_VIOLATION
            Assert.True(Directory.Exists(originalParent));
            Assert.False(Directory.Exists(detachedParent));
        }
        else
        {
            // Unix permits moving the held tree; replay must reject its now-missing original edge.
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(Acquire);
            Assert.True(Directory.Exists(detachedParent));
            Assert.False(Directory.Exists(originalParent));
        }

        Assert.Equal(1, callbackCount);
        Assert.False(Directory.Exists(destination));
    }

    [LinuxExt4CasefoldFact]
    public async Task AcquireAsync_Ext4CasefoldRejectsSharedParentAliasBeforePublication()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync(candidate =>
        {
            var feed = Directory.CreateDirectory(Path.Combine(candidate.Fixture.PackageInstallRoot, "folded-feed"));
            UnixPhysicalStoreIdentityTests.EnableExt4Casefold(feed.FullName);
        });
        await context.PublishStatesAsync();
        await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1, true,
            CancellationToken.None);
        var feedPath = Path.Combine(context.Fixture.PackageInstallRoot, "folded-feed");
        using (var feed = PhysicalStoreTestDirectory.Open(context.Files, feedPath))
        {
            var names = Assert.IsAssignableFrom<IPhysicalStoreNameFileSystem>(context.Files);
            Assert.False(names.ObserveDirectoryNameSemantics(feed).CaseSensitive);
        }

        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var destination = Path.Combine(feedPath, "Folded.Package", "1.0.0");
        var archiveBytes = CreateArchive(
            ("Foo/a.txt", "first"u8.ToArray()),
            ("foo/b.txt", "second"u8.ToArray()));
        var callbackCount = 0;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, destination, borrow,
            async (stream, token) =>
            {
                callbackCount++;
                await stream.WriteAsync(archiveBytes, token);
            }, CancellationToken.None));

        Assert.Equal(1, callbackCount);
        Assert.Contains("native name alias", refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_CorruptArchiveAndCancellationNeverPublishFinalDirectory()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var corruptDestination = InstallPath(context, "Corrupt.Archive");

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, corruptDestination, borrow,
            async (stream, token) => await stream.WriteAsync("not a zip archive"u8.ToArray(), token), CancellationToken.None));

        Assert.False(Directory.Exists(corruptDestination));

        var cancelledDestination = InstallPath(context, "Cancelled.Archive");
        var archiveBytes = CreateArchive(("lib/Cancelled.dll", "payload"u8.ToArray()));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativePackageInstallSession.AcquireAsync(
            context.Fixture.PackageInstallRoot, cancelledDestination, borrow,
            async (stream, token) =>
            {
                await stream.WriteAsync(archiveBytes.AsMemory(0, archiveBytes.Length / 2), token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));

        Assert.False(Directory.Exists(cancelledDestination));
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => new(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static string InstallPath(RootMembershipProtectionVerificationTests.Context context, string packageId)
        => Path.Combine(context.Fixture.PackageInstallRoot, "feed", packageId, "1.0.0");

    private static byte[] CreateArchive(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var output = entry.Open();
                output.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateSymbolicLinkArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("linked.txt", CompressionLevel.NoCompression);
            entry.ExternalAttributes = unchecked((int)(0xA000u << 16));
            using var output = entry.Open();
            output.Write("link-target"u8);
        }
        return stream.ToArray();
    }

    private static byte[] CreateCorruptPayloadArchive()
    {
        var payload = "unique-corruption-payload"u8.ToArray();
        var archive = CreateArchive(("content.bin", payload));
        var payloadOffset = FindBytes(archive, payload);
        Assert.True(payloadOffset >= 0);
        archive[payloadOffset] ^= 0x01;
        return archive;
    }

    private static int FindBytes(byte[] source, byte[] value)
    {
        for (var start = 0; start <= source.Length - value.Length; start++)
        {
            if (source.AsSpan(start, value.Length).SequenceEqual(value))
                return start;
        }
        return -1;
    }
}
