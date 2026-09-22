using Microsoft.Extensions.Configuration;
using Nuplane.Capabilities;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class CapabilitySelectionConfigurationReaderTests
{
    private static CapabilityOptions Populate(Dictionary<string, string?> data)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var options = new CapabilityOptions();
        CapabilitySelectionConfigurationReader.Populate(options, configuration.GetSection("Nuplane:Capabilities"));
        return options;
    }

    [Fact]
    public void Populate_EmptySection_BindsNothing()
    {
        var options = Populate([]);

        Assert.Empty(options.Selections);
        Assert.Empty(options.ConfigurationErrors);
    }

    [Fact]
    public void Populate_SimpleForm_BindsSingleOption()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider"] = "PostgreSql"
        });

        var selection = Assert.Single(options.Selections);
        Assert.Equal("ef-provider", selection.Key);
        Assert.Equal(["PostgreSql"], selection.Value.Options);
        Assert.Null(selection.Value.Version);
        Assert.Null(selection.Value.Feed);
    }

    [Fact]
    public void Populate_SimpleFormCommaSeparated_BindsEveryOption()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider"] = "PostgreSql, Sqlite"
        });

        var selection = Assert.Single(options.Selections);
        Assert.Equal(["PostgreSql", "Sqlite"], selection.Value.Options);
    }

    [Fact]
    public void Populate_ObjectForm_BindsOptionVersionAndFeed()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider:Option"] = "PostgreSql",
            ["Nuplane:Capabilities:ef-provider:Version"] = "[10.0.0]",
            ["Nuplane:Capabilities:ef-provider:Feed"] = "nuget"
        });

        var selection = Assert.Single(options.Selections);
        Assert.Equal(["PostgreSql"], selection.Value.Options);
        Assert.Equal("[10.0.0]", selection.Value.Version);
        Assert.Equal("nuget", selection.Value.Feed);
    }

    [Fact]
    public void Populate_ObjectFormWithCommaSeparatedOption_BindsEveryOption()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider:Option"] = "PostgreSql,Sqlite"
        });

        var selection = Assert.Single(options.Selections);
        Assert.Equal(["PostgreSql", "Sqlite"], selection.Value.Options);
    }

    [Fact]
    public void Populate_ValueAndOptionChildAgree_BindsWithoutError()
    {
        // A value and an Option child at the same path can both be set when configuration is layered
        // from more than one provider (for example an environment variable alongside appsettings);
        // agreeing is not a conflict.
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider"] = "PostgreSql",
            ["Nuplane:Capabilities:ef-provider:Option"] = "PostgreSql",
            ["Nuplane:Capabilities:ef-provider:Version"] = "[10.0.0]"
        });

        var selection = Assert.Single(options.Selections);
        Assert.Equal(["PostgreSql"], selection.Value.Options);
        Assert.Equal("[10.0.0]", selection.Value.Version);
        Assert.Empty(options.ConfigurationErrors);
    }

    [Fact]
    public void Populate_ValueAndOptionChildDisagree_RecordsConfigurationErrorNamingTheKey()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider"] = "PostgreSql",
            ["Nuplane:Capabilities:ef-provider:Option"] = "Sqlite"
        });

        Assert.Empty(options.Selections);
        var error = Assert.Single(options.ConfigurationErrors);
        Assert.Contains("Nuplane:Capabilities:ef-provider", error, StringComparison.Ordinal);
        Assert.Contains("PostgreSql", error, StringComparison.Ordinal);
        Assert.Contains("Sqlite", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Populate_NoValueOrOptionChild_RecordsConfigurationErrorNamingTheKey()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider:Version"] = "[10.0.0]"
        });

        Assert.Empty(options.Selections);
        var error = Assert.Single(options.ConfigurationErrors);
        Assert.Contains("Nuplane:Capabilities:ef-provider must select at least one option", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Populate_MultipleCapabilities_BindsEachIndependently()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:ef-provider"] = "PostgreSql",
            ["Nuplane:Capabilities:message-broker:Option"] = "RabbitMq",
            ["Nuplane:Capabilities:message-broker:Feed"] = "internal"
        });

        Assert.Equal(2, options.Selections.Count);
        Assert.Equal(["PostgreSql"], options.Selections["ef-provider"].Options);
        Assert.Equal(["RabbitMq"], options.Selections["message-broker"].Options);
        Assert.Equal("internal", options.Selections["message-broker"].Feed);
    }

    [Fact]
    public void Populate_CapabilityNameKeyIsCaseInsensitive()
    {
        var options = Populate(new()
        {
            ["Nuplane:Capabilities:Ef-Provider"] = "PostgreSql"
        });

        Assert.True(options.Selections.ContainsKey("ef-provider"));
        Assert.True(options.Selections.ContainsKey("EF-PROVIDER"));
    }
}
