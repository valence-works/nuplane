namespace Nuplane.Trust.Source;

/// <summary>
/// Represents the result of a trusted source policy evaluation.
/// </summary>
/// <param name="SourceName">The name of the source or credential that was evaluated.</param>
/// <param name="IsAllowed">Whether the source or credential is allowed.</param>
/// <param name="ReasonCode">The reason code for the evaluation result.</param>
public sealed record TrustedSourcePolicyResult(string SourceName, bool IsAllowed, string ReasonCode)
{
    /// <summary>
    /// Creates an allowed result.
    /// </summary>
    internal static TrustedSourcePolicyResult Allowed(string sourceName, string reasonCode) =>
        new(sourceName, true, reasonCode);

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    internal static TrustedSourcePolicyResult Rejected(string sourceName, string reasonCode) =>
        new(sourceName, false, reasonCode);
}