namespace Nuplane.Reconciliation.Configuration;

/// <summary>
/// Host-declared package ids and id prefixes that <see cref="PackageDependencyGraphResolver"/>
/// treats as already supplied by the host, so a package's declared <c>dependency</c> on one of them
/// is skipped rather than acquired. Bound from the <c>Nuplane:HostProvidedPackages</c> configuration
/// section, which is a plain list rather than a named-property object (see
/// <c>NuplaneOptionsRegistrationServices</c> and <c>HostProvidedPackagesConfigurationReader</c>).
/// </summary>
/// <remarks>
/// <para>
/// This answers a different question from <c>Loading:SharedAssemblies</c>
/// (<c>Nuplane.Loading.LoadingOptions.SharedAssemblies</c>, documented in the Usage Guide alongside
/// this option): this list decides which <em>dependencies</em> the resolver does not acquire at all,
/// before a package is ever on disk. <c>SharedAssemblies</c> decides which <em>assemblies</em> of
/// packages that were acquired are resolved from the host's own load context instead of a
/// package-specific one, after loading. A package id can belong on one list, the other, both, or
/// neither.
/// </para>
/// <para>
/// An entry that ends with <c>.</c> is a prefix: it matches every dependency package id that starts
/// with it, case-insensitively (for example <c>"Acme."</c> matches <c>Acme.Contracts</c> and
/// <c>Acme.Abstractions</c>, but not <c>AcmeContracts</c> or bare <c>Acme</c>). Every other entry is
/// matched as an exact package id, also case-insensitively. Duplicate entries, case-insensitively,
/// are harmless and ignored rather than rejected.
/// </para>
/// <para>
/// This list is consulted only for dependencies, never for root packages: an injected or explicit
/// root is always acquired, however it is named. It is also independent of the <c>*.deps.json</c>
/// rule — a dependency present in the host's own deps at a version that satisfies the request is
/// skipped regardless of this list, and that rule is unaffected by anything configured here.
/// </para>
/// <para>
/// Defaults to Nuplane's own contract package ids, so an unconfigured host reproduces exactly the
/// Nuplane-owned part of the fixed allowlist this option replaces.
/// </para>
/// </remarks>
public sealed class HostProvidedPackagesOptions
{
    /// <summary>
    /// Gets the default entries: Nuplane's own contract package ids, applied when the host
    /// configures nothing under <c>Nuplane:HostProvidedPackages</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultEntries { get; } =
    [
        "Nuplane.Abstractions",
        "Nuplane.Loading.Abstractions"
    ];

    /// <summary>
    /// Gets the configured entries: package ids and <c>Prefix.</c>-style prefixes. Starts populated
    /// with <see cref="DefaultEntries"/>; configuration binding replaces the whole list rather than
    /// appending to it, so an explicitly configured but empty <c>Nuplane:HostProvidedPackages</c>
    /// means no entries at all, not the defaults plus nothing.
    /// </summary>
    public ICollection<string> Entries { get; } = new List<string>(DefaultEntries);
}
