using System.Diagnostics.CodeAnalysis;

namespace Nuplane.Capabilities;

/// <summary>
/// The one place the desired-state source name of a capability-contributed root package is written
/// and read: <c>capability:&lt;capability&gt;=&lt;option&gt;</c>, in the same vocabulary as
/// <c>feed-rule:&lt;feed&gt;</c> and <c>dependency-of:&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// It is provenance a store, an admin surface, or an operator reads back, so it is also the only
/// channel that carries which capability and which option put a root in the closure. Capability and
/// option names are restricted to letters, digits, <c>.</c>, <c>_</c>, and <c>-</c>
/// (<c>NuplanePackageMetadataReader.NamePattern</c>), so neither the <c>:</c> nor the <c>=</c>
/// separator can occur inside a name and parsing back is exact rather than a guess.
/// </remarks>
internal static class CapabilitySourceName
{
    /// <summary>The prefix every capability-contributed source name starts with.</summary>
    internal const string Prefix = "capability:";

    /// <summary>Builds the source name a selected option's injected root request carries.</summary>
    internal static string Create(string capabilityName, string optionName) =>
        $"{Prefix}{capabilityName}={optionName}";

    /// <summary>
    /// Reads the capability and option back out of a source name <see cref="Create"/> produced.
    /// Returns <see langword="false"/> — without throwing — for any other source name, so a caller
    /// that sees requests from every desired source can simply skip the ones that are not
    /// capability contributions.
    /// </summary>
    internal static bool TryParse(
        string? sourceName,
        [NotNullWhen(true)] out string? capabilityName,
        [NotNullWhen(true)] out string? optionName)
    {
        capabilityName = null;
        optionName = null;

        if (string.IsNullOrEmpty(sourceName) || !sourceName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = sourceName[Prefix.Length..];
        var separatorIndex = body.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == body.Length - 1)
        {
            return false;
        }

        capabilityName = body[..separatorIndex];
        optionName = body[(separatorIndex + 1)..];
        return true;
    }
}
