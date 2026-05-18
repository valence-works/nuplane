using Microsoft.Extensions.Configuration;
using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

/// <summary>
/// Reads setup feed declarations from an <see cref="IConfiguration"/> source.
/// </summary>
public sealed class ConfigurationNuplaneSetupFeedDeclarationSource(IConfiguration configuration)
    : INuplaneSetupFeedDeclarationSource
{
    private readonly IConfiguration configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private NuplaneFeedSetupReadResult? result;

    /// <inheritdoc />
    public NuplaneFeedSetupReadResult Read() =>
        result ??= NuplaneFeedSetupDeclarationReader.Read(configuration);
}
