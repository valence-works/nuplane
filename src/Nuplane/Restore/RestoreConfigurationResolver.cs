using Microsoft.Extensions.Configuration;

namespace Nuplane.Restore;

/// <summary>
/// Resolves the <c>Nuplane</c> section out of whichever <see cref="IConfiguration"/> a host-free
/// caller supplies, so <see cref="NuplaneRestore.RestoreAsync"/> and
/// <see cref="NuplaneRestore.DescribeDesiredAsync"/> accept the same two shapes their XML docs
/// promise: the host's configuration root, or that root's own <c>Nuplane</c> section.
/// </summary>
/// <remarks>
/// <c>AddNuplane</c> itself is unchanged and still expects to be handed configuration already scoped
/// to Nuplane's own keys (<c>Setup</c>, <c>Reconciliation</c>, <c>StoreRegistry</c>, ...): it never
/// unwraps a <c>Nuplane</c> key. A host that calls <c>AddNuplane</c> directly already resolves that
/// section itself before passing it in, as the README shows. This resolver exists only for the
/// host-free entry point, whose caller may hand it the host's unresolved configuration root instead.
/// </remarks>
internal static class RestoreConfigurationResolver
{
    /// <summary>
    /// The name of the section a configuration root nests Nuplane's own configuration under.
    /// </summary>
    internal const string NuplaneSectionName = "Nuplane";

    /// <summary>
    /// Returns <paramref name="configuration"/>'s own <see cref="NuplaneSectionName"/> child section
    /// when it exists, and <paramref name="configuration"/> itself otherwise — so a configuration
    /// root and the <c>Nuplane</c> section it nests both resolve to the same effective configuration.
    /// </summary>
    internal static IConfiguration ResolveNuplaneSection(IConfiguration configuration)
    {
        var section = configuration.GetSection(NuplaneSectionName);
        return section.Exists() ? section : configuration;
    }
}
