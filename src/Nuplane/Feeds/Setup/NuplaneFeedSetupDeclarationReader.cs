using Microsoft.Extensions.Configuration;
using Nuplane.Setup;

namespace Nuplane.Feeds.Setup;

/// <summary>
/// Reads setup feed declarations from raw configuration while preserving array and keyed shapes.
/// </summary>
public static class NuplaneFeedSetupDeclarationReader
{
    private const string MixedShapeOverrideCode = "NuplaneSetupFeedMixedShapeOverride";
    private const string KeyNameMismatchCode = "NuplaneSetupFeedKeyNameMismatch";
    private const string DuplicateKeyedFeedCode = "NuplaneSetupFeedDuplicateKeyed";

    /// <summary>
    /// Reads setup feed declarations from a <c>Nuplane</c>, <c>Setup</c>, or <c>Feeds</c> section.
    /// </summary>
    /// <param name="configuration">The configuration section to read.</param>
    /// <returns>The effective feed declarations and diagnostics.</returns>
    public static NuplaneFeedSetupReadResult Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var feedsSection = GetFeedsSection(configuration);
        var rawDeclarations = new List<NuplaneFeedSetupDeclaration>();
        var diagnostics = new List<NuplaneFeedSetupDiagnostic>();

        foreach (var feedSection in feedsSection.GetChildren())
        {
            var isArrayEntry = IsAllDigits(feedSection.Key);
            var options = new NuplaneFeedSetupOptions();
            feedSection.Bind(options);

            var declaration = isArrayEntry
                ? CreateArrayDeclaration(feedSection, options)
                : CreateKeyedDeclaration(feedSection, options, diagnostics);

            rawDeclarations.Add(declaration);
        }

        var effectiveDeclarations = SelectEffectiveDeclarations(rawDeclarations, diagnostics);

        return new(effectiveDeclarations, diagnostics);
    }

    private static IConfigurationSection GetFeedsSection(IConfiguration configuration)
    {
        if (configuration is IConfigurationSection section)
        {
            if (string.Equals(section.Key, nameof(NuplaneSetupOptions.Feeds), StringComparison.OrdinalIgnoreCase))
            {
                return section;
            }

            if (string.Equals(section.Key, "Setup", StringComparison.OrdinalIgnoreCase))
            {
                return section.GetSection(nameof(NuplaneSetupOptions.Feeds));
            }
        }

        var setupSection = configuration.GetSection("Setup");
        return setupSection.Exists()
            ? setupSection.GetSection(nameof(NuplaneSetupOptions.Feeds))
            : configuration.GetSection(nameof(NuplaneSetupOptions.Feeds));
    }

    private static NuplaneFeedSetupDeclaration CreateArrayDeclaration(
        IConfigurationSection feedSection,
        NuplaneFeedSetupOptions options)
    {
        _ = int.TryParse(feedSection.Key, out var index);
        return new(
            options.Name,
            NuplaneFeedSetupSourceShape.Array,
            feedSection.Path,
            index,
            null,
            options,
            []);
    }

    private static NuplaneFeedSetupDeclaration CreateKeyedDeclaration(
        IConfigurationSection feedSection,
        NuplaneFeedSetupOptions options,
        List<NuplaneFeedSetupDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(options.Name)
            && !string.Equals(options.Name, feedSection.Key, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                NuplaneFeedSetupDiagnosticSeverity.Error,
                KeyNameMismatchCode,
                $"Nuplane setup keyed feed '{feedSection.Key}' has Name '{options.Name}', but keyed feed Name must match the containing key.",
                feedSection.Path,
                feedSection.Key));
        }

        options.Name = feedSection.Key;
        return new(
            feedSection.Key,
            NuplaneFeedSetupSourceShape.Keyed,
            feedSection.Path,
            null,
            feedSection.Key,
            options,
            []);
    }

    private static IReadOnlyList<NuplaneFeedSetupDeclaration> SelectEffectiveDeclarations(
        IReadOnlyList<NuplaneFeedSetupDeclaration> declarations,
        List<NuplaneFeedSetupDiagnostic> diagnostics)
    {
        var effective = new List<NuplaneFeedSetupDeclaration>();

        foreach (var group in declarations
                     .GroupBy(static declaration => declaration.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var keyed = group
                .Where(static declaration => declaration.SourceShape == NuplaneFeedSetupSourceShape.Keyed)
                .OrderBy(static declaration => declaration.ConfigurationPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var array = group
                .Where(static declaration => declaration.SourceShape == NuplaneFeedSetupSourceShape.Array)
                .OrderBy(static declaration => declaration.ArrayIndex)
                .ToArray();

            if (keyed.Length > 1)
            {
                foreach (var duplicate in keyed.Skip(1))
                {
                    diagnostics.Add(new(
                        NuplaneFeedSetupDiagnosticSeverity.Error,
                        DuplicateKeyedFeedCode,
                        $"Nuplane setup contains duplicate keyed feed declaration for '{duplicate.Name}'.",
                        duplicate.ConfigurationPath,
                        duplicate.Name));
                }
            }

            if (keyed.Length > 0)
            {
                var selected = keyed[0];
                foreach (var ignored in array)
                {
                    diagnostics.Add(new(
                        NuplaneFeedSetupDiagnosticSeverity.Warning,
                        MixedShapeOverrideCode,
                        $"Nuplane setup feed '{selected.Name}' is declared by key at '{selected.ConfigurationPath}', so array declaration at '{ignored.ConfigurationPath}' is ignored.",
                        ignored.ConfigurationPath,
                        selected.Name));
                }

                effective.Add(selected with { IgnoredArrayDeclarations = array });
                continue;
            }

            effective.AddRange(array);
        }

        return effective;
    }

    private static bool IsAllDigits(string value) =>
        value.Length > 0 && value.All(static ch => ch is >= '0' and <= '9');
}
