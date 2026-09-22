namespace Nuplane.Abstractions;

/// <summary>
/// Represents one candidate package that can satisfy a <see cref="PackageCapabilityDeclaration"/>.
/// </summary>
/// <param name="Name">The option's name, unique within its capability, case-insensitively.</param>
/// <param name="PackageId">
/// The NuGet package id the host would add as a root if this option is selected. Never the
/// declaring package's own id.
/// </param>
/// <param name="VersionRange">
/// The version range the declaring package requires of <paramref name="PackageId"/>, as a NuGet
/// version range expression. A bare version is normalized to an exact single-version range.
/// </param>
public sealed record PackageCapabilityOption(
    string Name,
    string PackageId,
    string VersionRange);
