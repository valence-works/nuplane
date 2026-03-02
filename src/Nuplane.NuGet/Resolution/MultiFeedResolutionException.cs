namespace Nuplane.NuGet.Resolution;

public sealed class MultiFeedResolutionException(string packageId, string message)
    : InvalidOperationException($"Multi-feed resolution failed for '{packageId}': {message}");