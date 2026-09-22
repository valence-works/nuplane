using Nuplane.Abstractions;

namespace Nuplane.Capabilities;

/// <summary>
/// One package in the closure together with the capabilities it declares, the shape
/// <see cref="CapabilitySelectionResolver"/> consumes. A package with no <c>nuplane.json</c>
/// capabilities section, or none read yet, contributes an empty <see cref="Declarations"/>.
/// </summary>
/// <param name="PackageId">The declaring package's id.</param>
/// <param name="PackageVersion">The declaring package's resolved version, used only to identify it in diagnostics.</param>
/// <param name="Declarations">The capabilities this package declares.</param>
internal sealed record CapabilityDeclaringPackage(
    string PackageId,
    string PackageVersion,
    IReadOnlyList<PackageCapabilityDeclaration> Declarations)
{
    /// <summary>The <c>id@version</c> form every refusal and diagnostic message identifies this package by.</summary>
    internal string Identity => $"{PackageId}@{PackageVersion}";
}

/// <summary>
/// The result of <see cref="CapabilitySelectionResolver.Resolve"/>: the root packages a selection
/// injects, the declaring packages a rule refuses, and informational diagnostics that name neither a
/// refusal nor an injection (an explicit root silently satisfying a capability, or a selection that
/// matched no declaration).
/// </summary>
/// <param name="Injections">
/// One <see cref="PackageRequest"/> per selected option not already satisfied by an explicit root,
/// ordered by capability name then option name (both ordinal).
/// </param>
/// <param name="Refusals">Every capability whose declaring package(s) cannot be satisfied, in the same order.</param>
/// <param name="Diagnostics">Informational messages: explicit-root satisfaction, and an unmatched selection.</param>
internal sealed record CapabilityResolution(
    IReadOnlyList<PackageRequest> Injections,
    IReadOnlyList<CapabilityRefusal> Refusals,
    IReadOnlyList<string> Diagnostics)
{
    internal static readonly CapabilityResolution Empty = new([], [], []);
}

/// <summary>
/// One capability a declaring package cannot be satisfied for, and why.
/// </summary>
/// <param name="CapabilityName">The capability's canonical name.</param>
/// <param name="Stage">One of the <c>capability-*</c> stage names in <see cref="CapabilityRefusalStage"/>.</param>
/// <param name="Message">A message naming the capability, the declaring package(s), and the options or requests involved.</param>
/// <param name="DeclaringPackageIds">The <c>id@version</c> identity of every package this refusal applies to.</param>
internal sealed record CapabilityRefusal(
    string CapabilityName,
    string Stage,
    string Message,
    IReadOnlyList<string> DeclaringPackageIds);

/// <summary>The stage names <see cref="CapabilitySelectionResolver"/> records refusals under.</summary>
internal static class CapabilityRefusalStage
{
    /// <summary>Two declarations of the same capability disagree, or an explicit root cannot satisfy a declared option.</summary>
    internal const string Conflict = "capability-conflict";

    /// <summary>No selection is configured and no explicit root satisfies any declared option.</summary>
    internal const string Unselected = "capability-unselected";

    /// <summary>A selection names an option the capability does not declare.</summary>
    internal const string UnknownOption = "capability-unknown-option";

    /// <summary>A selected option's effective version range is not a single pinned version, and pinning is required.</summary>
    internal const string Unpinned = "capability-unpinned";
}
