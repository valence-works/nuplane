namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Describes one path's classification within an atomic multi-root admission.</summary>
public sealed record PackageStorePathAdmissionEntry
{
    internal PackageStorePathAdmissionEntry(
        string installPath,
        PackageStoreAdmissionStatus status,
        PhysicalRootIdentity? root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == PackageStoreAdmissionStatus.Enrolled && root is null)
            throw new ArgumentException("An enrolled path must identify its physical root.", nameof(root));
        InstallPath = installPath;
        Status = status;
        Root = root;
    }

    /// <summary>Gets the requested install path.</summary>
    public string InstallPath { get; }

    /// <summary>Gets the path's admission status.</summary>
    public PackageStoreAdmissionStatus Status { get; }

    /// <summary>Gets the resolved physical root, when available.</summary>
    public PhysicalRootIdentity? Root { get; }
}
