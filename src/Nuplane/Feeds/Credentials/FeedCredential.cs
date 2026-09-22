using System.Net.Http.Headers;
using System.Text;
using NuGet.Configuration;

namespace Nuplane.Feeds.Credentials;

/// <summary>
/// A resolved feed credential in the shape NuGet's basic authentication wants: a user name and a
/// password. Both halves are treated as secret — the user half of a <c>user:password</c> secret came
/// out of the same secret store as the password — so nothing here prints either of them.
/// </summary>
internal sealed class FeedCredential
{
    /// <summary>
    /// The user name sent when the resolved secret is a bare token. NuGet's basic authentication
    /// needs a non-empty user name, and token-issuing feeds such as Azure Artifacts and Feedz accept
    /// any, so the token travels as the password under this fixed placeholder.
    /// </summary>
    internal const string TokenUserName = "nuplane";

    private FeedCredential(string userName, SecretValue password)
    {
        UserName = userName;
        Password = password;
    }

    internal string UserName { get; }

    internal SecretValue Password { get; }

    /// <summary>
    /// Maps a resolved secret onto a credential: <c>user:password</c> splits at the first colon, and
    /// anything without a colon is a bare token carried under <see cref="TokenUserName"/>.
    /// A secret whose colon leaves either half empty is refused (<see langword="null"/>) rather than
    /// sent as a half-formed credential that the feed would answer with a confusing 401.
    /// </summary>
    internal static FeedCredential? FromSecret(SecretValue secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var value = secret.Reveal();
        var separator = value.IndexOf(':');

        if (separator < 0)
        {
            return new(TokenUserName, secret);
        }

        if (separator == 0 || separator == value.Length - 1)
        {
            return null;
        }

        return new(value[..separator], new(value[(separator + 1)..]));
    }

    /// <summary>
    /// The per-source credential NuGet's client attaches to one feed. It is set on the
    /// <see cref="PackageSource"/> of a single <c>SourceRepository</c>, never on a process-global
    /// credential service, so one feed's secret is never offered to another feed.
    /// </summary>
    internal PackageSourceCredential ToPackageSourceCredential(string source) =>
        new(source, UserName, Password.Reveal(), isPasswordClearText: true, validAuthenticationTypesText: null);

    /// <summary>
    /// The <c>Authorization</c> header for the plain <see cref="HttpClient"/> path, built per request
    /// so that the shared client's default headers never carry one feed's secret into another feed's
    /// request.
    /// </summary>
    internal AuthenticationHeaderValue ToBasicAuthenticationHeader() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password.Reveal()}")));

    /// <summary>Redacts both halves: neither the user name nor the password is ever printed.</summary>
    public override string ToString() => SecretValue.Redacted;
}
