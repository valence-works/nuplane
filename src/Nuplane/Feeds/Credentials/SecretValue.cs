namespace Nuplane.Feeds.Credentials;

/// <summary>
/// A resolved secret, wrapped so that logging it, interpolating it into a message, or including it
/// in an exception prints a redaction marker instead of the value. <see cref="Reveal"/> is the only
/// way back to the value, which makes every place that handles the secret in the clear greppable.
/// </summary>
public sealed class SecretValue
{
    /// <summary>
    /// What a secret prints as. Nothing that goes to a log, a message, or the store ever sees more.
    /// </summary>
    internal const string Redacted = "***";

    private readonly string _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretValue"/> class.
    /// </summary>
    /// <param name="value">The secret value.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is <see langword="null"/> or empty. An empty secret is reported as unresolved instead of being wrapped.</exception>
    public SecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>
    /// Returns the secret in the clear. Never hand the result to a logger, an exception message, or
    /// anything that persists.
    /// </summary>
    /// <returns>The secret value.</returns>
    public string Reveal() => _value;

    /// <summary>
    /// Returns a redaction marker, never the value, so that accidental interpolation cannot leak the
    /// secret.
    /// </summary>
    /// <returns>A fixed redaction marker.</returns>
    public override string ToString() => Redacted;
}
