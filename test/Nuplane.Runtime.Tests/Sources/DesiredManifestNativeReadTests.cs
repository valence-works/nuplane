using System.Text;
using System.Runtime.InteropServices;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Sources;
using Nuplane.Store.Coordination;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Sources;

public sealed class DesiredManifestNativeReadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nuplane-manifest-native-{Guid.NewGuid():N}");

    public DesiredManifestNativeReadTests()
        => System.IO.Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task ReadAsync_PreservesFileReadAllTextBomSemantics(string encodingName)
    {
        const string json = "{\"schemaVersion\":\"1.0\",\"packages\":[{\"id\":\"Native.Package\",\"version\":\"2.3.4\",\"sourceHint\":\"local\"}]}";
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            "utf32-le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            "utf32-be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        var prefix = encoding.GetPreamble();
        var payload = encoding.GetBytes(json);
        var path = Path.Combine(_directory, $"{encodingName}.json");
        File.WriteAllBytes(path, [.. prefix, .. payload]);

        var result = await new DesiredManifestReader().ReadAsync(path, "bom-correlation", CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Equal("bom-correlation", result.CorrelationId);
        var entry = Assert.Single(result.Manifest!.Packages);
        Assert.Equal("Native.Package", entry.Id);
        Assert.Equal("2.3.4", entry.Version);
        Assert.Equal("local", entry.SourceHint);
    }

    [Fact]
    public async Task ReadAsync_EmptyAndMalformedFilesRemainInvalid()
    {
        var empty = Path.Combine(_directory, "empty.json");
        var malformed = Path.Combine(_directory, "malformed.json");
        File.WriteAllBytes(empty, []);
        File.WriteAllText(malformed, "{");
        var reader = new DesiredManifestReader();

        var emptyResult = await reader.ReadAsync(empty, "empty", CancellationToken.None);
        var malformedResult = await reader.ReadAsync(malformed, "malformed", CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, emptyResult.Status);
        Assert.Equal(ManifestReadStatus.Invalid, malformedResult.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingFileAndOrdinaryDirectoryRemainNotFound()
    {
        var missing = await new DesiredManifestReader().ReadAsync(
            Path.Combine(_directory, "missing.json"), "missing", CancellationToken.None);
        var missingParent = await new DesiredManifestReader().ReadAsync(
            Path.Combine(_directory, "missing-parent", "desired.json"), "missing-parent", CancellationToken.None);
        var directory = await new DesiredManifestReader().ReadAsync(_directory, "directory", CancellationToken.None);
        var trailingDirectory = await new DesiredManifestReader().ReadAsync(_directory + Path.DirectorySeparatorChar,
            "trailing-directory", CancellationToken.None);

        Assert.Equal(ManifestReadStatus.NotFound, missing.Status);
        Assert.Equal(ManifestReadStatus.NotFound, missingParent.Status);
        Assert.Equal(ManifestReadStatus.NotFound, directory.Status);
        Assert.Equal(ManifestReadStatus.NotFound, trailingDirectory.Status);
    }

    [Fact]
    public async Task ReadAsync_FinalSymbolicLinkIsTypedAuthorityRefusal()
    {
        var target = Path.Combine(_directory, "target.json");
        var link = Path.Combine(_directory, "linked.json");
        File.WriteAllText(target, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        File.CreateSymbolicLink(link, target);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            new DesiredManifestReader().ReadAsync(link, "symlink", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [Fact]
    public async Task ReadAsync_HardLinkedFileIsTypedAuthorityRefusal()
    {
        var target = Path.Combine(_directory, "target-hardlink.json");
        var alias = Path.Combine(_directory, "hardlink.json");
        File.WriteAllText(target, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        CreateHardLink(target, alias);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            new DesiredManifestReader().ReadAsync(alias, "hardlink", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [DesiredManifestUnixFact]
    public async Task ReadAsync_SpecialFileIsTypedAuthorityRefusal()
    {
        var path = Path.Combine(_directory, "special.json");
        if (Mkfifo(path, 0x180) != 0)
            throw new IOException($"Creating the native FIFO failed with errno {Marshal.GetLastPInvokeError()}.");

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            new DesiredManifestReader().ReadAsync(path, "special", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [Fact]
    public async Task ReadAsync_AwaitsWhileNativeFileIsHeldAndCancellationReleasesIt()
    {
        var path = Path.Combine(_directory, "held.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        var readHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var reader = new DesiredManifestReader(async token =>
        {
            readHeld.TrySetResult();
            await releaseRead.Task.WaitAsync(token);
        });
        var read = reader.ReadAsync(path, "held", cancellation.Token);

        try
        {
            await readHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertCanNotAcquireWriter(path);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            AssertCanAcquireWriter(path);
        }
        finally
        {
            cancellation.Cancel();
            releaseRead.TrySetResult();
            if (!read.IsCompleted)
            {
                try { await read; }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task ReadAsync_DisposedOriginalBorrowDrainsUntilAwaitedNativeReadCompletes()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var path = Path.Combine(fixture.InstallRoot, "borrow-drain.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        var readHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new DesiredManifestReader(async token =>
        {
            readHeld.TrySetResult();
            await releaseRead.Task.WaitAsync(token);
        });
        var ownerAdmission = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        var borrow = ownerAdmission.Owner!.Borrow();
        var read = reader.ReadAsync(borrow, path, "borrow-drain", CancellationToken.None);
        Task? ownerDisposal = null;

        try
        {
            await readHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            borrow.Dispose();
            ownerDisposal = ownerAdmission.DisposeAsync().AsTask();
            Assert.False(ownerDisposal.IsCompleted);
            releaseRead.TrySetResult();
            Assert.Equal(ManifestReadStatus.Succeeded, (await read).Status);
            await ownerDisposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseRead.TrySetResult();
            borrow.Dispose();
            if (ownerDisposal is null)
                await ownerAdmission.DisposeAsync();
            else if (!ownerDisposal.IsCompleted)
                await ownerDisposal;
        }
    }

    [Fact]
    public async Task ReadAsync_PathRestrictedBorrowRefusesBeforeNativeRead()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var allowedPath = System.IO.Directory.CreateDirectory(Path.Combine(fixture.InstallRoot, "allowed-package")).FullName;
        var manifestPath = Path.Combine(fixture.InstallRoot, "restricted.json");
        File.WriteAllText(manifestPath, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        await using var pathAdmission = await fixture.Admission.AcquireForInstallPathsAsync(
            [allowedPath], PackageStoreAdmissionKind.Maintenance);
        var borrow = pathAdmission.BorrowFor(allowedPath);
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });

        try
        {
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                reader.ReadAsync(borrow, manifestPath, "path-restricted", CancellationToken.None));

            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
            Assert.Equal(0, Volatile.Read(ref nativeReadCount));
        }
        finally
        {
            borrow.Dispose();
        }
    }

    [DesiredManifestUnixFact]
    public async Task ReadAsync_RefusesSameLengthReplacementDuringAwaitedNativeRead()
    {
        var path = Path.Combine(_directory, "replacement.json");
        var moved = Path.Combine(_directory, "replacement.original.json");
        const string original = "{\"schemaVersion\":\"1.0\",\"packages\":[]}";
        const string replacement = "{\"schemaVersion\":\"2.0\",\"packages\":[]}";
        Assert.Equal(original.Length, replacement.Length);
        File.WriteAllText(path, original);
        var reader = new DesiredManifestReader(_ =>
        {
            File.Move(path, moved);
            File.WriteAllText(path, replacement);
            Assert.Equal(original.Length, new FileInfo(path).Length);
            Assert.Equal(original.Length, new FileInfo(moved).Length);
            return Task.CompletedTask;
        });

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            reader.ReadAsync(path, "replacement", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    [Fact]
    public async Task ReadAsync_RefusesAuthorityIntroducedAfterNativeSnapshot()
    {
        var path = Path.Combine(_directory, "authority.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"1.0\",\"packages\":[]}");
        var reader = new DesiredManifestReader(_ =>
        {
            System.IO.Directory.CreateDirectory(Path.Combine(_directory, ".nuplane-store"));
            return Task.CompletedTask;
        });

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            reader.ReadAsync(path, "authority", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
            System.IO.Directory.Delete(_directory, recursive: true);
    }

    private static void AssertCanNotAcquireWriter(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockExclusive | LockNonBlocking);
        if (result == 0)
            _ = Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockUnlock);
        Assert.Equal(-1, result);
    }

    private static void AssertCanAcquireWriter(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return;
        }

        using var unixStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var handle = unixStream.SafeFileHandle.DangerousGetHandle().ToInt32();
        Assert.Equal(0, Flock(handle, LockExclusive | LockNonBlocking));
        Assert.Equal(0, Flock(handle, LockUnlock));
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : CreateHardLinkUnix(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The native hard-link operation failed with {Marshal.GetLastPInvokeError()}.");
    }

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);

}

public sealed class DesiredManifestUnixFactAttribute : FactAttribute
{
    public DesiredManifestUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix native rename replay is qualified only on Darwin and Linux; Windows share-delete refusal is covered separately.";
    }
}
