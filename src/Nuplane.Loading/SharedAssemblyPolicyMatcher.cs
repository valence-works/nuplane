using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Determines whether a requested assembly matches the shared assembly policy,
/// comparing name, public key token, and major version.
/// </summary>
public sealed class SharedAssemblyPolicyMatcher
{
    /// <summary>
    /// Determines whether the specified assembly name matches any entry in the shared policy.
    /// </summary>
    /// <param name="requested">The assembly name being requested.</param>
    /// <param name="entries">The shared assembly policy entries to match against.</param>
    /// <returns><see langword="true"/> if the assembly matches the shared policy; otherwise <see langword="false"/>.</returns>
    public bool IsMatch(AssemblyName requested, IReadOnlyList<SharedAssemblyPolicyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(entries);

        var requestedToken = ToToken(requested.GetPublicKeyToken());
        var requestedMajor = requested.Version?.Major ?? 0;

        return entries.Any(x =>
            string.Equals(x.Name, requested.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.PublicKeyToken, requestedToken, StringComparison.OrdinalIgnoreCase) &&
            x.MajorVersion == requestedMajor);
    }

    private static string ToToken(byte[]? tokenBytes) =>
        tokenBytes is null || tokenBytes.Length == 0
            ? string.Empty
            : string.Concat(tokenBytes.Select(b => b.ToString("x2")));
}
