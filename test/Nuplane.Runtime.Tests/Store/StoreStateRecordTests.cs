using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Store;

public sealed class StoreStateRecordTests
{
    private const string Recorded = "sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string Other = "sha512:AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ==";

    [Theory]
    [InlineData("1.0.0-Beta", "1.0.0-beta")]
    [InlineData("1.0", "1.0.0.0")]
    [InlineData("1.0.0-preview.1+ci.7", "1.0.0-Preview.1+ci.8")]
    public void ArchiveHashKey_SpellingsOfOneVersion_ShareAKey(string first, string second)
    {
        Assert.Equal(StoreStateRecord.ArchiveHashKey("Acme.Caf\u00E9", first),
            StoreStateRecord.ArchiveHashKey("ACME.CAFE\u0301", second));
        Assert.NotEqual(StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0"),
            StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.1"));
    }

    [Theory]
    [InlineData("acme.addon/1.0.0")]
    [InlineData("acme.addon/1.0")]
    public void NextArchiveHashes_RecordedVersion_KeepsTheFirstValue(string recordedKey)
    {
        var state = StoreStateRecord.Empty() with
        {
            ArchiveHashesByPackage = new(StringComparer.Ordinal) { [recordedKey] = Recorded }
        };

        var next = state.NextArchiveHashes([Descriptor("ACME.ADDON", "1.0.0", Other), Descriptor("Acme.Other", "2.0.0", Other)]);

        Assert.Equal(new Dictionary<string, string>
        {
            [recordedKey] = Recorded,
            ["acme.other/2.0.0"] = Other
        }, next);
    }

    [Fact]
    public void NextArchiveHashes_ConflictingHistoryForEquivalentVersions_RefusesTheHistory()
    {
        var state = StoreStateRecord.Empty() with
        {
            ArchiveHashesByPackage = new(StringComparer.Ordinal)
            {
                ["acme.addon/1.0"] = Recorded,
                ["acme.addon/1.0.0"] = Other
            }
        };

        Assert.Throws<InvalidDataException>(() => state.NextArchiveHashes([]));
    }

    [Fact]
    public void WithRecordedArchiveHashes_DescriptorDisagreeingWithTheRecord_TakesTheRecordedValue()
    {
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acme.addon/1.0"] = Recorded
        };
        var descriptor = Descriptor("Acme.Addon", "1.0.0+build.7", Other);

        var descriptors = StoreStateRecord.WithRecordedArchiveHashes(
            new Dictionary<string, ActivePackageDescriptor> { ["Acme.Addon"] = descriptor }, recorded);

        Assert.Equal(descriptor with { ArchiveHash = Recorded }, descriptors["Acme.Addon"]);
    }

    private static ActivePackageDescriptor Descriptor(string packageId, string version, string hash) =>
        new(packageId, version, "feed", "source", $"/packages/feed/{packageId}/{version}", DateTimeOffset.UnixEpoch, "correlation")
        {
            ArchiveHash = hash
        };
}
