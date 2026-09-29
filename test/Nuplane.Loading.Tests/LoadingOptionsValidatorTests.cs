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
    public void Validate_InvalidLoadModeSelectionPolicy_ReturnsError()
    {
        var sut = new LoadingOptionsValidator();
        var options = new LoadingOptions
        {
            LoadModeSelectionPolicy = (PackageLoadModeSelectionPolicy)42
        };

        var errors = sut.Validate(options);

        Assert.Contains(errors, error => error.Contains("selection policy", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void Validate_PackageLoadModeOverrideWithSurroundingWhitespace_ReturnsError()
    {
        var sut = new LoadingOptionsValidator();
        var options = new LoadingOptions();
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a ", LoadMode = PackageLoadMode.HostIntegrated });

        var errors = sut.Validate(options);

        Assert.Contains(errors, error => error.Contains("leading or trailing whitespace", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_UnsignedSharedAssembly_ReturnsNoErrors(string? publicKeyToken)
    {
        var errors = ValidateSharedAssembly(publicKeyToken);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("31bf3856ad364e35")]
    [InlineData("31BF3856AD364E35")]
    public void Validate_SignedSharedAssemblyWithSixteenHexToken_ReturnsNoErrors(string publicKeyToken)
    {
        var errors = ValidateSharedAssembly(publicKeyToken);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("31bf3856ad364e3")]
    [InlineData("31bf3856ad364e35a")]
    [InlineData("31bf3856ad364e3g")]
    public void Validate_SharedAssemblyWithMalformedToken_ReturnsError(string publicKeyToken)
    {
        var errors = ValidateSharedAssembly(publicKeyToken);

        Assert.Contains(errors, error => error.Contains("16-char hex public key token", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ValidateSharedAssembly(string? publicKeyToken)
    {
        var options = new LoadingOptions();
        options.SharedAssemblies.Add(new SharedAssemblyIdentity("Acme.Contracts", publicKeyToken, 1));

        return new LoadingOptionsValidator().Validate(options);
    }
}
