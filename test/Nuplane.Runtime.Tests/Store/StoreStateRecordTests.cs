using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Store;

public sealed class StoreStateRecordTests
{
    private const string Recorded = "sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string Other = "sha512:AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ==";

    [Fact]
    public void ArchiveHashKey_SpellingsOfOneVersion_ShareAKey()
    {
        Assert.Equal(StoreStateRecord.ArchiveHashKey("Acme.Caf\u00E9", "1.0.0-Beta"),
            StoreStateRecord.ArchiveHashKey("ACME.CAFE\u0301", "1.0.0-beta"));
        Assert.NotEqual(StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0"),
            StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.1"));
    }

    [Fact]
    public void NextArchiveHashes_RecordedVersion_KeepsTheFirstValue()
    {
        var state = StoreStateRecord.Empty() with
        {
            ArchiveHashesByPackage = new(StringComparer.Ordinal) { [StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0")] = Recorded }
        };

        var next = state.NextArchiveHashes([Descriptor("ACME.ADDON", "1.0.0", Other), Descriptor("Acme.Other", "2.0.0", Other)]);

        Assert.Equal(Recorded, next![StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0")]);
        Assert.Equal(Other, next[StoreStateRecord.ArchiveHashKey("Acme.Other", "2.0.0")]);
    }

    [Fact]
    public void WithRecordedArchiveHashes_DescriptorDisagreeingWithTheRecord_TakesTheRecordedValue()
    {
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0")] = Recorded
        };

        var descriptors = StoreStateRecord.WithRecordedArchiveHashes(
            new Dictionary<string, ActivePackageDescriptor> { ["Acme.Addon"] = Descriptor("Acme.Addon", "1.0.0", Other) }, recorded);

        Assert.Equal(Recorded, descriptors["Acme.Addon"].ArchiveHash);
    }

    private static ActivePackageDescriptor Descriptor(string packageId, string version, string hash) =>
        new(packageId, version, "feed", "source", $"/packages/feed/{packageId}/{version}", DateTimeOffset.UnixEpoch, "correlation")
        {
            ArchiveHash = hash
        };
}
