using System.Collections.Concurrent;
using Nuplane.Abstractions;

namespace Nuplane.Feeds.Credentials;

/// <summary>
/// Turns a feed's configured secret reference into the credential the NuGet client and the package
/// download need, at most once per reference per reconciliation cycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cache lives exactly one cycle.</b> <see cref="BeginCycle"/> installs it in the ambient
/// execution context that a reconciliation cycle and everything it awaits share, and disposing the
/// returned scope removes it, so no resolved secret outlives the cycle that needed it. Outside a
/// cycle — a direct call on the acquirer, the pre-flight a host-free restore runs while composing —
/// there is no ambient cache and every call resolves afresh, which is the safe direction: an extra
/// lookup, never a stale secret.
/// </para>
/// <para>
/// Entries are keyed by the reference rather than by the feed, so two feeds sharing one reference
/// resolve it once, and a changed reference is never served from an older one.
/// </para>
/// </remarks>
internal static class FeedCredentials
{
    private static readonly AsyncLocal<ConcurrentDictionary<string, Lazy<Task<FeedCredential?>>>?> CycleCache = new();

    /// <summary>
    /// Starts a per-cycle credential cache and returns the scope that ends it. Nested scopes restore
    /// the enclosing cache on disposal.
    /// </summary>
    internal static IDisposable BeginCycle()
    {
        var previous = CycleCache.Value;
        CycleCache.Value = new(StringComparer.Ordinal);
        return new CycleScope(previous);
    }

    /// <summary>
    /// Resolves <paramref name="feed"/>'s credentials, if it declares any.
    /// </summary>
    /// <param name="resolver">The resolver to dispatch through, or <see langword="null"/> when nothing registered one — in which case a feed declaring credentials is refused, exactly as one referencing an unregistered provider is.</param>
    /// <param name="feed">The feed whose credentials to resolve.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>Nothing to resolve, the resolved credential, or a refusal.</returns>
    /// <exception cref="ArgumentException">Thrown when the feed's configured credentials are not a well-formed secret reference. Options validation refuses that shape before a feed reaches here.</exception>
    internal static async ValueTask<FeedCredentialLookup> ResolveAsync(
        ISecretReferenceResolver? resolver,
        FeedDefinition feed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);

        var reference = feed.Credentials;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return FeedCredentialLookup.NotConfigured;
        }

        if (resolver is null)
        {
            return FeedCredentialLookup.Refused;
        }

        var cache = CycleCache.Value;
        if (cache is null)
        {
            return new(true, await ResolveCoreAsync(resolver, reference, cancellationToken).ConfigureAwait(false));
        }

        var pending = cache.GetOrAdd(
            reference,
            static (key, state) => new(
                // Deliberately not the first caller's token: the entry is shared by every package
                // this cycle acquires from the feed, and each caller applies its own token to the
                // wait below instead.
                () => ResolveCoreAsync(state, key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication),
            resolver);

        try
        {
            return new(true, await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            // A provider that threw, or a cancelled wait, must not poison the rest of the cycle:
            // drop the entry so a later package resolves the reference again.
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<FeedCredential?>>>(reference, pending));
            throw;
        }
    }

    private static async Task<FeedCredential?> ResolveCoreAsync(
        ISecretReferenceResolver resolver,
        string reference,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);

        // An unresolved reference and a secret that is not a usable credential shape are the same
        // outcome to the caller: the feed is refused by name, never contacted half-authenticated.
        return resolution.IsResolved ? FeedCredential.FromSecret(resolution.Value!) : null;
    }

    private sealed class CycleScope(ConcurrentDictionary<string, Lazy<Task<FeedCredential?>>>? previous) : IDisposable
    {
        public void Dispose() => CycleCache.Value = previous;
    }
}
