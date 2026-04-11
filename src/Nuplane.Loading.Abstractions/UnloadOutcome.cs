namespace Nuplane.Loading;

/// <summary>
/// Describes the outcome of attempting to unload a package assembly load context.
/// </summary>
internal enum UnloadOutcome
{
    /// <summary>The assembly load context was fully unloaded and garbage collected.</summary>
    Unloaded,
    /// <summary>The unload was initiated but the context is still alive (pending GC collection).</summary>
    UnloadPending,
    /// <summary>The unload attempt failed with an error.</summary>
    Failed
}