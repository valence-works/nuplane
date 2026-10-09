using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageDirectoryAllowMissingSuffixTests
{
    [SupportedPhysicalStoreFact]
    public void Resolve_WhenOrdinaryUnenrolledPackageSuffixIsMissing_RetainsTheAbsentEdgeWithoutCreatingIt()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var resolver = new PackageStoreAuthorityResolver(files, registry);
        var missingPackage = Path.Combine(fixture.PackageInstallRoot, "future-parent", "nested", "package");

        using var result = resolver.Resolve(missingPackage, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix);

        Assert.Null(result.RootIdentity);
        Assert.Null(result.MembershipCandidate);
        Assert.True(result.IsProspectiveMissingSuffix);
        Assert.False(result.IsProspectiveConfiguredRoot);
        Assert.Equal(files.InspectHandle(root).Identity, files.InspectHandle(Assert.IsType<PhysicalStoreDirectoryHandle>(result.Target)).Identity);
        result.Revalidate();
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public async Task Resolve_WhenSuffixIsMissingBelowCompleteAuthority_RefusesInsteadOfInferringUnenrolled()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var missingPackage = Path.Combine(context.SharedInstallPath, "missing-package");
        var resolver = new PackageStoreAuthorityResolver(context.Files, context.Registry);

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(missingPackage, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.False(Directory.Exists(missingPackage));
    }

    [SupportedPhysicalStoreFact]
    public async Task Resolve_WhenSuffixIsMissingBelowIncompleteAuthority_RefusesBeforePackageContentCanBeRead()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var missingPackage = Path.Combine(context.SharedInstallPath, "missing-package");
        var resolver = new PackageStoreAuthorityResolver(context.Files, context.Registry);

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(missingPackage, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, error.Reason);
        Assert.False(Directory.Exists(missingPackage));
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenFinalDirectoryAliasTargetsAnUnenrolledDirectory_StillRefusesTheAlias()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var outside = fixture.CreateDirectory("package-target");
        var alias = Path.Combine(fixture.PackageInstallRoot, "package-alias");
        Directory.CreateSymbolicLink(alias, outside);
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(alias, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenMissingPackageNameCouldBeReservedControlDirectory_RefusesFirstAndLaterEdges()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));
        var targets = new[]
        {
            Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName),
            Path.Combine(fixture.PackageInstallRoot, "future-parent", RootMembershipRegistry.ControlDirectoryName)
        };

        foreach (var target in targets)
        {
            var error = Assert.Throws<PackageStoreAdmissionException>(() =>
                resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        }

        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenLaterMissingNameHasNoObservableNativeProfile_RefusesToGuessUnicodeAliases()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var target = Path.Combine(fixture.PackageInstallRoot, "future-parent", "caf\u00E9-package");
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenTheFirstMissingNameUsesAnObservedCaseInsensitiveAliasOfControl_RefusesIt()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var semantics = ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent);
        var target = Path.Combine(fixture.PackageInstallRoot, ".NUPLANE-STORE");
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));

        if (semantics.CaseSensitive && !semantics.NormalizationInsensitive)
        {
            using var result = resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix);
            Assert.Null(result.RootIdentity);
        }
        else
        {
            var error = Assert.Throws<PackageStoreAdmissionException>(() =>
                resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        }

        Assert.False(Directory.Exists(target));
    }

    [LinuxExt4CasefoldFact]
    public void Resolve_OnExt4Casefold_RefusesUnicodeAliasOfReservedControlName()
    {
        using var fixture = new PackageStoreFixture();
        var files = new UnixPhysicalStoreFileSystem();
        UnixPhysicalStoreIdentityTests.EnableExt4Casefold(fixture.PackageInstallRoot);
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var semantics = ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent);
        Assert.Equal("linux-ext4-casefold-v1", semantics.ProfileId);
        var target = Path.Combine(fixture.PackageInstallRoot, ".nuplane-\u017Ftore");
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName)));
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenRelativePackagePathHasAnExplicitCapturedBase_PreservesTheRawComponents()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));
        const string relative = "./future-parent/nested-package";

        using var result = resolver.Resolve(relative, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix,
            exactBaseLocator: fixture.PackageInstallRoot);

        Assert.True(result.IsProspectiveMissingSuffix);
        result.Revalidate();
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public void Resolve_WhenAnObservedMissingEdgeAppearsBeforeReplay_RefusesTheStaleObservation()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var resolver = new PackageStoreAuthorityResolver(files, new RootMembershipRegistry(files, new StoreStateSerializer()));
        var firstMissing = Path.Combine(fixture.PackageInstallRoot, "future-package-parent");
        var target = Path.Combine(firstMissing, "nested", "package");
        using var result = resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix);
        Directory.CreateDirectory(firstMissing);

        var error = Assert.Throws<PackageStoreAdmissionException>(result.Revalidate);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.False(Directory.Exists(target));
    }

    [SupportedPhysicalStoreFact]
    public async Task Resolve_WhenMissingSuffixEscapesACompleteAuthorityThroughAnAlias_RefusesTheEscape()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var outside = context.Fixture.CreateDirectory("outside-package-parent");
        var alias = Path.Combine(context.Fixture.PackageInstallRoot, "outside-alias");
        Directory.CreateSymbolicLink(alias, outside);
        var target = Path.Combine(alias, "missing-package");
        var resolver = new PackageStoreAuthorityResolver(context.Files, context.Registry);

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            resolver.Resolve(target, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, error.Reason);
        Assert.False(Directory.Exists(Path.Combine(outside, "missing-package")));
    }

    private static IPhysicalStoreFileSystem CreateFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
}
