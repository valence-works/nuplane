using Microsoft.Extensions.Configuration;

namespace Nuplane.Reconciliation.Configuration;

/// <summary>
/// Binds the <c>Nuplane:HostProvidedPackages</c> configuration section into
/// <see cref="HostProvidedPackagesOptions"/>. The section is a plain list (<c>["Id.One", "Prefix."]</c>),
/// not an object with named children, so it cannot bind through the ordinary
/// <c>IConfigurationSection.Bind</c> property-matching path that every other Nuplane option section uses.
/// </summary>
internal static class HostProvidedPackagesConfigurationReader
{
    /// <summary>
    /// Populates <paramref name="options"/> from <paramref name="section"/>, one entry per array
    /// element. A section that is absent leaves <see cref="HostProvidedPackagesOptions.Entries"/> at
    /// its default value; a section that is present, including an empty array, replaces it entirely,
    /// so an explicit <c>"HostProvidedPackages": []</c> means no configured entries at all.
    /// </summary>
    internal static void Populate(HostProvidedPackagesOptions options, IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return;
        }

        options.Entries.Clear();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is not null)
            {
                options.Entries.Add(child.Value);
            }
        }
    }
}
