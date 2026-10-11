namespace Nuplane.Store.Maintenance;

/// <summary>Describes a part of the root whose inventory could not be completed with certainty.</summary>
internal sealed record PackageStoreInventoryIssue
{
    internal PackageStoreInventoryIssue(string rootRelativePath, string reason)
    {
        ArgumentNullException.ThrowIfNull(rootRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (rootRelativePath.Length != 0)
        {
            var components = rootRelativePath.Split('/', StringSplitOptions.None);
            if (components.Any(static component => component is "" or "." or ".."))
                throw new ArgumentException("An inventory issue path must be root-relative and slash-separated.", nameof(rootRelativePath));
            foreach (var component in components)
                Nuplane.Store.Coordination.PhysicalFiles.PhysicalStoreNames.ValidateSingleComponent(component);
        }

        RootRelativePath = rootRelativePath;
        Reason = reason;
    }

    /// <summary>An empty path denotes a scan-wide issue at the root.</summary>
    internal string RootRelativePath { get; }
    internal string Reason { get; }
}
