namespace Nuplane.Feeds.Credentials;

/// <summary>
/// The outcome of resolving one <c>secrets://&lt;provider&gt;/&lt;name&gt;</c> reference: either a secret,
/// or the reason there is none. "No provider" and "no value" are outcomes rather than exceptions,
/// because a host that has registered no provider for a reference must degrade to a refused feed,
/// not to a failed run.
/// </summary>
public sealed record SecretReferenceResolution
{
    private SecretReferenceResolution(SecretReferenceResolutionStatus status, string providerName, SecretValue? value)
    {
        Status = status;
        ProviderName = providerName;
        Value = value;
    }

    /// <summary>Gets why the reference did or did not yield a secret.</summary>
    public SecretReferenceResolutionStatus Status { get; }

    /// <summary>Gets the provider segment the reference named, whether or not a provider claimed it.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// Gets the resolved secret, or <see langword="null"/> when <see cref="Status"/> is not
    /// <see cref="SecretReferenceResolutionStatus.Resolved"/>.
    /// </summary>
    public SecretValue? Value { get; }

    /// <summary>Gets whether a secret was resolved.</summary>
    public bool IsResolved => Status == SecretReferenceResolutionStatus.Resolved;

    /// <summary>Creates a resolved outcome.</summary>
    /// <param name="providerName">The provider that answered.</param>
    /// <param name="value">The resolved secret.</param>
    /// <returns>A resolved outcome carrying <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public static SecretReferenceResolution Resolved(string providerName, SecretValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(value);
        return new(SecretReferenceResolutionStatus.Resolved, providerName, value);
    }

    /// <summary>Creates the outcome for a reference whose provider segment nothing claims.</summary>
    /// <param name="providerName">The provider segment the reference named.</param>
    /// <returns>An unresolved outcome.</returns>
    public static SecretReferenceResolution NoProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return new(SecretReferenceResolutionStatus.NoProvider, providerName, null);
    }

    /// <summary>Creates the outcome for a provider that holds nothing under the referenced name.</summary>
    /// <param name="providerName">The provider that answered.</param>
    /// <returns>An unresolved outcome.</returns>
    public static SecretReferenceResolution Empty(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return new(SecretReferenceResolutionStatus.Empty, providerName, null);
    }

    /// <summary>
    /// Returns the status and the provider name, never the secret — the compiler-generated record
    /// <c>ToString</c> is deliberately replaced so that logging an outcome cannot leak one.
    /// </summary>
    /// <returns>A redacted description of the outcome.</returns>
    public override string ToString() => $"{Status} (provider '{ProviderName}')";
}
