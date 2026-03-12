using NuGet.Versioning;
using Nuplane.Feeds.Versioning;

namespace Nuplane.NuGet.Tests;

public sealed class NuGetFeedVersionEnumeratorTests
{
    [Fact]
    public void NormalizeVersions_ReturnsSemverSortedNormalizedStrings()
    {
        var normalized = NuGetFeedVersionEnumerator.NormalizeVersions(
        [
            NuGetVersion.Parse("2.0.0"),
            NuGetVersion.Parse("1.0"),
            NuGetVersion.Parse("1.0.0-beta"),
            NuGetVersion.Parse("1.0.1")
        ]);

        Assert.Equal(["1.0.0-beta", "1.0.0", "1.0.1", "2.0.0"], normalized);
    }

    [Fact]
    public void NormalizeVersions_EmptyFeed_ReturnsEmptyList()
    {
        Assert.Empty(NuGetFeedVersionEnumerator.NormalizeVersions([]));
    }

    [Fact]
    public void NormalizeVersions_NullEntrySequence_Propagates()
    {
        Assert.Throws<ArgumentNullException>(() => NuGetFeedVersionEnumerator.NormalizeVersions(null!));
    }
}
