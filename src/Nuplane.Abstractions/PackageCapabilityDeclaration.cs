namespace Nuplane.Abstractions;

/// <summary>
/// Represents a single named capability a package declares in the <c>capabilities</c> section of a
/// package-root <c>nuplane.json</c> document: a choice of root package the host must make for the
/// declaring package to run.
/// </summary>
/// <param name="Name">The capability's name, unique within the document, case-insensitively.</param>
/// <param name="Description">An optional bounded human-readable explanation of the capability.</param>
/// <param name="Options">The candidate packages that can satisfy this capability. At least one.</param>
public sealed record PackageCapabilityDeclaration(
    string Name,
    string? Description,
    IReadOnlyList<PackageCapabilityOption> Options);
