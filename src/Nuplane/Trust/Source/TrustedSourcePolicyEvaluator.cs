using Nuplane.Abstractions;

namespace Nuplane.Trust.Source;

/// <summary>
/// Evaluates whether a desired-state source is allowed to contribute requests based on
/// the configured <see cref="TrustedSourcePolicyOptions"/>.
/// </summary>
public sealed class TrustedSourcePolicyEvaluator
{
    /// <summary>
    /// Evaluates whether the specified source name is trusted.
    /// </summary>
    /// <param name="sourceName">The name of the desired-state source to evaluate.</param>
    /// <param name="options">The trusted source policy options.</param>
    /// <returns>A <see cref="TrustedSourcePolicyResult"/> indicating whether the source is allowed.</returns>
    public TrustedSourcePolicyResult Evaluate(string sourceName, TrustedSourcePolicyOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return TrustedSourcePolicyResult.Allowed(sourceName, "policy.disabled");
        }

        if (options.TrustedSourceNames.Count == 0)
        {
            return TrustedSourcePolicyResult.Rejected(sourceName, "policy.no_trusted_sources");
        }

        return options.TrustedSourceNames.Contains(sourceName)
            ? TrustedSourcePolicyResult.Allowed(sourceName, "policy.trusted")
            : TrustedSourcePolicyResult.Rejected(sourceName, "policy.untrusted");
    }

    /// <summary>
    /// Evaluates whether a credential reference is allowed based on the trusted source policy.
    /// </summary>
    /// <param name="credential">The credential string to evaluate.</param>
    /// <param name="options">The trusted source policy options.</param>
    /// <returns>A <see cref="TrustedSourcePolicyResult"/> indicating whether the credential is allowed.</returns>
    public TrustedSourcePolicyResult EvaluateCredential(string? credential, TrustedSourcePolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(credential))
        {
            return TrustedSourcePolicyResult.Allowed("credential", "credential.none");
        }

        if (credential.StartsWith("secrets://", StringComparison.OrdinalIgnoreCase))
        {
            return options.AllowSecretReferences
                ? TrustedSourcePolicyResult.Allowed("credential", "credential.secret_reference_allowed")
                : TrustedSourcePolicyResult.Rejected("credential", "credential.secret_reference_rejected");
        }

        return options.RejectInlineCredentials
            ? TrustedSourcePolicyResult.Rejected("credential", "credential.inline_rejected")
            : TrustedSourcePolicyResult.Allowed("credential", "credential.inline_allowed");
    }
}