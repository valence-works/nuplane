using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.TestSupport;

internal sealed class EnrolledEmptyPackageStoreFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    internal string InstallRoot { get; private set; } = null!;
    internal IPackageStoreAdmission Admission { get; private set; } = null!;

    internal static async Task<EnrolledEmptyPackageStoreFixture> CreateAsync()
    {
        var fixture = new EnrolledEmptyPackageStoreFixture();
        try
        {
            fixture.InstallRoot = fixture._temp.CreateSubdirectory("packages");
            var stateParent = fixture._temp.CreateSubdirectory("state");
            var statePath = Path.Combine(stateParent, "state.json");
            var files = PackageStoreRuntimeAdmission.CreatePhysicalFileSystem();
            var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
            var resolver = new PackageStoreAuthorityResolver(files, registry);
            using var resolvedRoot = resolver.Resolve(fixture.InstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory);
            using var resolvedParent = resolver.Resolve(stateParent, PhysicalStorePathTarget.ConfiguredRootDirectory);
            var root = Assert.IsType<PhysicalStoreDirectoryHandle>(resolvedRoot.Target);
            var parent = Assert.IsType<PhysicalStoreDirectoryHandle>(resolvedParent.Target);
            var identity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
            var members = new[] { new RootMemberRecord("member", statePath, new RootMemberRecord.DeclaredBinding()) };
            registry.InitializeIncomplete(root, identity, 1, members, true, CancellationToken.None);
            await registry.BindDeclaredMembersAsync(root, identity, 1, members,
                new Dictionary<string, (PhysicalStoreDirectoryHandle, string)>
                {
                    ["member"] = (parent, Path.GetFileName(statePath))
                }, true, CancellationToken.None);

            var empty = StoreStateRecord.Empty();
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
            var candidate = new PackageProtectionRecord(1, identity, 1, "member", 1,
                ProtectionDigest.StateBody(empty), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, identity, 1, "member", 1,
                candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);
            await registry.WithQuiescentBoundIncompleteMemberLocationsAsync(root, identity, 1, true,
                async (locked, token) =>
                {
                    await locked.PublishStateAsync("member", empty with { ProtectionRecord = protection }, token);
                    return true;
                }, CancellationToken.None);
            await registry.CompleteEnrollmentAsync(root, identity, 1, true, CancellationToken.None);
            fixture.Admission = new PackageStoreAdmission(files, registry, fixture.InstallRoot);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    public void Dispose() => _temp.Dispose();
}
