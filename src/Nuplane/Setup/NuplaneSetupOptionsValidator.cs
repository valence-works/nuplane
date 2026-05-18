using Microsoft.Extensions.Options;
using Nuplane.Feeds.Setup;

namespace Nuplane.Setup;

internal sealed class NuplaneSetupOptionsValidator : IValidateOptions<NuplaneSetupOptions>
{
    private readonly INuplaneSetupFeedDeclarationSource? feedDeclarationSource;

    public NuplaneSetupOptionsValidator()
    {
    }

    public NuplaneSetupOptionsValidator(INuplaneSetupFeedDeclarationSource feedDeclarationSource)
    {
        this.feedDeclarationSource = feedDeclarationSource ?? throw new ArgumentNullException(nameof(feedDeclarationSource));
    }

    public ValidateOptionsResult Validate(string? name, NuplaneSetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        var errors = new List<string>();

        if (options.AutomaticReconciliation && options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Nuplane setup PollInterval must be greater than zero when AutomaticReconciliation is enabled.");
        }

        if (options.StateFilePath is not null && string.IsNullOrWhiteSpace(options.StateFilePath))
        {
            errors.Add("Nuplane setup StateFilePath cannot be blank when provided.");
        }

        if (options.UseInMemoryStore && !string.IsNullOrEmpty(options.StateFilePath))
        {
            errors.Add("Nuplane setup UseInMemoryStore cannot be combined with a non-empty StateFilePath.");
        }

        if (feedDeclarationSource is { } source)
        {
            var readResult = source.Read();
            var feedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            errors.AddRange(readResult.Diagnostics
                .Where(static diagnostic => diagnostic.Severity == NuplaneFeedSetupDiagnosticSeverity.Error)
                .Select(static diagnostic => diagnostic.Message));
            foreach (var declaration in readResult.Declarations)
            {
                if (string.IsNullOrWhiteSpace(declaration.Name))
                {
                    errors.Add($"Nuplane setup feed at '{declaration.ConfigurationPath}' must have a non-empty Name.");
                }
                else if (!feedNames.Add(declaration.Name))
                {
                    errors.Add($"Nuplane setup contains duplicate feed name '{declaration.Name}'.");
                }

                ValidateFeed(errors, declaration.Options, $"Nuplane setup feed '{declaration.Name}' at '{declaration.ConfigurationPath}'");
            }
        }
        else
        {
            var feedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < options.Feeds.Count; i++)
            {
                var feed = options.Feeds[i];
                var label = $"Nuplane setup feed at index {i}";

                if (string.IsNullOrWhiteSpace(feed.Name))
                {
                    errors.Add($"{label} must have a non-empty Name.");
                }
                else if (!feedNames.Add(feed.Name))
                {
                    errors.Add($"Nuplane setup contains duplicate feed name '{feed.Name}'.");
                }

                ValidateFeed(errors, feed, label);
            }
        }


        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateFeed(List<string> errors, NuplaneFeedSetupOptions feed, string label)
    {
        var hasDirectoryPath = !string.IsNullOrWhiteSpace(feed.DirectoryPath);
        var hasServiceIndex = !string.IsNullOrWhiteSpace(feed.ServiceIndex);

        if (hasDirectoryPath == hasServiceIndex)
        {
            errors.Add($"{label} must set exactly one of DirectoryPath or ServiceIndex.");
        }

        if (hasServiceIndex && !Uri.TryCreate(feed.ServiceIndex, UriKind.Absolute, out _))
        {
            errors.Add($"{label} has an invalid absolute ServiceIndex URI '{feed.ServiceIndex}'.");
        }

        if (feed.Directory.DebounceWindow <= TimeSpan.Zero)
        {
            errors.Add($"{label} Directory.DebounceWindow must be greater than zero.");
        }
    }
}
