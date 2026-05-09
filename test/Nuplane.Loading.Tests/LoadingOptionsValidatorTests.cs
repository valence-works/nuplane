namespace Nuplane.Loading.Tests;

public sealed class LoadingOptionsValidatorTests
{
    [Fact]
    public void Validate_DefaultOptions_ReturnsNoLoadModeErrors()
    {
        var sut = new LoadingOptionsValidator();

        var errors = sut.Validate(new LoadingOptions());

        Assert.DoesNotContain(errors, error => error.Contains("load mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InvalidDefaultLoadMode_ReturnsError()
    {
        var sut = new LoadingOptionsValidator();
        var options = new LoadingOptions
        {
            DefaultLoadMode = (PackageLoadMode)42
        };

        var errors = sut.Validate(options);

        Assert.Contains(errors, error => error.Contains("default load mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DuplicatePackageLoadModeOverrides_ReturnsError()
    {
        var sut = new LoadingOptionsValidator();
        var options = new LoadingOptions();
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.HostIntegrated });
        options.PackageLoadModes.Add(new() { PackageId = "PKG-A", LoadMode = PackageLoadMode.Collectible });

        var errors = sut.Validate(options);

        Assert.Contains(errors, error => error.Contains("Duplicate package load mode override", StringComparison.OrdinalIgnoreCase));
    }
}
