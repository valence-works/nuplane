using System.Reflection;

namespace Nuplane.Loading;

public sealed class SharedAssemblyPolicyMatcher
{
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
