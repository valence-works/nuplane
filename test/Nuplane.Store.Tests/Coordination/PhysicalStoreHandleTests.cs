using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PhysicalStoreHandleTests
{
    [Fact]
    public void Dispose_WithActiveNativeScope_DefersNativeCloseAndRejectsNewWork()
    {
        var provider = new object();
        var native = new CountingSafeHandle();
        using var directory = new PhysicalStoreDirectoryHandle(provider, native);
        var scope = directory.AcquireScopedSafeHandle(provider);
        try
        {
            directory.Dispose();
            directory.Dispose();
            Assert.Equal(0, native.ReleaseCount);
            Assert.Equal(new IntPtr(17), scope.DangerousHandle);
            var error = Assert.Throws<PackageStoreAdmissionException>(
                () => directory.AcquireScopedSafeHandle(provider));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, error.Reason);
        }
        finally
        {
            scope.Dispose();
        }

        scope.Dispose();
        Assert.Equal(1, native.ReleaseCount);
        Assert.Throws<ObjectDisposedException>(() => scope.DangerousHandle);
    }

    [Fact]
    public void AcquireScopedSafeHandle_ForeignProvider_RefusesWithoutClosingTheHandle()
    {
        var provider = new object();
        var native = new CountingSafeHandle();
        using var directory = new PhysicalStoreDirectoryHandle(provider, native);

        var error = Assert.Throws<PackageStoreAdmissionException>(
            () => directory.AcquireScopedSafeHandle(new object()));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, error.Reason);
        Assert.Equal(0, native.ReleaseCount);
        using var scope = directory.AcquireScopedSafeHandle(provider);
        Assert.Equal(new IntPtr(17), scope.DangerousHandle);
    }

    [Fact]
    public async Task ClaimInitialWrite_ConcurrentClaims_AdmitsOnlyOneWriter()
    {
        var provider = new object();
        using var file = new PhysicalStoreFileHandle(provider, new CountingSafeHandle(), createdExclusive: true);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                file.ClaimInitialWrite(provider);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results.Where(static claimed => claimed));
        Assert.Throws<InvalidOperationException>(() => file.ClaimInitialWrite(provider));
    }

    [Fact]
    public void ClaimInitialWrite_OpenedFile_RefusesAnInPlaceRewrite()
    {
        var provider = new object();
        using var file = new PhysicalStoreFileHandle(provider, new CountingSafeHandle(), createdExclusive: false);

        Assert.Throws<InvalidOperationException>(() => file.ClaimInitialWrite(provider));
    }

    [Fact]
    public void LockReservation_PreventsDuplicateOwnersAndSupportsCleanupAfterDispose()
    {
        var provider = new object();
        using var file = new PhysicalStoreFileHandle(provider, new CountingSafeHandle(), createdExclusive: false);

        Assert.Equal(PhysicalStoreLockReservationResult.Acquired, file.TryReserveLock(provider));
        Assert.Equal(PhysicalStoreLockReservationResult.Busy, file.TryReserveLock(provider));
        var foreign = Assert.Throws<PackageStoreAdmissionException>(
            () => file.ReleaseLockReservation(new object()));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreign.Reason);
        Assert.Equal(PhysicalStoreLockReservationResult.Busy, file.TryReserveLock(provider));
        file.ReleaseLockReservation(provider);
        Assert.Equal(PhysicalStoreLockReservationResult.Acquired, file.TryReserveLock(provider));

        file.Dispose();
        var expired = Assert.Throws<PackageStoreAdmissionException>(() => file.TryReserveLock(provider));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        file.ReleaseLockReservation(provider);
        Assert.Throws<InvalidOperationException>(() => file.ReleaseLockReservation(provider));
    }

    [Fact]
    public void LockReservation_WhenReleaseCannotBeConfirmed_PoisonsTheHandle()
    {
        var provider = new object();
        using var file = new PhysicalStoreFileHandle(provider, new CountingSafeHandle(), createdExclusive: false);

        Assert.Equal(PhysicalStoreLockReservationResult.Acquired, file.TryReserveLock(provider));
        file.MarkLockReleaseFailed(provider);

        Assert.Equal(PhysicalStoreLockReservationResult.Poisoned, file.TryReserveLock(provider));
        Assert.Throws<InvalidOperationException>(() => file.ReleaseLockReservation(provider));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("root/child")]
    [InlineData("root\\child")]
    [InlineData("C:")]
    [InlineData("entry:stream")]
    [InlineData("entry\0")]
    public void ValidateSingleComponent_PathOrNamespaceSyntax_RefusesBeforeNativeUse(string name)
    {
        Assert.Throws<ArgumentException>(() => PhysicalStoreNames.ValidateSingleComponent(name));
    }

    [Theory]
    [InlineData(".nuplane-control")]
    [InlineData("store-state.json")]
    [InlineData("naïve")]
    public void ValidateSingleComponent_OrdinaryName_IsAccepted(string name)
    {
        PhysicalStoreNames.ValidateSingleComponent(name);
    }

    private sealed class CountingSafeHandle : SafeHandle
    {
        private int _releaseCount;

        internal CountingSafeHandle() : base(new IntPtr(-1), ownsHandle: true)
        {
            SetHandle(new IntPtr(17));
        }

        public override bool IsInvalid => handle == new IntPtr(-1);
        internal int ReleaseCount => Volatile.Read(ref _releaseCount);

        protected override bool ReleaseHandle()
        {
            Interlocked.Increment(ref _releaseCount);
            return true;
        }
    }
}
