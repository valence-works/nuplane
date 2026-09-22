namespace Nuplane.Capabilities;

/// <summary>
/// Host-configured capability selections, bound from the <c>Nuplane:Capabilities</c> configuration
/// section (see <c>NuplaneOptionsRegistrationServices</c>) and from
/// <c>NuplaneBuilder.SelectCapability</c>, which is applied after configuration and therefore wins.
/// </summary>
/// <remarks>
/// A capability declared by a package (<c>PackageCapabilityDeclaration</c>, read from that package's
/// <c>nuplane.json</c>) names a choice the host must make; this options type is where the host makes
/// it. <c>CapabilitySelectionResolver</c> is the pure function that turns a declaration plus a
/// selection into an injected root package or a refusal.
/// </remarks>
public sealed class CapabilityOptions
{
    /// <summary>
    /// Gets the configured selections, keyed by capability name, matched case-insensitively.
    /// </summary>
    public IDictionary<string, CapabilitySelection> Selections { get; } =
        new Dictionary<string, CapabilitySelection>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configuration-shape errors found while binding <see cref="Selections"/> from raw
    /// configuration — for example a capability section that sets both a raw value and an
    /// <c>Option</c> child to different values. Surfaced by <c>CapabilityOptionsValidator</c>
    /// alongside the per-selection checks it performs on <see cref="Selections"/> itself.
    /// </summary>
    internal List<string> ConfigurationErrors { get; } = [];
}

/// <summary>
/// One host-configured selection for a single capability: which option (or options) to use, and the
/// optional overrides <c>CapabilitySelectionResolver</c> applies in place of the declaring package's
/// own version and feed preference.
/// </summary>
public sealed class CapabilitySelection
{
    /// <summary>
    /// Gets or sets the selected option name(s), matched against a capability's declared option
    /// names case-insensitively. More than one entry selects more than one option of the same
    /// capability — a host that runs two engines on purpose.
    /// </summary>
    public IReadOnlyList<string> Options { get; set; } = [];

    /// <summary>
    /// Gets or sets the version range that replaces every selected option's declared version, or
    /// <see langword="null"/> to use the declaring package's own declared version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the feed name an injected root prefers, or <see langword="null"/> to leave it
    /// unset (any trusted feed).
    /// </summary>
    public string? Feed { get; set; }
}
