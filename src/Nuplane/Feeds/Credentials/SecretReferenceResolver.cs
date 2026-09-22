namespace Nuplane.Feeds.Credentials;

/// <summary>
/// The built-in <see cref="ISecretReferenceResolver"/>: it parses the reference, looks up the
/// registered <see cref="ISecretReferenceProvider"/> whose <see cref="ISecretReferenceProvider.Scheme"/>
/// matches its provider segment, and reports an unresolved outcome when there is no such provider or
/// the provider holds no value.
/// </summary>
public sealed class SecretReferenceResolver : ISecretReferenceResolver
{
    private readonly Dictionary<string, ISecretReferenceProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretReferenceResolver"/> class over every
    /// registered provider.
    /// </summary>
    /// <param name="providers">The registered providers, keyed by their declared provider segment.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="providers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a provider declares no provider segment, or when two providers claim the same one.
    /// Silently picking one of two providers for <c>env</c> would make a host that meant to replace
    /// the built-in provider look like it had, so the ambiguity is refused instead; remove the
    /// descriptor of the provider being replaced.
    /// </exception>
    public SecretReferenceResolver(IEnumerable<ISecretReferenceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var scheme = provider.Scheme;
            if (string.IsNullOrWhiteSpace(scheme))
            {
                throw new InvalidOperationException(
                    $"Secret reference provider '{provider.GetType().FullName}' declares no provider name, so no reference could ever address it.");
            }

            if (!_providers.TryAdd(scheme, provider))
            {
                throw new InvalidOperationException(
                    $"Secret reference providers '{_providers[scheme].GetType().FullName}' and '{provider.GetType().FullName}' both claim the provider name '{scheme}'. "
                    + "Remove the service descriptor of the one being replaced rather than registering a second provider for the same name.");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<SecretReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var parsed = SecretReference.Parse(reference, nameof(reference));

        if (!_providers.TryGetValue(parsed.ProviderName, out var provider))
        {
            return SecretReferenceResolution.NoProvider(parsed.ProviderName);
        }

        var value = await provider.ResolveAsync(parsed.Name, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(value)
            ? SecretReferenceResolution.Empty(parsed.ProviderName)
            : SecretReferenceResolution.Resolved(parsed.ProviderName, new(value));
    }
}
