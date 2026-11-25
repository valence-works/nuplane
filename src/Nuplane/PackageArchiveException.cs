namespace Nuplane;

/// <summary>Reports that a package archive was refused, naming the package and the reason.</summary>
public sealed class PackageArchiveException : Exception
{
    /// <summary>Initializes a refusal for one package.</summary>
    /// <param name="packageId">The refused package's identifier.</param>
    /// <param name="version">The refused package's version.</param>
    /// <param name="reason">Why the archive was refused.</param>
    /// <param name="detail">A sentence describing the refusal.</param>
    /// <param name="innerException">The underlying failure, when there is one.</param>
    public PackageArchiveException(
        string packageId,
        string version,
        PackageArchiveFailureReason reason,
        string detail,
        Exception? innerException = null)
        : base($"Package '{packageId}' {version} archive refused ({reason}): {detail}", innerException)
    {
        PackageId = packageId;
        Version = version;
        Reason = reason;
    }

    /// <summary>Gets the refused package's identifier.</summary>
    public string PackageId { get; }

    /// <summary>Gets the refused package's version.</summary>
    public string Version { get; }

    /// <summary>Gets why the archive was refused.</summary>
    public PackageArchiveFailureReason Reason { get; }
}
