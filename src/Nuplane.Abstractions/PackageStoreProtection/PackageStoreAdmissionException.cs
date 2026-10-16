namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Thrown when package-store authority or a scoped package path cannot be validated.</summary>
public sealed class PackageStoreAdmissionException : InvalidOperationException
{
    /// <summary>Initializes an admission refusal.</summary>
    /// <param name="reason">The precise refusal category.</param>
    /// <param name="message">A diagnostic message safe to expose to the caller.</param>
    /// <param name="root">The physical root involved, when it could be identified.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public PackageStoreAdmissionException(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Root = root;
    }

    /// <summary>Gets the refusal category.</summary>
    public PackageStoreAdmissionReason Reason { get; }

    /// <summary>Gets the physical root involved, when it could be identified.</summary>
    public PhysicalRootIdentity? Root { get; }
}
