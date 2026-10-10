using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>A detached snapshot of one bounded physical package-store observation.</summary>
/// <remarks>
/// A complete snapshot means every in-scope directory was observed within the fixed bounds. Unknown,
/// staging, control, and incomplete entries may still be present. A snapshot is descriptive and does
/// not prove that the namespace remains unchanged or authorize later access or deletion.
/// </remarks>
internal sealed record PackageStoreInventorySnapshot
{
    internal PackageStoreInventorySnapshot(
        PhysicalRootIdentity root,
        long epoch,
        bool isComplete,
        IEnumerable<PackageStoreInventoryEntry> entries,
        IEnumerable<PackageStoreInventoryIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(issues);
        if (epoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(epoch));

        var entryArray = entries.OrderBy(static entry => entry.RootRelativePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Kind)
            .ToArray();
        var issueArray = issues.OrderBy(static issue => issue.RootRelativePath, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Reason, StringComparer.Ordinal)
            .ToArray();
        if (isComplete != (issueArray.Length == 0))
            throw new ArgumentException("A complete snapshot has no uncertainty issues, and an incomplete snapshot records at least one.", nameof(isComplete));

        Root = root;
        Epoch = epoch;
        IsComplete = isComplete;
        Entries = new ReadOnlyCollection<PackageStoreInventoryEntry>(entryArray);
        Issues = new ReadOnlyCollection<PackageStoreInventoryIssue>(issueArray);
    }

    internal PhysicalRootIdentity Root { get; }
    internal long Epoch { get; }
    internal bool IsComplete { get; }
    internal IReadOnlyList<PackageStoreInventoryEntry> Entries { get; }
    internal IReadOnlyList<PackageStoreInventoryIssue> Issues { get; }
}
