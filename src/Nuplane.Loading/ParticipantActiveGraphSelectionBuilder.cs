using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Loading;

/// <summary>Joins host grouping to complete verified persisted active-graph candidates.</summary>
internal static class ParticipantActiveGraphSelectionBuilder
{
    internal static IReadOnlyList<Selection> Build(ActivePackagesSnapshot snapshot, StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        var packages = snapshot.Packages.Select(static package => new ResolvedPackage(
            package.PackageId, package.Version, package.FeedName ?? string.Empty, package.InstallPath,
            package.ActivatedAtUtc, package.SourceName ?? string.Empty)).ToArray();
        var grouped = PackageGraphGrouping.ForActiveState(packages, state);
        var candidates = PersistedStoreStateGraphVerifier.Verify(state).ActiveGraphs;
        var used = new HashSet<ProtectedGraphSnapshot>(ReferenceEqualityComparer.Instance);
        var selections = new List<Selection>(grouped.Count);

        foreach (var group in grouped)
        {
            var matches = candidates.Where(candidate => MatchesExactly(candidate, group)).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0]))
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnknownAuthority,
                    matches.Length == 0
                        ? "The active package group has no exact verified persisted graph candidate."
                        : "The active package group does not select one unique verified persisted graph candidate.");

            var candidate = matches[0];
            if (candidate.Roots.Count != 1 || candidate.Nodes.Any(node => node.Install.Root != candidate.Roots[0]))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Participant host-integrated loading currently requires each complete active graph to belong to exactly one physical root.",
                    candidate.Roots.Count == 1 ? candidate.Roots[0] : null);

            var nodesByPackage = candidate.Nodes.ToDictionary(
                static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase);
            var selectedPackages = group.Select(package =>
            {
                if (!nodesByPackage.TryGetValue(package.Id, out var node) ||
                    !string.Equals(package.Version, node.Install.Version, StringComparison.Ordinal))
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                        "The active descriptor differs from its verified persisted graph candidate.", candidate.Roots[0]);
                return package with { PackageContentHash = node.Install.VerifiedArchiveHash };
            }).ToArray();

            var pathByNode = new Dictionary<Guid, string>();
            foreach (var node in candidate.Nodes)
            {
                var package = selectedPackages.Single(item => string.Equals(
                    item.Id, node.Install.PackageId, StringComparison.OrdinalIgnoreCase));
                pathByNode.Add(node.NodeId, package.InstallPath);
            }
            selections.Add(new(candidate, selectedPackages, pathByNode));
        }

        if (used.Count != candidates.Count)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The verified active graph set does not exactly cover the host's active package groups.");
        return selections;
    }

    private static bool MatchesExactly(ProtectedGraphSnapshot candidate, IReadOnlyList<ResolvedPackage> group)
    {
        if ((candidate.Disposition & ProtectedGraphDisposition.Active) == 0 || candidate.Nodes.Count != group.Count)
            return false;
        return candidate.Nodes.All(node => group.Any(package =>
            string.Equals(package.Id, node.Install.PackageId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.Version, node.Install.Version, StringComparison.Ordinal)));
    }

    internal sealed record Selection(
        ProtectedGraphSnapshot Graph,
        IReadOnlyList<ResolvedPackage> Packages,
        IReadOnlyDictionary<Guid, string> ExactPathsByNode);
}
