namespace Nuplane.Feeds.Credentials;

/// <summary>
/// The built-in provider behind <c>secrets://env/&lt;VARIABLE&gt;</c>: it reads the named process
/// environment variable. <c>AddNuplane</c> registers it, so it works in a host and in a host-free
/// tool without any registration of its own.
/// </summary>
/// <remarks>
/// A variable that is unset or empty resolves to nothing, which refuses the referencing feed by name
/// rather than failing the run.
/// </remarks>
public sealed class EnvironmentSecretReferenceProvider : ISecretReferenceProvider
{
    /// <summary>The provider segment this provider answers for: <c>env</c>.</summary>
    public const string ProviderName = "env";

    /// <inheritdoc />
    public string Scheme => ProviderName;

    /// <inheritdoc />
    public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        return new(Environment.GetEnvironmentVariable(name));
    }
}
