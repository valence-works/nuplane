namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>A counted, non-owning scope for package-store work under an admitted root operation.</summary>
/// <remarks>The caller that created the borrow disposes it; callees must not dispose a borrowed scope.</remarks>
public sealed class PackageStoreOperationBorrow : IDisposable
{
    private readonly IPackageStoreOperationOwnerControl _control;
    private int _disposed;

    internal PackageStoreOperationBorrow(PackageStoreOperationOwner owner, IPackageStoreOperationOwnerControl control)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>Gets the owner whose root and lifetime this borrow shares.</summary>
    internal PackageStoreOperationOwner Owner { get; }

    /// <summary>Gets the physical root associated with this borrow.</summary>
    public PhysicalRootIdentity Root => Owner.Root;

    /// <summary>Gets the positive admission epoch associated with this borrow.</summary>
    public long Epoch => Owner.Epoch;

    /// <summary>Releases this borrow's counted ownership exactly once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _control.ReleaseBorrow(this);
    }

    /// <summary>Validates that a path belongs to this live admitted operation.</summary>
    /// <param name="installPath">The install path to validate.</param>
    /// <exception cref="PackageStoreAdmissionException">The scope is expired or the path is outside its root.</exception>
    public void ValidateForInstallPath(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.ExpiredScope,
                "The package-store operation borrow has expired.",
                Root);
        }

        _control.ValidateForInstallPath(this, installPath);
    }
}
