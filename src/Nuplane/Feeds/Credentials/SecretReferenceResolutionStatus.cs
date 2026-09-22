namespace Nuplane.Feeds.Credentials;

/// <summary>
/// Why a <c>secrets://&lt;provider&gt;/&lt;name&gt;</c> reference did or did not yield a secret. Only
/// <see cref="Resolved"/> carries a value; the other outcomes are ordinary answers that refuse the
/// referencing feed by name rather than failing the run.
/// </summary>
public enum SecretReferenceResolutionStatus
{
    /// <summary>A registered provider returned a non-empty value.</summary>
    Resolved,

    /// <summary>No registered provider claims the reference's provider segment.</summary>
    NoProvider,

    /// <summary>The provider answered, but holds nothing under that name.</summary>
    Empty
}
