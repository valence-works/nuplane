using Microsoft.Extensions.Options;

namespace Nuplane.Setup;

internal sealed class NuplaneSetupOptionsValidator : IValidateOptions<NuplaneSetupOptions>
{
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


        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
