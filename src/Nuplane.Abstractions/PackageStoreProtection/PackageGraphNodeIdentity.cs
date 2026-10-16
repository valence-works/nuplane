namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Identifies one selected package node in a copied graph-use snapshot.</summary>
public sealed record PackageGraphNodeIdentity
{
    /// <summary>Initializes a graph node identity for use by Nuplane core.</summary>
    /// <param name="nodeId">The non-empty stable node identifier within the snapshot.</param>
    /// <param name="install">The exact installed package identity for the node.</param>
    /// <exception cref="ArgumentException"><paramref name="nodeId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="install"/> is null.</exception>
    internal PackageGraphNodeIdentity(Guid nodeId, PackageInstallIdentity install)
    {
        if (nodeId == Guid.Empty)
            throw new ArgumentException("A graph node identifier cannot be empty.", nameof(nodeId));

        ArgumentNullException.ThrowIfNull(install);
        NodeId = nodeId;
        Install = install;
    }

    /// <summary>Gets the stable node identifier within the snapshot.</summary>
    public Guid NodeId { get; }

    /// <summary>Gets the exact installed package identity.</summary>
    public PackageInstallIdentity Install { get; }
}
