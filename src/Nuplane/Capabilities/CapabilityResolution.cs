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
/// <param name="Diagnostics">Informational outcomes: explicit-root satisfaction, and an unmatched selection.</param>
internal sealed record CapabilityResolution(
    IReadOnlyList<PackageRequest> Injections,
    IReadOnlyList<CapabilityRefusal> Refusals,
    IReadOnlyList<CapabilityDiagnostic> Diagnostics)
{
    internal static readonly CapabilityResolution Empty = new([], [], []);

    /// <summary>
    /// The injections a <c>requirePinned</c> resolution dropped because their effective version
    /// range is not a single pinned version, each already refused as
    /// <see cref="CapabilityRefusalStage.Unpinned"/> in <see cref="Refusals"/>. They are
    /// reported as requests as well, because a host-free restore lists the offending contributions
    /// the same way it lists an unpinned desired request, and rebuilding them from the refusal's
    /// prose — or recomputing the effective range outside this resolver — would be a second place
    /// that decides what a selection means. Declared outside the primary constructor deliberately:
    /// every existing three-argument construction of this record keeps compiling.
    /// </summary>
    internal IReadOnlyList<PackageRequest> UnpinnedRequests { get; init; } = [];
}

/// <summary>
/// One informational outcome of <see cref="CapabilitySelectionResolver.Resolve"/> that is neither a
/// refusal nor an injection, carrying both the message an operator reads and the kind a caller
/// routes on, so no consumer has to re-derive the rule that produced it.
/// </summary>
/// <param name="Kind">Which rule produced this diagnostic.</param>
/// <param name="CapabilityName">The capability's canonical name.</param>
/// <param name="PackageId">
/// The explicit root package that satisfied the capability for
/// <see cref="CapabilityDiagnosticKind.SatisfiedByExplicitRoot"/>; <see langword="null"/> otherwise.
/// </param>
/// <param name="Message">The human-readable message.</param>
internal sealed record CapabilityDiagnostic(
    CapabilityDiagnosticKind Kind,
    string CapabilityName,
    string? PackageId,
    string Message);

/// <summary>The kinds of informational outcome <see cref="CapabilitySelectionResolver"/> reports.</summary>
internal enum CapabilityDiagnosticKind
{
    /// <summary>
    /// No option was selected, but the host already named an option's package as an explicit
    /// desired root, so the capability is satisfied without injecting anything.
    /// </summary>
    SatisfiedByExplicitRoot,

    /// <summary>
    /// A configured selection names a capability that no package in this cycle declares — usually a
    /// typo in the configuration key, which nothing else would make visible.
    /// </summary>
    SelectionUnmatched
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

/// <summary>
/// The stage names capability refusals are recorded under, extending the reconciliation pipeline's
/// existing <c>resolve-*</c> family. <see cref="CapabilitySelectionResolver"/> produces all of them
/// except <see cref="Unresolved"/> and <see cref="ContributionLimit"/>, which only the resolution
/// executor can know about.
/// </summary>
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

    /// <summary>
    /// A package-root <c>nuplane.json</c> declaring schema 2 is invalid. Only schema 2 can affect
    /// the package closure, so only an invalid schema-2 document fails its package; an invalid
    /// schema-1 document stays ignored, as it always has been.
    /// </summary>
    internal const string MetadataInvalid = "capability-metadata-invalid";

    /// <summary>
    /// A selected option's package could not be acquired from any eligible feed. Recorded against
    /// every package that declared the capability, because a package whose capability is unmet is
    /// broken, and applying it would only move the failure to load time.
    /// </summary>
    internal const string Unresolved = "capability-unresolved";

    /// <summary>
    /// Contribution rounds did not reach a fixpoint within the bound, so resolution stopped rather
    /// than chasing an unbounded chain of capability-declaring packages.
    /// </summary>
    internal const string ContributionLimit = "capability-contribution-limit";
}
