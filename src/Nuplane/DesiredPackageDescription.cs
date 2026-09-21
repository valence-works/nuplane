namespace Nuplane;

/// <summary>
/// One desired package request as the reconciliation pipeline would read it, plus the answer to
/// "is this a single-point pin?" — which cannot be computed outside the assembly, because the
/// include-pattern parser and the version-request classifier that decide it are internal.
/// </summary>
/// <param name="PackageId">The package identifier the request names.</param>
/// <param name="VersionRange">
/// The version constraint, already split from the include pattern that produced it: an empty string
/// for "resolve to latest", a bare version such as <c>1.2.3</c>, an exact range such as
/// <c>[1.2.3]</c>, or a range or floating version such as <c>[1.0.0,2.0.0)</c> or <c>1.*</c>.
/// </param>
/// <param name="FeedName">The preferred feed the request names, when it names one.</param>
/// <param name="SourceName">The desired-state source that produced the request.</param>
/// <param name="IsPinned">
/// Whether <paramref name="VersionRange"/> resolves to exactly one version — a bare exact version or
/// an inclusive single-point range. A bare identifier, a range with room in it, and a floating
/// version are all <see langword="false"/>.
/// </param>
/// <param name="PinnedVersion">
/// The single version <paramref name="VersionRange"/> pins, normalized, or <see langword="null"/>
/// when <paramref name="IsPinned"/> is <see langword="false"/>.
/// </param>
public sealed record DesiredPackageDescription(
    string PackageId,
    string VersionRange,
    string? FeedName,
    string SourceName,
    bool IsPinned,
    string? PinnedVersion);
