using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreDirectoryNameSemanticsTests
{
    [SupportedPhysicalStoreFact]
    public void Observe_EmptyParentCreatesNothingAndMatchesCanonicalFileObservation()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var names = (IPhysicalStoreNameFileSystem)context.FileSystem;

        Assert.Empty(Directory.EnumerateFileSystemEntries(context.ParentPath));
        var semantics = names.ObserveDirectoryNameSemantics(context.Parent);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.ParentPath));

        PhysicalFileIdentity fileIdentity;
        using (var file = context.FileSystem.CreateFileExclusiveAt(context.Parent, "profile.json"))
            fileIdentity = context.FileSystem.InspectHandle(file).Identity;

        var canonical = names.ObserveCanonicalFileNameNoFollow(context.Parent, "profile.json", fileIdentity);
        Assert.Equal(semantics, canonical.Semantics);
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RejectsForeignAndClosedParentHandles()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var names = (IPhysicalStoreNameFileSystem)context.FileSystem;
        IPhysicalStoreNameFileSystem foreign = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();

        var foreignError = Assert.Throws<PackageStoreAdmissionException>(
            () => foreign.ObserveDirectoryNameSemantics(context.Parent));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignError.Reason);

        context.Parent.Dispose();
        var closedError = Assert.Throws<PackageStoreAdmissionException>(
            () => names.ObserveDirectoryNameSemantics(context.Parent));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, closedError.Reason);
    }
}
