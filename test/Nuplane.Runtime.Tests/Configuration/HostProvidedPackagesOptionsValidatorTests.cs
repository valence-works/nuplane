using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Validation;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class HostProvidedPackagesOptionsValidatorTests
{
    private readonly HostProvidedPackagesOptionsValidator _sut = new();

    private static HostProvidedPackagesOptions WithEntries(params string[] entries)
    {
        var options = new HostProvidedPackagesOptions();
        options.Entries.Clear();
        foreach (var entry in entries)
        {
            options.Entries.Add(entry);
        }

        return options;
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new HostProvidedPackagesOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_EmptyEntries_Succeeds()
    {
        var result = _sut.Validate(null, WithEntries());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ExactIdEntry_Succeeds()
    {
        var result = _sut.Validate(null, WithEntries("Acme.Contracts"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_PrefixEntry_Succeeds()
    {
        var result = _sut.Validate(null, WithEntries("Acme."));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankEntry_Fails()
    {
        var result = _sut.Validate(null, WithEntries(" "));

        Assert.True(result.Failed);
        Assert.Contains("must not be empty or whitespace", result.FailureMessage);
    }

    [Fact]
    public void Validate_EntryContainingWhitespace_Fails()
    {
        var result = _sut.Validate(null, WithEntries("Acme Contracts"));

        Assert.True(result.Failed);
        Assert.Contains("Acme Contracts", result.FailureMessage);
        Assert.Contains("must not contain whitespace", result.FailureMessage);
    }

    [Fact]
    public void Validate_BarePrefixDot_Fails()
    {
        var result = _sut.Validate(null, WithEntries("."));

        Assert.True(result.Failed);
        Assert.Contains("names no segment before the trailing '.'", result.FailureMessage);
    }

    [Fact]
    public void Validate_PrefixWithEmptySegment_Fails()
    {
        var result = _sut.Validate(null, WithEntries("Acme..Contracts."));

        Assert.True(result.Failed);
        Assert.Contains("names no segment before the trailing '.'", result.FailureMessage);
    }

    [Fact]
    public void Validate_DuplicateEntriesCaseInsensitively_Succeeds()
    {
        var result = _sut.Validate(null, WithEntries("Acme.Contracts", "acme.contracts", "ACME.CONTRACTS"));

        Assert.True(result.Succeeded);
    }
}
