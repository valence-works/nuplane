namespace Nuplane.Feeds.Credentials;

/// <summary>
/// What resolving one feed's configured credentials produced: nothing to resolve, a usable
/// credential, or a refusal. The three are kept apart so that "this feed wants credentials and we
/// could not get them" can never be mistaken for "this feed needs none".
/// </summary>
internal readonly record struct FeedCredentialLookup(bool DeclaresCredentials, FeedCredential? Credential)
{
    /// <summary>The feed configures no credentials at all, and is contacted anonymously.</summary>
    internal static readonly FeedCredentialLookup NotConfigured = new(false, null);

    /// <summary>The feed configures credentials that could not be resolved, so it is refused.</summary>
    internal static readonly FeedCredentialLookup Refused = new(true, null);

    internal bool IsRefused => DeclaresCredentials && Credential is null;

    /// <summary>Redacted: the credential's own <c>ToString</c> prints nothing, and neither does this.</summary>
    public override string ToString() =>
        !DeclaresCredentials ? "none" : Credential is null ? "refused" : "resolved";
}
