using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

/// <summary>
/// Resolves the host's copy of an assembly the shared-assembly policy matches, and decides whether it satisfies
/// the policy. A shared assembly is the host's to provide, so a host with no copy of it, or one of a different
/// major version, leaves the package that carries it unable to run.
/// </summary>
internal static class SharedAssemblyHostCopy
{
    /// <summary>
    /// Determines whether the host's default context has a copy of <paramref name="policyMatched"/> whose major
    /// version equals the policy's. The policy matches on the requested major version, so the requested assembly's
    /// major version is the policy entry's <c>MajorVersion</c>.
    /// </summary>
    /// <param name="policyMatched">The name of an assembly the shared-assembly policy matched.</param>
    /// <returns><see langword="true"/> when the host has a satisfying copy; otherwise <see langword="false"/>.</returns>
    internal static bool HostSatisfies(AssemblyName policyMatched)
    {
        try
        {
            var hostCopy = AssemblyLoadContext.Default.LoadFromAssemblyName(policyMatched);
            return hostCopy.GetName().Version?.Major == (policyMatched.Version?.Major ?? 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Describes the policy entry that matched <paramref name="policyMatched"/>, as name, token and major version.</summary>
    internal static string DescribePolicyEntry(AssemblyName policyMatched)
    {
        var token = policyMatched.GetPublicKeyToken();
        var tokenText = token is null || token.Length == 0 ? "unsigned" : Convert.ToHexString(token).ToLowerInvariant();
        return $"'{policyMatched.Name}' (public key token: {tokenText}, major version: {policyMatched.Version?.Major ?? 0})";
    }
}
