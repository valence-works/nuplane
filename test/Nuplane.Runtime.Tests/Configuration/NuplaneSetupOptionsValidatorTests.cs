using Microsoft.Extensions.Configuration;
using Nuplane.Setup;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class NuplaneSetupOptionsValidatorTests
{
    private readonly NuplaneSetupOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions { StateFilePath = "  " });

        Assert.True(result.Failed);
        Assert.Contains("StateFilePath cannot be blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_EmptyStringStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions { StateFilePath = "" });

        Assert.True(result.Failed);
        Assert.Contains("StateFilePath cannot be blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_ValidStateFilePath_Succeeds()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions { StateFilePath = "./state.json" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_UseInMemoryStoreOnly_Succeeds()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions { UseInMemoryStore = true });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_UseInMemoryStoreWithStateFilePath_Fails()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions
        {
            UseInMemoryStore = true,
            StateFilePath = "./state.json"
        });

        Assert.True(result.Failed);
        Assert.Contains("UseInMemoryStore cannot be combined", result.FailureMessage);
    }

    [Fact]
    public void Validate_UseInMemoryStoreWithNullPath_Succeeds()
    {
        var result = _sut.Validate(null, new NuplaneSetupOptions
        {
            UseInMemoryStore = true,
            StateFilePath = null
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_KeyedFeedWithoutName_Succeeds()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:nuget.org:ServiceIndex"] = "https://api.nuget.org/v3/index.json"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_KeyedFeedWithMismatchedName_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:feedz.io:Name"] = "other-feed",
            ["Nuplane:Setup:Feeds:feedz.io:ServiceIndex"] = "https://feedz.example/v3/index.json"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("feedz.io", result.FailureMessage);
        Assert.Contains("other-feed", result.FailureMessage);
    }

    [Fact]
    public void Validate_KeyedFeedWithBothSourceTypes_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:local-packages:ServiceIndex"] = "https://feed.example/v3/index.json",
            ["Nuplane:Setup:Feeds:local-packages:DirectoryPath"] = "packages"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("exactly one of DirectoryPath or ServiceIndex", result.FailureMessage);
    }

    [Fact]
    public void Validate_KeyedFeedWithMissingSourceType_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:empty-feed:IncludePatterns:0"] = "*"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("exactly one of DirectoryPath or ServiceIndex", result.FailureMessage);
    }

    [Fact]
    public void Validate_KeyedFeedWithInvalidServiceIndex_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:nuget.org:ServiceIndex"] = "not-a-uri"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("invalid absolute ServiceIndex URI", result.FailureMessage);
    }

    [Fact]
    public void Validate_RemoteFeedWithZeroDirectoryDebounceWindow_Succeeds()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:nuget.org:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
            ["Nuplane:Setup:Feeds:nuget.org:Directory:DebounceWindow"] = "00:00:00"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_KeyedFeedWithBlankDirectoryPath_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:local-packages:DirectoryPath"] = "  "
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("exactly one of DirectoryPath or ServiceIndex", result.FailureMessage);
    }

    [Fact]
    public void Validate_KeyedDirectoryFeedWithInvalidRole_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:local-packages:DirectoryPath"] = "packages",
            ["Nuplane:Setup:Feeds:local-packages:Directory:Role"] = "99"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("Directory.Role must be a valid directory feed role", result.FailureMessage);
    }

    [Fact]
    public void Validate_DuplicateArrayFeedNamesWithRawSource_Fails()
    {
        var sut = CreateValidator(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:0:Name"] = "nuget.org",
            ["Nuplane:Setup:Feeds:0:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
            ["Nuplane:Setup:Feeds:1:Name"] = "NuGet.Org",
            ["Nuplane:Setup:Feeds:1:ServiceIndex"] = "https://mirror.example/v3/index.json"
        });

        var result = sut.Validate(null, new NuplaneSetupOptions());

        Assert.True(result.Failed);
        Assert.Contains("duplicate feed name", result.FailureMessage);
    }

    private static NuplaneSetupOptionsValidator CreateValidator(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new(new ConfigurationNuplaneSetupFeedDeclarationSource(configuration.GetSection("Nuplane:Setup")));
    }
}
