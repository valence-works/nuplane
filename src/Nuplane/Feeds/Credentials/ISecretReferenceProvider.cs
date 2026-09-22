namespace Nuplane.Feeds.Credentials;

/// <summary>
/// Resolves the name half of a <c>secrets://&lt;provider&gt;/&lt;name&gt;</c> reference for one provider.
/// Implementations are registered in dependency injection as an enumerable, and
/// <see cref="ISecretReferenceResolver"/> dispatches to the one whose <see cref="Scheme"/> matches the
/// provider segment of the reference.
/// </summary>
/// <remarks>
/// <para>
/// An implementation is resolved once and shared by every feed, so it must be thread-safe and
/// callable concurrently.
/// </para>
/// <para>
/// Nuplane treats the returned value as a secret from the moment it is returned: it is never logged,
/// never included in an exception message, and never written to the store. An implementation is
/// expected to hold the same line — in particular, it must not put the value into the message of an
/// exception it throws, because that message travels into a reconciliation cycle's failure
/// diagnostics.
/// </para>
/// </remarks>
public interface ISecretReferenceProvider
{
    /// <summary>
    /// Gets the provider segment this provider answers for — the <c>env</c> in
    /// <c>secrets://env/MY_FEED_TOKEN</c>. Matched case-insensitively, and must not be empty.
    /// Two registered providers claiming the same segment is a composition error and is refused.
    /// </summary>
    string Scheme { get; }

    /// <summary>
    /// Resolves the named secret.
    /// </summary>
    /// <remarks>
    /// Having no value under <paramref name="name"/> is a normal answer, not an error: return
    /// <see langword="null"/> (or an empty string) and the feed that referenced it is refused by
    /// name, exactly as a feed referencing an unregistered provider is. Throw only when the lookup
    /// itself failed — an unreachable secret store, for example — and never with the value in the
    /// message.
    /// </remarks>
    /// <param name="name">The name half of the reference: everything after the provider segment, with no leading slash.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>The secret value, or <see langword="null"/> when this provider holds nothing under that name.</returns>
    ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken);
}
