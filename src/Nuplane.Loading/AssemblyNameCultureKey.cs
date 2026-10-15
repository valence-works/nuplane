using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Identifies an assembly binding slot by case-insensitive simple name and culture.
/// Versions and public key tokens remain subject to conflict validation within that slot.
/// </summary>
internal readonly record struct AssemblyNameCultureKey
{
    public AssemblyNameCultureKey(string? name, string? cultureName)
    {
        Name = (name ?? string.Empty).ToUpperInvariant();
        CultureName = string.IsNullOrWhiteSpace(cultureName) || string.Equals(cultureName, "neutral", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : cultureName.ToUpperInvariant();
    }

    public AssemblyNameCultureKey(AssemblyName assemblyName) : this(assemblyName.Name, assemblyName.CultureName)
    {
    }

    public string Name { get; }

    public string CultureName { get; }
}
