using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Trust;

namespace Nuplane.Runtime.Tests.Trust;

public sealed class AllowlistGateTests
{
    private readonly AllowlistGate _sut = new();

    [Fact]
    public void Enforce_AllPackagesPermitted_ReturnsAll()
    {
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = true,
            AllowedPackageIds = new(["alpha", "beta"], StringComparer.OrdinalIgnoreCase)
        };
        var requests = new[] { Req("alpha"), Req("beta") };

        var result = _sut.Enforce(requests, opts);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Enforce_WildcardAllowlist_ReturnsAll()
    {
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = true,
            AllowedPackageIds = new(["*"], StringComparer.OrdinalIgnoreCase)
        };
        var requests = new[] { Req("alpha"), Req("beta"), Req("gamma") };

        var result = _sut.Enforce(requests, opts);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Enforce_OnePackageBlocked_ThrowsAggregateException()
    {
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = true,
            AllowedPackageIds = new(["alpha"], StringComparer.OrdinalIgnoreCase)
        };
        var requests = new[] { Req("alpha"), Req("blocked-pkg") };

        var ex = Assert.Throws<AggregateException>(() => _sut.Enforce(requests, opts));
        Assert.Single(ex.InnerExceptions);
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions[0]);
        Assert.Contains("blocked-pkg", ex.InnerExceptions[0].Message);
    }

    [Fact]
    public void Enforce_AllPackagesBlocked_ThrowsAggregateExceptionWithAllErrors()
    {
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = true,
            AllowedPackageIds = new([], StringComparer.OrdinalIgnoreCase)
        };
        var requests = new[] { Req("alpha"), Req("beta") };

        var ex = Assert.Throws<AggregateException>(() => _sut.Enforce(requests, opts));
        Assert.Equal(2, ex.InnerExceptions.Count);
    }

    [Fact]
    public void Enforce_RejectUnallowlistedPackagesFalse_ReturnsAll()
    {
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = false,
            AllowedPackageIds = new([], StringComparer.OrdinalIgnoreCase)
        };
        var requests = new[] { Req("alpha"), Req("beta"), Req("gamma") };

        var result = _sut.Enforce(requests, opts);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void EnsureActiveStorePath_PathInsideRoot_DoesNotThrow()
    {
        var root = Path.GetTempPath();
        var inside = Path.Combine(root, "subdir", "package.dll");

        var ex = Record.Exception(() => _sut.EnsureActiveStorePath("my-pkg", inside, root));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureActiveStorePath_PathOutsideRoot_ThrowsInvalidOperationException()
    {
        var root = Path.Combine(Path.GetTempPath(), "trusted-root");
        var outside = Path.Combine(Path.GetTempPath(), "evil-dir", "package.dll");

        Assert.Throws<InvalidOperationException>(
            () => _sut.EnsureActiveStorePath("my-pkg", outside, root));
    }

    private static PackageRequest Req(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "src");
}
