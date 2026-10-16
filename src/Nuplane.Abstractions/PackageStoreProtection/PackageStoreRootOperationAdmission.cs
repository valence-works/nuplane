namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Describes admission for one configured physical root.</summary>
public sealed class PackageStoreRootOperationAdmission : IAsyncDisposable
{
    /// <summary>Initializes an immutable admission result.</summary>
    /// <param name="status">Whether this root participates in protection.</param>
    /// <param name="root">The resolved physical root, when available.</param>
    /// <param name="owner">The operation owner for an enrolled root.</param>
    /// <exception cref="ArgumentException">The status and owner combination is inconsistent.</exception>
    internal PackageStoreRootOperationAdmission(
        PackageStoreAdmissionStatus status,
        PhysicalRootIdentity? root,
        PackageStoreOperationOwner? owner)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == PackageStoreAdmissionStatus.Enrolled && (root is null || owner is null))
            throw new ArgumentException("Enrolled admission requires a root and operation owner.");
        if (status == PackageStoreAdmissionStatus.Enrolled && !object.Equals(root, owner!.Root))
            throw new ArgumentException("The enrolled admission root must match its operation owner's root.", nameof(root));
        if (status == PackageStoreAdmissionStatus.Unenrolled && owner is not null)
            throw new ArgumentException("Unenrolled admission cannot fabricate an operation owner.");

        Status = status;
        Root = root;
        Owner = owner;
    }

    /// <summary>Gets the admission status.</summary>
    public PackageStoreAdmissionStatus Status { get; }

    /// <summary>Gets the resolved physical root, when available.</summary>
    public PhysicalRootIdentity? Root { get; }

    /// <summary>Gets the opaque owner for an enrolled root; otherwise null.</summary>
    public PackageStoreOperationOwner? Owner { get; }

    /// <summary>Disposes the enrolled owner, if present.</summary>
    public ValueTask DisposeAsync() => Owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
