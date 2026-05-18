using Microsoft.Extensions.Configuration;
using Nuplane.Feeds.Setup;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class NuplaneFeedSetupDeclarationReaderTests
{
    [Fact]
    public void Read_EmptySetupSection_ReturnsEmptyResult()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var result = NuplaneFeedSetupDeclarationReader.Read(configuration.GetSection("Nuplane"));

        Assert.Empty(result.Declarations);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_KeyedRemoteFeedWithoutName_UsesKeyAsName()
    {
        var result = Read(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:nuget.org:ServiceIndex"] = "https://api.nuget.org/v3/index.json",
            ["Nuplane:Setup:Feeds:nuget.org:Credentials"] = "credentials://nuget",
            ["Nuplane:Setup:Feeds:nuget.org:IncludePatterns:0"] = "Elsa.*"
        });

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("nuget.org", declaration.Name);
        Assert.Equal(NuplaneFeedSetupSourceShape.Keyed, declaration.SourceShape);
        Assert.Equal("https://api.nuget.org/v3/index.json", declaration.Options.ServiceIndex);
        Assert.Equal("credentials://nuget", declaration.Options.Credentials);
        Assert.Equal("Elsa.*", Assert.Single(declaration.Options.IncludePatterns));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_KeyedDirectoryFeedWithoutName_PreservesDirectoryOptions()
    {
        var result = Read(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:local-packages:DirectoryPath"] = "packages",
            ["Nuplane:Setup:Feeds:local-packages:IncludePatterns:0"] = "*",
            ["Nuplane:Setup:Feeds:local-packages:Directory:Watch"] = "false",
            ["Nuplane:Setup:Feeds:local-packages:Directory:DebounceWindow"] = "00:00:02"
        });

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("local-packages", declaration.Name);
        Assert.Equal(NuplaneFeedSetupSourceShape.Keyed, declaration.SourceShape);
        Assert.Equal("packages", declaration.Options.DirectoryPath);
        Assert.Equal("*", Assert.Single(declaration.Options.IncludePatterns));
        Assert.False(declaration.Options.Directory.Watch);
        Assert.Equal(TimeSpan.FromSeconds(2), declaration.Options.Directory.DebounceWindow);
    }

    [Fact]
    public void Read_NumericChildKey_TreatsEntryAsArrayDeclaration()
    {
        var result = Read(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:0:Name"] = "nuget.org",
            ["Nuplane:Setup:Feeds:0:ServiceIndex"] = "https://api.nuget.org/v3/index.json"
        });

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("nuget.org", declaration.Name);
        Assert.Equal(NuplaneFeedSetupSourceShape.Array, declaration.SourceShape);
        Assert.Equal(0, declaration.ArrayIndex);
        Assert.Null(declaration.Key);
    }

    [Fact]
    public void Read_KeyedNameMismatch_AddsErrorDiagnostic()
    {
        var result = Read(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:feedz.io:Name"] = "other-feed",
            ["Nuplane:Setup:Feeds:feedz.io:ServiceIndex"] = "https://feedz.example/v3/index.json"
        });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(NuplaneFeedSetupDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("feedz.io", diagnostic.Message);
        Assert.Contains("other-feed", diagnostic.Message);
    }

    [Fact]
    public void Read_MixedArrayAndKeyedSameName_KeyedDeclarationWinsWithWarning()
    {
        var result = Read(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:Feeds:0:Name"] = "feedz.io",
            ["Nuplane:Setup:Feeds:0:ServiceIndex"] = "https://old.example/v3/index.json",
            ["Nuplane:Setup:Feeds:feedz.io:ServiceIndex"] = "https://new.example/v3/index.json"
        });

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("feedz.io", declaration.Name);
        Assert.Equal(NuplaneFeedSetupSourceShape.Keyed, declaration.SourceShape);
        Assert.Equal("https://new.example/v3/index.json", declaration.Options.ServiceIndex);
        Assert.Single(declaration.IgnoredArrayDeclarations);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(NuplaneFeedSetupDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("ignored", diagnostic.Message);
    }

    [Fact]
    public void Read_LayeredKeyedFeed_UsesEffectiveLaterProviderValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Setup:Feeds:feedz.io:ServiceIndex"] = "https://old.example/v3/index.json",
                ["Nuplane:Setup:Feeds:feedz.io:IncludePatterns:0"] = "Elsa.*"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Setup:Feeds:feedz.io:ServiceIndex"] = "https://new.example/v3/index.json",
                ["Nuplane:Setup:Feeds:feedz.io:IncludePatterns:0"] = "Elsa.Persistence.*"
            })
            .Build();

        var result = NuplaneFeedSetupDeclarationReader.Read(configuration.GetSection("Nuplane"));

        var declaration = Assert.Single(result.Declarations);
        Assert.Equal("https://new.example/v3/index.json", declaration.Options.ServiceIndex);
        Assert.Equal("Elsa.Persistence.*", Assert.Single(declaration.Options.IncludePatterns));
    }

    private static NuplaneFeedSetupReadResult Read(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return NuplaneFeedSetupDeclarationReader.Read(configuration.GetSection("Nuplane"));
    }
}
