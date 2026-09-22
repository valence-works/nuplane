namespace Nuplane.Feeds.Credentials;

/// <summary>
/// Resolves a <c>secrets://&lt;provider&gt;/&lt;name&gt;</c> reference by dispatching to the registered
/// <see cref="ISecretReferenceProvider"/> that claims its provider segment. This is the single
/// composition point: feeds reference secrets, providers hold them, and nothing else in Nuplane
/// knows how a secret is stored.
/// </summary>
public interface ISecretReferenceResolver
{
    /// <summary>
    /// Resolves <paramref name="reference"/>.
    /// </summary>
    /// <remarks>
    /// "Nothing is registered for that provider" and "the provider holds no value" are outcomes, not
    /// exceptions: both come back as an unresolved
    /// <see cref="SecretReferenceResolution"/>, and the feed that referenced the secret is refused by
    /// name. Only a malformed reference throws, and configuration validation refuses that shape
    /// before a feed ever reaches this method.
    /// </remarks>
    /// <param name="reference">The reference to resolve, of the form <c>secrets://&lt;provider&gt;/&lt;name&gt;</c>.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The resolved secret, or why there is none.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reference"/> is not of the form <c>secrets://&lt;provider&gt;/&lt;name&gt;</c>. The message never includes the value.</exception>
    ValueTask<SecretReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken);
}
