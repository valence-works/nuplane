using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// A package load context that applies a shared-assembly policy in its <c>Load</c> override, resolving a matched
/// assembly from the host's default context instead of from the package.
/// </summary>
/// <remarks>
/// <c>AssemblyLoadContext.LoadFromAssemblyPath</c> never consults <c>Load</c>: it loads the file into the context,
/// and from then on the context answers every by-name request for that assembly with it. Code that loads a
/// package's files by path therefore asks the context first, and binds a shared assembly by name instead, so the
/// package keeps the host's copy.
/// </remarks>
internal interface ISharedAssemblyPolicyLoadContext
{
    /// <summary>
    /// Determines whether the context's shared-assembly policy matches <paramref name="assemblyName"/>, so that the
    /// context resolves it from the host's default context.
    /// </summary>
    /// <param name="assemblyName">The name of the assembly to check.</param>
    /// <returns><see langword="true"/> when the assembly is shared with the host; otherwise <see langword="false"/>.</returns>
    bool IsSharedAssembly(AssemblyName assemblyName);
}
