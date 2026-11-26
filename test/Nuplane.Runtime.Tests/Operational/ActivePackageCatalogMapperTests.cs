using Nuplane.Abstractions;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class ActivePackageCatalogMapperTests
{
    private const string Recorded = "sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string FromDisk = "sha512:AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ==";

    [Theory]
    [InlineData("feed", "/packages/feed/Acme.Addon/1.0.0", "1.0.0", "1.0.0")]
    [InlineData("mirror", "/packages/mirror/Acme.Addon/1.0.0", "1.0", "1.0.0+build.7")]
    public void BuildNextDescriptors_RecordedVersion_UsesTheRecordOverTheHashReadFromDisk(
        string feedName, string installPath, string recordedVersion, string resolvedVersion)
    {
        // 1.0.0 is recorded but not active, as after a rollback through any feed or install path.
        var state = StoreStateRecord.Empty() with
        {
            ArchiveHashesByPackage = new(StringComparer.Ordinal) { [$"acme.addon/{recordedVersion}"] = Recorded }
        };
        var package = new ResolvedPackage("acme.addon", resolvedVersion, feedName, installPath, DateTimeOffset.UnixEpoch, "source")
        {
            PackageContentHash = FromDisk
        };
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["acme.addon"] = resolvedVersion };

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(state, versions, [package],
            new PackageChangeSet([package], [], [], "correlation", DateTimeOffset.UnixEpoch), "correlation",
            DateTimeOffset.UnixEpoch);

        var descriptor = descriptors["acme.addon"];
        Assert.Equal(Recorded, descriptor.ArchiveHash);
        Assert.Equal(resolvedVersion, descriptor.Version);
        Assert.Equal(installPath, descriptor.InstallPath);
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

        // Upgrading an otherwise unchanged activation fills only its missing hash.
        var existing = descriptors["Acme.Addon"] with { ArchiveHash = null };
        var priorState = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [package.Id] = existing }
        };
        var upgraded = ActivePackageCatalogMapper.BuildNextDescriptors(priorState, versions, [package],
            new PackageChangeSet([], [], [], "upgrade", DateTimeOffset.UnixEpoch.AddDays(1)), "upgrade",
            DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(existing with { ArchiveHash = FromDisk }, upgraded[package.Id]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MapActivePackages_PersistedDescriptor_UsesRecordedHashWhenAvailable(bool hasRecord)
    {
        var descriptor = new ActivePackage("Acme.Addon", "1.0.0+build.7", "feed", "source",
            "/packages/feed/Acme.Addon/1.0.0", DateTimeOffset.UnixEpoch, "correlation") { ArchiveHash = FromDisk };
        var state = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor },
            ArchiveHashesByPackage = hasRecord ? new(StringComparer.Ordinal) { ["acme.addon/1.0"] = Recorded } : null
        };

        var active = Assert.Single(ActivePackageCatalogMapper.MapActivePackages(state));

        Assert.Equal(descriptor with { ArchiveHash = hasRecord ? Recorded : FromDisk }, active);
    }
}
