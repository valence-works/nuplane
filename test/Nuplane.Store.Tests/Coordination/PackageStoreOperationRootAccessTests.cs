using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreOperationRootAccessTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_AwaitAndDisposedBorrow_RetainsEveryNativeLockUntilReplayEnds()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? closing = null;
        var operation = PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, async (files, root, _) =>
        {
            Assert.Same(fixture.Context.Files, files);
            Assert.Equal(fixture.Context.RootIdentity.HandleIdentity, files.InspectHandle(root).Identity);
            fixture.Borrow.Dispose();
            closing = fixture.Owner.DisposeAsync().AsTask();
            entered.TrySetResult(true);
            await resume.Task;
            Assert.False(closing.IsCompleted);
            await fixture.AssertEveryLockBusyAsync();
            return files.InspectHandle(root).Identity;
        });

        try
        {
            await entered.Task.WaitAsync(WaitLimit);
            Assert.False(closing!.IsCompleted);
            resume.TrySetResult(true);
            Assert.Equal(fixture.Context.RootIdentity.HandleIdentity, await operation.WaitAsync(WaitLimit));
            await closing.WaitAsync(WaitLimit);
        }
        finally
        {
            resume.TrySetResult(true);
            await operation.WaitAsync(WaitLimit);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_ExpiredBorrow_RefusesBeforeCallback()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        fixture.Borrow.Dispose();
        var calls = 0;

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (_, _, _) => Task.FromResult(++calls)));

        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, error.Reason);
        Assert.Equal(0, calls);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_InvalidMemberPayload_RefusesBeforeCallback()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        File.WriteAllText(fixture.Context.StatePaths["second"], "{}");
        var calls = 0;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (_, _, _) => Task.FromResult(++calls)));

        Assert.Equal(0, calls);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_MemberChangedDuringCallback_RefusesReturnedResult()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var calls = 0;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (_, _, _) =>
            {
                calls++;
                File.WriteAllText(fixture.Context.StatePaths["second"], "{}");
                return Task.FromResult(true);
            }));

        Assert.Equal(1, calls);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_CallbackFailsAfterMemberChanged_ReplayRefusalWins()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var calls = 0;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.WithValidatedRootAsync<bool>(fixture.Borrow, (_, _, _) =>
            {
                calls++;
                File.WriteAllText(fixture.Context.StatePaths["second"], "{}");
                throw new IOException("callback failed after member changed");
            }));

        Assert.Equal(1, calls);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_CancellationAfterCallback_DoesNotReturnResult()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, (_, _, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                calls++;
                cancellation.Cancel();
                return Task.FromResult(true);
            }, cancellation.Token));

        Assert.Equal(1, calls);
        await fixture.AssertEveryLockBusyAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task WithValidatedRootAsync_AwaitedCallback_SerializesConfiguredStateAccess()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var members = PackageStoreOperationAccess.GetLockedMemberLocations(fixture.Borrow);
        var operation = PackageStoreOperationAccess.WithValidatedRootAsync(fixture.Borrow, async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await resume.Task;
            return true;
        });

        try
        {
            await entered.Task.WaitAsync(WaitLimit);
            var read = members.ReadConfiguredStateAsync(fixture.Context.StatePaths["first"], CancellationToken.None);
            Assert.False(read.IsCompleted);
            resume.TrySetResult(true);
            Assert.True(await operation.WaitAsync(WaitLimit));
            Assert.NotNull(await read.WaitAsync(WaitLimit));
        }
        finally
        {
            resume.TrySetResult(true);
            await operation.WaitAsync(WaitLimit);
        }
    }

    internal sealed class AdmissionFixture : IAsyncDisposable
    {
        private readonly PackageStoreRootOperationAdmission _admission;

        private AdmissionFixture(RootMembershipProtectionVerificationTests.Context context,
            PackageStoreRootOperationAdmission admission, RootMembershipRegistry registry)
        {
            Context = context;
            Registry = registry;
            _admission = admission;
            Owner = Assert.IsType<PackageStoreOperationOwner>(admission.Owner);
            Borrow = Owner.Borrow();
        }

        internal RootMembershipProtectionVerificationTests.Context Context { get; }
        internal RootMembershipRegistry Registry { get; }
        internal PackageStoreOperationOwner Owner { get; }
        internal PackageStoreOperationBorrow Borrow { get; }

        internal static async Task<AdmissionFixture> CreateAsync(
            Func<IPhysicalStoreFileSystem, IPhysicalStoreFileSystem>? decorate = null)
        {
            var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
            PackageStoreRootOperationAdmission? admission = null;
            try
            {
                var files = decorate?.Invoke(context.Files) ?? context.Files;
                var registry = ReferenceEquals(files, context.Files) ? context.Registry : new RootMembershipRegistry(files, new StoreStateSerializer());
                admission = await new PackageStoreAdmission(files, registry,
                    context.Fixture.PackageInstallRoot).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
                return new AdmissionFixture(context, admission, registry);
            }
            catch
            {
                try { if (admission is not null) await admission.DisposeAsync(); }
                finally { context.Dispose(); }
                throw;
            }
        }

        internal async Task AssertEveryLockBusyAsync()
        {
            var ledger = Context.Registry.ReadCandidate(Context.Root);
            var names = new[] { "root.lock" }.Concat(ledger.Members.Select(member =>
                PhysicalStoreLock.GetMemberLockName(Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding).StateSlot)));
            using var control = Context.Files.OpenDirectoryChildNoFollow(Context.Root, RootMembershipRegistry.ControlDirectoryName);
            foreach (var name in names)
            {
                using var file = Context.Files.OpenFileChildNoFollow(control, name, FileAccess.ReadWrite);
                await using var acquired = await Context.Files.TryAcquireExclusiveLock(file);
                Assert.Null(acquired);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Borrow.Dispose();
            try { await _admission.DisposeAsync(); }
            finally { Context.Dispose(); }
        }
    }
}
