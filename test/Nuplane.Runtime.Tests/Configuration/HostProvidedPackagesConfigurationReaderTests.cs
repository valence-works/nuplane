using Microsoft.Extensions.Configuration;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class HostProvidedPackagesConfigurationReaderTests
{
    private static HostProvidedPackagesOptions Populate(Dictionary<string, string?> data)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var options = new HostProvidedPackagesOptions();
        HostProvidedPackagesConfigurationReader.Populate(options, configuration.GetSection("Nuplane:HostProvidedPackages"));
        return options;
    }

    [Fact]
    public void Populate_AbsentSection_LeavesDefaultEntries()
    {
        var options = Populate([]);

        Assert.Equal(HostProvidedPackagesOptions.DefaultEntries, options.Entries);
    }

    [Fact]
    public void Populate_EmptyArray_ClearsDefaultEntries()
    {
        // A JSON configuration provider registers the array key itself (empty string value, no
        // children) even for `"HostProvidedPackages": []`, so the section exists while binding no
        // elements. That is the shape simulated here: presence, not content, decides — an explicitly
        // empty list means the host declares no host-provided packages at all, not "use the
        // defaults".
        var options = Populate(new()
        {
            ["Nuplane:HostProvidedPackages"] = ""
        });

        Assert.Empty(options.Entries);
    }

    [Fact]
    public void Populate_IndexedKeys_ReplacesDefaultEntriesWithConfiguredOnes()
    {
        var options = Populate(new()
        {
            ["Nuplane:HostProvidedPackages:0"] = "Acme.Contracts",
            ["Nuplane:HostProvidedPackages:1"] = "Acme.Plugins."
        });

        Assert.Equal(["Acme.Contracts", "Acme.Plugins."], options.Entries);
    }

    [Fact]
    public void Populate_IndexedKeys_DoNotIncludeAnyDefaultEntry()
    {
        var options = Populate(new()
        {
            ["Nuplane:HostProvidedPackages:0"] = "Acme.Contracts"
        });

        Assert.DoesNotContain(options.Entries, entry => HostProvidedPackagesOptions.DefaultEntries.Contains(entry, StringComparer.OrdinalIgnoreCase));
    }
}
