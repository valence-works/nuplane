namespace Nuplane;

/// <summary>
/// One package a host-free restore's cycle refused or could not acquire, with the stage and the
/// message the cycle recorded for it — the same <c>FailureRecord</c> the store's
/// <c>LastFailureById</c> holds, surfaced on the result so a caller can tell a capability refusal
/// from an unreachable feed without reading the state file the restore just wrote.
/// </summary>
/// <param name="PackageId">The identifier of the package the cycle failed.</param>
/// <param name="Stage">
/// The stage the cycle recorded the failure under. A refused capability declaration records one of
/// the <c>capability-*</c> stages — <c>capability-unselected</c>, <c>capability-unknown-option</c>,
/// <c>capability-unpinned</c>, <c>capability-conflict</c>, <c>capability-unresolved</c>,
/// <c>capability-metadata-invalid</c>, <c>capability-contribution-limit</c> — and a package that
/// could not be resolved from any feed records a <c>resolve-*</c> stage, such as
/// <c>resolve-feed-unavailable</c> or <c>resolve-no-eligible-feed</c>. A package that depends on a
/// declared host-provided package (<c>Nuplane:HostProvidedPackages</c>) the host carries at a version
/// outside the required range records <c>host-version-unsatisfied</c>. Match on the prefix rather
/// than the full name where the distinction that matters is "a decision the host has not made"
/// versus "a feed that could not be reached": a later Nuplane may add a stage beside these.
/// </param>
/// <param name="Message">The message the cycle recorded, as Nuplane worded it: a capability refusal names the capability, every declared option, and the configuration key that selects one.</param>
public sealed record PackageRefusal(string PackageId, string Stage, string Message);
