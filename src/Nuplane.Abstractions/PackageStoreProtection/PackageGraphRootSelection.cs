using Nuplane.Abstractions;

namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Preserves one requested root and the selected graph node that satisfies it.</summary>
public sealed record PackageGraphRootSelection
{
    /// <summary>Initializes a root selection for a core-validated graph snapshot.</summary>
    /// <param name="request">The original requested package root.</param>
    /// <param name="selectedNodeId">The selected root node satisfying the request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="selectedNodeId"/> is empty.</exception>
    internal PackageGraphRootSelection(PackageRequest request, Guid selectedNodeId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (selectedNodeId == Guid.Empty)
            throw new ArgumentException("A selected root node identifier cannot be empty.", nameof(selectedNodeId));

        Request = request;
        SelectedNodeId = selectedNodeId;
    }

    /// <summary>Gets the original requested root, distinct from its dependency nodes.</summary>
    public PackageRequest Request { get; }

    /// <summary>Gets the selected graph node that satisfies <see cref="Request"/>.</summary>
    public Guid SelectedNodeId { get; }
}
