using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Integration.Tests;

public sealed class CoordinatedContainerFilesystemTests
{
    [CoordinatedContainerFact]
    public void CoordinatedNameProfile_ContainerTmpfs_IsRefusedWithFilesystemAndProbe()
    {
        var files = new UnixPhysicalStoreFileSystem();
        using var root = files.OpenNamespaceRoot("/");
        using var dev = files.OpenDirectoryChildNoFollow(root, "dev");
        using var tmpfs = files.OpenDirectoryChildNoFollow(dev, "shm");

        var exception = Assert.Throws<PackageStoreAdmissionException>(
            () => ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(tmpfs));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, exception.Reason);
        Assert.Contains("fstatfs", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tmpfs", exception.Message, StringComparison.Ordinal);
    }

    private sealed class CoordinatedContainerFactAttribute : FactAttribute
    {
        public CoordinatedContainerFactAttribute()
        {
            if (!OperatingSystem.IsLinux() ||
                Environment.GetEnvironmentVariable("NUPLANE_COORDINATED_CONTAINER_TEST") != "1")
                Skip = "Requires the Linux coordinated package store container fixture.";
        }
    }
}
