using Nuplane.PackageArchives;

namespace Nuplane.Runtime.Tests;

public sealed class PackageArchiveReaderOptionsValidatorTests
{
    [Fact]
    public void Validate_DefaultOptions_Succeeds() =>
        Assert.True(new PackageArchiveReaderOptionsValidator().Validate(null, new PackageArchiveReaderOptions()).Succeeded);

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData((long)int.MaxValue, 0L)]
    [InlineData(1024L, -1L)]
    public void Validate_OutOfRangeBounds_Fails(long maximumArchiveBytes, long cacheBytes) =>
        Assert.True(new PackageArchiveReaderOptionsValidator().Validate(null, new PackageArchiveReaderOptions
        {
            MaximumArchiveBytes = maximumArchiveBytes,
            CacheBytes = cacheBytes
        }).Failed);
}