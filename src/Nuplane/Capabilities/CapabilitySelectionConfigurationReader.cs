using Microsoft.Extensions.Configuration;

namespace Nuplane.Capabilities;

/// <summary>
/// Binds a <c>Nuplane:Capabilities</c> configuration section into <see cref="CapabilityOptions"/>,
/// accepting both the simple string form (<c>"ef-provider": "PostgreSql"</c>, comma-separated for
/// several options) and the object form (<c>"ef-provider": { "Option": "...", "Version": "...",
/// "Feed": "..." }</c>). Environment variables bind through the standard <c>__</c> section mapping,
/// since both forms are ordinary configuration shapes.
/// </summary>
internal static class CapabilitySelectionConfigurationReader
{
    /// <summary>
    /// Populates <paramref name="options"/> from <paramref name="capabilitiesSection"/>, one entry
    /// per child section (one per capability name). A section whose shape cannot be resolved to a
    /// selection — an empty option list, or a raw value and an <c>Option</c> child that disagree —
    /// contributes a diagnostic to <see cref="CapabilityOptions.ConfigurationErrors"/> instead of an
    /// entry, so <c>CapabilityOptionsValidator</c> can fail startup with a message naming the key.
    /// </summary>
    internal static void Populate(CapabilityOptions options, IConfigurationSection capabilitiesSection)
    {
        foreach (var capabilitySection in capabilitiesSection.GetChildren())
        {
            var key = $"Nuplane:Capabilities:{capabilitySection.Key}";
            var rawValue = capabilitySection.Value;
            var optionValue = capabilitySection["Option"];

            if (rawValue is not null && optionValue is not null
                && !string.Equals(rawValue, optionValue, StringComparison.Ordinal))
            {
                options.ConfigurationErrors.Add(
                    $"{key} sets both a value ('{rawValue}') and an Option child ('{optionValue}') with different values; set only one.");
                continue;
            }

            var optionsText = optionValue ?? rawValue;
            var optionNames = string.IsNullOrWhiteSpace(optionsText)
                ? []
                : optionsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (optionNames.Length == 0)
            {
                options.ConfigurationErrors.Add(
                    $"{key} must select at least one option, as a value or an Option child.");
                continue;
            }

            options.Selections[capabilitySection.Key] = new CapabilitySelection
            {
                Options = optionNames,
                Version = capabilitySection["Version"],
                Feed = capabilitySection["Feed"]
            };
        }
    }
}
