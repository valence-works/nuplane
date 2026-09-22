namespace Nuplane.Feeds.Credentials;

/// <summary>
/// A parsed <c>secrets://&lt;provider&gt;/&lt;name&gt;</c> reference. Everything after the first slash of
/// the name is part of the name, so a provider that addresses secrets by path
/// (<c>secrets://vault/apps/nuplane/feed-token</c>) needs no further syntax.
/// </summary>
/// <remarks>
/// The parse never echoes the input. A host that pastes a raw token into <c>Credentials</c> instead
/// of a reference reaches exactly this code, so putting the input into the error message would print
/// the secret it was trying to protect.
/// </remarks>
internal readonly record struct SecretReference(string ProviderName, string Name)
{
    /// <summary>The prefix every secret reference starts with.</summary>
    internal const string Prefix = "secrets://";

    /// <summary>The shape named in validation errors and exceptions, in place of the value.</summary>
    internal const string ExpectedShape = "secrets://<provider>/<name>";

    internal static bool TryParse(string? reference, out SecretReference parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = reference[Prefix.Length..];
        var separator = remainder.IndexOf('/');
        if (separator < 0)
        {
            return false;
        }

        var providerName = remainder[..separator];
        var name = remainder[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        parsed = new(providerName, name);
        return true;
    }

    /// <exception cref="ArgumentException">Thrown when <paramref name="reference"/> is not of the expected shape. The message names the shape, never the value.</exception>
    internal static SecretReference Parse(string reference, string parameterName)
    {
        if (!TryParse(reference, out var parsed))
        {
            throw new ArgumentException(
                $"The value is not a secret reference of the form '{ExpectedShape}'. The value itself is deliberately omitted from this message, because a misconfiguration that puts a secret here would otherwise print it.",
                parameterName);
        }

        return parsed;
    }
}
