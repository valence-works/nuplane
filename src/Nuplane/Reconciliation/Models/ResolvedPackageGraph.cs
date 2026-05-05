namespace Nuplane.Reconciliation.Models;

/// <summary>
/// Represents a deterministic dependency graph resolved for one or more desired package roots.
/// </summary>
/// <param name="GraphId">The stable graph identity.</param>
/// <param name="GenerationId">The active graph generation identity.</param>
/// <param name="TargetFramework">The target framework used for dependency and asset selection.</param>
/// <param name="Roots">The desired root nodes in the graph.</param>
/// <param name="Nodes">All selected package nodes in deterministic order.</param>
/// <param name="Edges">All dependency edges in deterministic order.</param>
/// <param name="SourceDecisions">The source/feed decisions that selected graph nodes.</param>
/// <param name="CreatedAtUtc">The UTC time at which the graph was resolved.</param>
public sealed record ResolvedPackageGraph(
    string GraphId,
    string GenerationId,
    string TargetFramework,
    IReadOnlyList<ResolvedPackageNode> Roots,
    IReadOnlyList<ResolvedPackageNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<FeedResolutionDecision> SourceDecisions,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Creates a deterministic graph identity from sorted graph content.
    /// </summary>
    public static string CreateGraphId(
        string targetFramework,
        IEnumerable<ResolvedPackageNode> roots,
        IEnumerable<ResolvedPackageNode> nodes,
        IEnumerable<DependencyEdge> edges,
        IEnumerable<FeedResolutionDecision> sourceDecisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(sourceDecisions);

        var parts = new List<string> { $"tfm:{targetFramework}" };
        parts.AddRange(roots
            .OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static node => $"root:{node.PackageId}@{node.Version}:{node.Role}"));
        parts.AddRange(nodes
            .OrderBy(static node => node.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static node => $"node:{node.PackageId}@{node.Version}:{node.Role}:{node.SourceKind}:{node.SourceName}:{node.PackageContentHash}"));
        parts.AddRange(edges
            .OrderBy(static edge => edge.FromPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.FromVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.ToPackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.SelectedVersion, StringComparer.OrdinalIgnoreCase)
            .Select(static edge => $"edge:{edge.FromPackageId}@{edge.FromVersion}>{edge.ToPackageId}@{edge.SelectedVersion}:{edge.RequestedVersionRange}:{edge.DependencyGroupTargetFramework}:{edge.Optional}"));
        parts.AddRange(sourceDecisions
            .OrderBy(static decision => decision.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static decision => decision.SelectedVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static decision => decision.SelectedFeed, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static decision => decision.DecisionPath, StringComparer.OrdinalIgnoreCase)
            .Select(static decision => $"source:{decision.PackageId}@{decision.SelectedVersion}:{decision.RequestedFeed}:{decision.SelectedFeed}:{string.Join(',', decision.CandidateFeeds)}:{decision.DecisionPath}:{decision.FailureReason}"));

        var payload = string.Join('\n', parts);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
