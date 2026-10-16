using System.Reflection;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Tests;

public sealed class PackageStoreProtectionContractTests
{
    [Fact]
    public void PhysicalIdentities_AreDescriptiveImmutableValuesWithValueEquality()
    {
        var firstFileIdentity = new PhysicalFileIdentity("test-provider", "volume-1", "file-42");
        var equalFileIdentity = new PhysicalFileIdentity("test-provider", "volume-1", "file-42");
        var root = new PhysicalRootIdentity(firstFileIdentity);
        var equalRoot = new PhysicalRootIdentity(equalFileIdentity);

        Assert.Equal(firstFileIdentity, equalFileIdentity);
        Assert.Equal(root, equalRoot);
        Assert.Same(firstFileIdentity, root.HandleIdentity);
        Assert.Equal("test-provider", firstFileIdentity.Provider);
        Assert.Equal("volume-1", firstFileIdentity.VolumeOrDeviceId);
        Assert.Equal("file-42", firstFileIdentity.FileId);
    }

    [Fact]
    public void PackageInstallIdentity_PreservesValidatedDescriptiveFields()
    {
        var root = new PhysicalRootIdentity(new PhysicalFileIdentity("provider", "volume", "root"));
        var directory = new PhysicalFileIdentity("provider", "volume", "directory");
        var identity = new PackageInstallIdentity(
            root,
            "Sample.Package",
            "2.3.4",
            "sample.package/2.3.4",
            directory,
            "completion-v1",
            "sha512:abc");

        Assert.Same(root, identity.Root);
        Assert.Same(directory, identity.DirectoryIdentity);
        Assert.Equal("Sample.Package", identity.PackageId);
        Assert.Equal("2.3.4", identity.Version);
        Assert.Equal("sample.package/2.3.4", identity.RootRelativeInstallPath);
        Assert.Equal("completion-v1", identity.CompletionIdentity);
        Assert.Equal("sha512:abc", identity.VerifiedArchiveHash);
        Assert.All(typeof(PackageInstallIdentity).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/package/1.0")]
    [InlineData("\\package\\1.0")]
    [InlineData("package\\1.0")]
    [InlineData("package//1.0")]
    [InlineData("package/./1.0")]
    [InlineData("package/../1.0")]
    [InlineData("C:/outside")]
    [InlineData("package:stream")]
    [InlineData("package\0/1.0")]
    public void PackageInstallIdentity_RejectsPathsThatAreNotNormalizedRelativeNames(string installPath)
    {
        var root = new PhysicalRootIdentity(new PhysicalFileIdentity("provider", "volume", "root"));
        var directory = new PhysicalFileIdentity("provider", "volume", "directory");

        Assert.Throws<ArgumentException>(() => new PackageInstallIdentity(
            root,
            "Sample.Package",
            "1.0.0",
            installPath,
            directory,
            "completion-v1"));
    }

    [Fact]
    public void AuthorityBearingTypes_DoNotExposePublicConstructorsOrOwnerFromBorrow()
    {
        var authorityTypes = new[]
        {
            typeof(PackageStoreRootOperationAdmission),
            typeof(PackageStorePathAdmission),
            typeof(PackageStorePathAdmissionEntry),
            typeof(PackageStoreOperationOwner),
            typeof(PackageStoreOperationBorrow),
            typeof(PackageGraphUseSnapshot),
            typeof(PackageGraphRootSelection),
            typeof(PackageGraphNodeIdentity),
            typeof(PackageGraphEdgeIdentity),
            typeof(PackageGraphUseLeaseOwner),
            typeof(PackageGraphUseLease)
        };

        Assert.All(authorityTypes, type =>
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)));
        Assert.Null(typeof(PackageStoreOperationBorrow).GetProperty(
            "Owner",
            BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(PackageGraphUseLease)));
    }
}
