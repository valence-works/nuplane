using Microsoft.Extensions.Configuration;
using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

/// <summary>
/// Reads setup feed declarations from an <see cref="IConfiguration"/> source.
/// </summary>
public sealed class ConfigurationNuplaneSetupFeedDeclarationSource : INuplaneSetupFeedDeclarationSource
{
    private readonly Lazy<NuplaneFeedSetupReadResult> _result;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationNuplaneSetupFeedDeclarationSource"/> class.
    /// </summary>
    /// <param name="configuration">The configuration source to read setup feed declarations from.</param>
    public ConfigurationNuplaneSetupFeedDeclarationSource(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _result = new(() => NuplaneFeedSetupDeclarationReader.Read(configuration));
    }

    /// <inheritdoc />
    public NuplaneFeedSetupReadResult Read() =>
        _result.Value;
}
