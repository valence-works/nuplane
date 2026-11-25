using Nuplane.Abstractions;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class ActivePackageCatalogMapperTests
{
    private const string Recorded = "sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string FromDisk = "sha512:AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ==";

    [Theory]
    [InlineData("feed", "/packages/feed/Acme.Addon/1.0.0")]
    [InlineData("mirror", "/packages/mirror/Acme.Addon/1.0.0")]
    public void BuildNextDescriptors_RecordedVersion_UsesTheRecordOverTheHashReadFromDisk(string feedName, string installPath)
    {
        // 1.0.0 is recorded but not active, as after a rollback through any feed or install path.
        var state = StoreStateRecord.Empty() with
        {
            ArchiveHashesByPackage = new(StringComparer.Ordinal) { [StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0")] = Recorded }
        };
        var package = new ResolvedPackage("acme.addon", "1.0.0", feedName, installPath, DateTimeOffset.UnixEpoch, "source")
        {
            PackageContentHash = FromDisk
        };
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["acme.addon"] = "1.0.0" };

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(state, versions, [package],
            new PackageChangeSet([package], [], [], "correlation", DateTimeOffset.UnixEpoch), "correlation",
            DateTimeOffset.UnixEpoch);

        Assert.Equal(Recorded, descriptors["acme.addon"].ArchiveHash);
    }

    [Fact]
    public void BuildNextDescriptors_UnrecordedVersion_UsesTheAcquisitionRecord()
    {
        var package = new ResolvedPackage("Acme.Addon", "2.0.0", "feed", "/packages/feed/Acme.Addon/2.0.0",
            DateTimeOffset.UnixEpoch, "source") { PackageContentHash = FromDisk };
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Acme.Addon"] = "2.0.0" };

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(StoreStateRecord.Empty(), versions, [package],
            new PackageChangeSet([package], [], [], "correlation", DateTimeOffset.UnixEpoch), "correlation",
            DateTimeOffset.UnixEpoch);

        Assert.Equal(FromDisk, descriptors["Acme.Addon"].ArchiveHash);
    }
}
