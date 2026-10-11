namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>A non-disposable view of exact package identities protected by a graph-use lease.</summary>
public sealed class PackageGraphUseLease
{
    private readonly IPackageGraphUseLeaseOwnerControl _control;
    internal IPackageGraphUseLeaseOwnerControl Control => _control;

    internal PackageGraphUseLease(
        PackageGraphUseSnapshot snapshot,
        IPackageGraphUseLeaseOwnerControl control)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>Gets the immutable graph snapshot covered by this lease.</summary>
    public PackageGraphUseSnapshot Snapshot { get; }

    /// <summary>Opens a counted read scope for a path in this graph.</summary>
    /// <param name="installPath">The exact installed package path to read.</param>
    /// <returns>A disposable pin that must remain alive for the complete read.</returns>
    /// <exception cref="PackageStoreAdmissionException">The path is outside the graph or the lease is stale.</exception>
    public IDisposable AcquireRead(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return _control.AcquireRead(this, installPath);
    }

    /// <summary>Gets the immutable descriptive install identity bound to an exact original path.</summary>
    /// <remarks>This does not authorize filesystem access; callers must still hold an <see cref="AcquireRead"/> scope for the entire read.</remarks>
    /// <param name="installPath">The exact installed package path, compared using ordinal path identity.</param>
    /// <returns>The immutable identity Core paired with this exact path when the lease was published.</returns>
    /// <exception cref="PackageStoreAdmissionException">The exact path is outside this lease.</exception>
    public PackageInstallIdentity GetInstallIdentityForExactPath(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return _control.GetInstallIdentity(this, installPath);
    }
}
