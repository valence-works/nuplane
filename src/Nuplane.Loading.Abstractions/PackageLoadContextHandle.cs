namespace Nuplane.Loading;

/// <summary>
/// Wraps a reference to a package's assembly load context, enabling the runtime to manage
/// its lifecycle (including unloading) without directly depending on the load context type.
/// </summary>
/// <param name="ContextKey">The unique key identifying the assembly load context.</param>
/// <param name="Context">The underlying load context object.</param>
internal sealed record PackageLoadContextHandle(
    string ContextKey,
    object Context);