namespace Nuplane.Store.Coordination;

/// <summary>Signals only a stable ordinary directory appearing at an opted-in unenrolled probe edge.</summary>
/// <remarks>
/// This is an internal retry cause, never a public admission result. The runtime may catch it only before the
/// unenrolled metadata callback, and must preserve it as the inner cause if bounded reclassification is exhausted.
/// </remarks>
internal sealed class PackageStoreDirectoryAppearanceRetryException : Exception
{
    internal PackageStoreDirectoryAppearanceRetryException()
        : base("A previously absent package-directory edge appeared during native classification.")
    {
    }
}
