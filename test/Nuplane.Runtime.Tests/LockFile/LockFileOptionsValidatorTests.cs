using Nuplane.Options.Validation;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Tests.LockFile;

public sealed class LockFileOptionsValidatorTests
{
    private readonly LockFileOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankPath_Fails()
    {
        var result = _sut.Validate(null, new LockFileOptions { Path = "   " });

        Assert.True(result.Failed);
        Assert.Contains("Lock file path must be provided", result.FailureMessage);
    }
}

