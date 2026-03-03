namespace Nuplane.Abstractions;

/// <summary>
/// Specifies how package version selection behaves during resolution.
/// </summary>
public enum PackageUpdatePolicy
{
    /// <summary>Resolves to the exact version specified.</summary>
    Exact,
    /// <summary>Resolves to the best matching version within the specified range.</summary>
    Range
}