using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PhysicalStorePackageStreamTests
{
    [SupportedUnixFact]
    public Task UnixStreams_AreBoundedSeekableAndCreationOnly()
        => ExerciseBoundedStreams(new UnixPhysicalStoreFileSystem());

    [SupportedWindowsFact]
    public Task WindowsStreams_AreBoundedSeekableAndCreationOnly()
        => ExerciseBoundedStreams(new WindowsPhysicalStoreFileSystem());

    [SupportedUnixFact]
    public void UnixReadStream_RefusesWhenTheExactParentNameIsReplaced()
    {
        using var fixture = new PackageStoreFixture();
        var files = new UnixPhysicalStoreFileSystem();
        var streams = (IPhysicalStorePackageStreamFileSystem)files;
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var parentInfo = files.InspectHandle(parent);
        const string name = "archive.nupkg";
        var original = "original archive bytes"u8.ToArray();
        using var created = files.CreateFileExclusiveAt(parent, name);
        using (var writer = streams.CreatePackageFileWriteStream(parent, name, created, parentInfo, 1024))
            writer.Write(original);

        var expectedFile = files.InspectHandle(created);
        var reader = streams.OpenPackageArchiveReadStream(parent, name, created, parentInfo, expectedFile, 1024);
        var eofReader = streams.OpenPackageArchiveReadStream(parent, name, created, parentInfo, expectedFile, 1024);
        eofReader.Position = expectedFile.Length;
        var archivePath = Path.Combine(fixture.PackageInstallRoot, name);
        var movedPath = Path.Combine(fixture.PackageInstallRoot, "moved.nupkg");
        File.Move(archivePath, movedPath);
        File.WriteAllBytes(archivePath, "replacement archive"u8.ToArray());

        try
        {
            var zeroReadRefusal = Assert.Throws<PackageStoreAdmissionException>(() => reader.Read(Array.Empty<byte>(), 0, 0));
            var eofRefusal = Assert.Throws<PackageStoreAdmissionException>(() => eofReader.ReadByte());

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, zeroReadRefusal.Reason);
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, eofRefusal.Reason);
            Assert.Equal(0L, reader.Position);
            Assert.Equal(expectedFile.Length, eofReader.Position);
            Assert.Equal("replacement archive", File.ReadAllText(archivePath));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority,
                Assert.IsType<PackageStoreAdmissionException>(Record.Exception(reader.Dispose)).Reason);
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority,
                Assert.IsType<PackageStoreAdmissionException>(Record.Exception(eofReader.Dispose)).Reason);
        }
        finally
        {
            _ = Record.Exception(reader.Dispose);
            _ = Record.Exception(eofReader.Dispose);
        }
    }

    [SupportedWindowsFact]
    public void WindowsReadStream_RefusesWrongCaseAliasForTheSameFile()
    {
        var files = new WindowsPhysicalStoreFileSystem();
        var streams = Assert.IsAssignableFrom<IPhysicalStorePackageStreamFileSystem>(files);
        using var fixture = new PackageStoreFixture();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var parentInfo = files.InspectHandle(parent);
        const string canonicalName = "Archive.nupkg";
        using var file = files.CreateFileExclusiveAt(parent, canonicalName);
        using (var writer = streams.CreatePackageFileWriteStream(parent, canonicalName, file, parentInfo, 64))
            writer.Write("archive"u8);

        var fileInfo = files.InspectHandle(file);
        var refusal = Assert.Throws<PackageStoreAdmissionException>(
            () => streams.OpenPackageArchiveReadStream(parent, "archive.nupkg", file, parentInfo, fileInfo, 64));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [Fact]
    public void BoundedStream_PartialWriteCancellationStopsBeforeRetrying()
    {
        using var parentHandle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        using var fileHandle = new SafeFileHandle(new IntPtr(2), ownsHandle: false);
        var parentLease = new PhysicalStoreSafeHandleLease(parentHandle);
        var fileLease = new PhysicalStoreSafeHandleLease(fileHandle);
        using var cancellation = new CancellationTokenSource();
        var nativeLength = 0L;
        var nativeWriteCount = 0;
        using var stream = new BoundedPhysicalStoreFileStream(
            parentLease,
            fileLease,
            maximumBytes: 8,
            readLength: 0,
            writable: true,
            validateBinding: expectedLength => Assert.Equal(nativeLength, expectedLength),
            readAt: static (_, _, _, _) => throw new NotSupportedException(),
            writeAt: (_, _, _, _) =>
            {
                nativeWriteCount++;
                nativeLength++;
                cancellation.Cancel();
                return 1;
            },
            flush: static () => { });

        Assert.ThrowsAny<OperationCanceledException>(
            () => stream.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), cancellation.Token));

        Assert.Equal(1, nativeWriteCount);
        Assert.Equal(1L, stream.Position);
    }

    private static async Task ExerciseBoundedStreams(IPhysicalStoreFileSystem files)
    {
        var streams = Assert.IsAssignableFrom<IPhysicalStorePackageStreamFileSystem>(files);
        using var fixture = new PackageStoreFixture();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var parentInfo = files.InspectHandle(parent);
        const string name = "archive.nupkg";
        var content = new byte[230_000];
        for (var index = 0; index < content.Length; index++)
            content[index] = unchecked((byte)(index * 37));
        var payload = CreateArchive(content);

        using var file = files.CreateFileExclusiveAt(parent, name);
        await using (var writer = streams.CreatePackageFileWriteStream(parent, name, file, parentInfo, payload.Length))
        {
            await writer.WriteAsync(payload.AsMemory(0, 83_111));
            await writer.WriteAsync(payload.AsMemory(83_111));
            await writer.FlushAsync();
        }

        var writtenFile = files.InspectHandle(file);
        using (var verificationReader = streams.OpenPackageArchiveReadStream(parent, name, file, parentInfo, writtenFile, payload.Length))
        {
            using var writtenBytes = new MemoryStream();
            await verificationReader.CopyToAsync(writtenBytes);
            Assert.Equal(payload, writtenBytes.ToArray());
        }

        using var limitedFile = files.CreateFileExclusiveAt(parent, "limited.nupkg");
        await using (var limitedWriter = streams.CreatePackageFileWriteStream(parent, "limited.nupkg", limitedFile, parentInfo, 3))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => limitedWriter.WriteAsync(new byte[] { 1 }.AsMemory(), cancelled.Token).AsTask());
            Assert.Equal(0, limitedWriter.Position);
            Assert.Throws<IOException>(() => limitedWriter.Write(new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(0, limitedWriter.Position);
        }

        Assert.Equal(0L, files.InspectHandle(limitedFile).Length);
        Assert.Throws<InvalidOperationException>(
            () => streams.CreatePackageFileWriteStream(parent, "limited.nupkg", limitedFile, parentInfo, 3));

        var expectedFile = files.InspectHandle(file);
        Assert.Equal((long)payload.Length, expectedFile.Length);
        Assert.Equal(
            PackageStoreAdmissionReason.UnknownAuthority,
            Assert.Throws<PackageStoreAdmissionException>(
                () => streams.OpenPackageArchiveReadStream(parent, name, file, parentInfo, expectedFile, payload.Length - 1)).Reason);

        await using var reader = streams.OpenPackageArchiveReadStream(parent, name, file, parentInfo, expectedFile, payload.Length);
        parent.Dispose();
        file.Dispose();
        Assert.True(reader.CanRead);
        Assert.True(reader.CanSeek);
        Assert.False(reader.CanWrite);
        Assert.Equal((long)payload.Length, reader.Length);
        Assert.Equal(96_731L, reader.Seek(96_731, SeekOrigin.Begin));
        var slice = new byte[7_777];
        Assert.Equal(slice.Length, await reader.ReadAsync(slice.AsMemory()));
        Assert.Equal(payload.AsSpan(96_731, slice.Length).ToArray(), slice);
        Assert.Equal((long)payload.Length, reader.Seek(reader.Length, SeekOrigin.Begin));
        Assert.Equal(0, await reader.ReadAsync(new byte[1].AsMemory()));
        reader.Position = 0;
        using var archive = new ZipArchive(reader, ZipArchiveMode.Read, leaveOpen: true);
        using var entryStream = archive.GetEntry("content.bin")!.Open();
        using var restoredContent = new MemoryStream();
        await entryStream.CopyToAsync(restoredContent);
        Assert.Equal(content, restoredContent.ToArray());
    }

    private static byte[] CreateArchive(byte[] content)
    {
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var contentStream = archive.CreateEntry("content.bin", CompressionLevel.NoCompression).Open();
            contentStream.Write(content);
        }

        return archiveBytes.ToArray();
    }
}
