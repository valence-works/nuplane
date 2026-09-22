using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Feeds.Credentials;

namespace Nuplane.Registration;

internal static class NuplaneSecretReferenceRegistrationServices
{
    /// <summary>
    /// Registers the secret reference resolver and the built-in <c>env</c> provider. Every
    /// registration is a <c>TryAdd</c>, so calling <c>AddNuplane</c> twice registers one of each, and
    /// a host that wants a different <c>env</c> removes the built-in descriptor:
    /// <code>
    /// services.RemoveAll&lt;ISecretReferenceProvider&gt;();                       // every provider, or
    /// services.Remove(services.Single(descriptor =&gt;
    ///     descriptor.ServiceType == typeof(ISecretReferenceProvider)
    ///     &amp;&amp; descriptor.ImplementationType == typeof(EnvironmentSecretReferenceProvider)));
    /// </code>
    /// Registering a second provider that claims <c>env</c> instead of removing this one is refused
    /// when the resolver is constructed, so a replacement that did not take effect cannot pass
    /// unnoticed.
    /// </summary>
    internal static void RegisterSecretReferences(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecretReferenceProvider, EnvironmentSecretReferenceProvider>());
        services.TryAddSingleton<SecretReferenceResolver>();
        services.TryAddSingleton<ISecretReferenceResolver>(sp => sp.GetRequiredService<SecretReferenceResolver>());
    }
}
