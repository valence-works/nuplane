using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

internal sealed partial class NuplaneSetupFeedDiagnosticReporter : IPostConfigureOptions<NuplaneSetupOptions>
{
    private readonly INuplaneSetupFeedDeclarationSource _feedDeclarationSource;
    private readonly ILogger<NuplaneSetupFeedDiagnosticReporter> _logger;

    public NuplaneSetupFeedDiagnosticReporter(
        INuplaneSetupFeedDeclarationSource feedDeclarationSource,
        ILogger<NuplaneSetupFeedDiagnosticReporter> logger)
    {
        _feedDeclarationSource = feedDeclarationSource ?? throw new ArgumentNullException(nameof(feedDeclarationSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void PostConfigure(string? name, NuplaneSetupOptions options)
    {
        foreach (var diagnostic in _feedDeclarationSource.Read().Diagnostics
                     .Where(static diagnostic => diagnostic.Severity == NuplaneFeedSetupDiagnosticSeverity.Warning))
        {
            SetupFeedWarning(
                _logger,
                diagnostic.Code,
                diagnostic.FeedName,
                diagnostic.ConfigurationPath,
                diagnostic.Message);
        }
    }

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Warning,
        Message = "Nuplane setup feed diagnostic {Code} for feed {FeedName} at {ConfigurationPath}: {DiagnosticMessage}")]
    private static partial void SetupFeedWarning(
        ILogger logger,
        string code,
        string? feedName,
        string configurationPath,
        string diagnosticMessage);
}
