namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>
/// Identifies one immutable installed package directory within a physical package-store root.
/// </summary>
/// <remarks>
/// This public value does not authorize access. Core validates its root and native directory facts
/// against the current admitted store before accepting it in a graph snapshot or lease.
/// </remarks>
public sealed record PackageInstallIdentity
{
    /// <summary>Initializes an immutable package-install identity value.</summary>
    /// <param name="root">The physical root containing the installation.</param>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The selected package version.</param>
    /// <param name="rootRelativeInstallPath">The normalized slash-separated install path relative to <paramref name="root"/>.</param>
    /// <param name="directoryIdentity">The native identity of the installed directory.</param>
    /// <param name="completionIdentity">The validated identity of the install-completion record.</param>
    /// <param name="verifiedArchiveHash">The verified package archive hash, when available.</param>
    /// <exception cref="ArgumentNullException">A required identity argument is null.</exception>
    /// <exception cref="ArgumentException">A required text value is blank, the relative path is not normalized, or the archive hash is blank.</exception>
    public PackageInstallIdentity(
        PhysicalRootIdentity root,
        string packageId,
        string version,
        string rootRelativeInstallPath,
        PhysicalFileIdentity directoryIdentity,
        string completionIdentity,
        string? verifiedArchiveHash = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRelativeInstallPath);
        ArgumentNullException.ThrowIfNull(directoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(completionIdentity);

        if (rootRelativeInstallPath.StartsWith("/", StringComparison.Ordinal) ||
            rootRelativeInstallPath.Contains(':') ||
            rootRelativeInstallPath.Contains('\0') ||
            rootRelativeInstallPath.Contains('\\') ||
            rootRelativeInstallPath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The install path must be a normalized root-relative path.", nameof(rootRelativeInstallPath));
        }

        if (verifiedArchiveHash is not null && string.IsNullOrWhiteSpace(verifiedArchiveHash))
            throw new ArgumentException("The verified archive hash cannot be blank.", nameof(verifiedArchiveHash));

        Root = root;
        PackageId = packageId;
        Version = version;
        RootRelativeInstallPath = rootRelativeInstallPath;
        DirectoryIdentity = directoryIdentity;
        CompletionIdentity = completionIdentity;
        VerifiedArchiveHash = verifiedArchiveHash;
    }

    /// <summary>Gets the physical root containing this installation.</summary>
    public PhysicalRootIdentity Root { get; }

    /// <summary>Gets the NuGet package identifier.</summary>
    public string PackageId { get; }

    /// <summary>Gets the selected package version.</summary>
    public string Version { get; }

    /// <summary>Gets the normalized path relative to <see cref="Root"/>.</summary>
    public string RootRelativeInstallPath { get; }

    /// <summary>Gets the native identity of the installed directory.</summary>
    public PhysicalFileIdentity DirectoryIdentity { get; }

    /// <summary>Gets the validated identity of the install-completion record.</summary>
    public string CompletionIdentity { get; }

    /// <summary>Gets the verified archive hash, when the archive hash is available.</summary>
    public string? VerifiedArchiveHash { get; }
}
