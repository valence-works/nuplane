using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination.DeletionRecords;

/// <summary>Describes an exact bounded set of entries beneath one install-directory identity.</summary>
/// <remarks>
/// This immutable value can represent a surviving subset, including a markerless finalizing tree. A
/// <see cref="PackageStoreDeletionJournal"/> separately requires the mandatory root completion-marker baseline row.
/// The value is descriptive evidence only and does not authorize mutation.
/// </remarks>
internal sealed class PhysicalStoreDeletionManifest
{
    internal const int MaximumEntries = 16_384;
    internal const int MaximumChildrenPerDirectory = 1_024;
    internal const int MaximumDepth = 64;

    internal PhysicalStoreDeletionManifest(
        PhysicalFileIdentity installDirectoryIdentity,
        PhysicalStoreNameSemantics installDirectorySemantics,
        IEnumerable<PhysicalStoreDeletionManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(installDirectoryIdentity);
        ArgumentNullException.ThrowIfNull(installDirectorySemantics);
        ArgumentNullException.ThrowIfNull(entries);

        InstallDirectoryIdentity = CopyIdentity(installDirectoryIdentity);
        InstallDirectorySemantics = CopySemantics(installDirectorySemantics);

        var copied = CopyEntries(entries);
        copied.Sort(ManifestEntryComparer.Instance);
        ValidateEntries(InstallDirectoryIdentity, InstallDirectorySemantics, copied);
        Entries = copied.ToImmutableArray();
    }

    internal PhysicalFileIdentity InstallDirectoryIdentity { get; }
    internal PhysicalStoreNameSemantics InstallDirectorySemantics { get; }
    internal ImmutableArray<PhysicalStoreDeletionManifestEntry> Entries { get; }

    /// <summary>Requires every observed row to be an exact surviving row from this baseline.</summary>
    /// <returns>The number of baseline rows missing from the supplied observation; this does not establish native absence.</returns>
    /// <exception cref="PackageStoreAdmissionException">The root/profile or any surviving row differs from the baseline.</exception>
    internal int RequireExactSubset(PhysicalStoreDeletionManifest observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (InstallDirectoryIdentity != observed.InstallDirectoryIdentity ||
            InstallDirectorySemantics != observed.InstallDirectorySemantics)
        {
            throw Refused("The observed deletion tree has a different install-root identity or name profile.");
        }

        var baseline = Entries.ToDictionary(static entry => GetPathKey(entry.RelativeComponents), StringComparer.Ordinal);
        foreach (var actual in observed.Entries)
        {
            var key = GetPathKey(actual.RelativeComponents);
            if (!baseline.TryGetValue(key, out var expected) || !HasSameBinding(expected, actual))
                throw Refused("The observed deletion tree contains an added or replaced entry.");
        }

        return Entries.Length - observed.Entries.Length;
    }

    internal static string GetPathKey(IEnumerable<string> components)
        => string.Join("/", components);

    private static List<PhysicalStoreDeletionManifestEntry> CopyEntries(
        IEnumerable<PhysicalStoreDeletionManifestEntry> entries)
    {
        var copied = new List<PhysicalStoreDeletionManifestEntry>();
        foreach (var entry in entries)
        {
            if (copied.Count == MaximumEntries)
                throw new ArgumentOutOfRangeException(nameof(entries), "The deletion manifest exceeds its entry limit.");
            copied.Add(entry ?? throw new ArgumentException("A deletion manifest cannot contain a null row.", nameof(entries)));
        }

        return copied;
    }

    private static void ValidateEntries(
        PhysicalFileIdentity installDirectoryIdentity,
        PhysicalStoreNameSemantics installDirectorySemantics,
        IReadOnlyList<PhysicalStoreDeletionManifestEntry> entries)
    {
        var byPath = new Dictionary<string, PhysicalStoreDeletionManifestEntry>(StringComparer.Ordinal);
        var identities = new HashSet<PhysicalFileIdentity> { installDirectoryIdentity };
        var childCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.RelativeComponents.Length == 0 || entry.RelativeComponents.Length > MaximumDepth)
                throw new ArgumentOutOfRangeException(nameof(entries), "A deletion manifest row has an invalid depth.");

            var path = GetPathKey(entry.RelativeComponents);
            if (!byPath.TryAdd(path, entry))
                throw new ArgumentException("Deletion manifest relative paths must be unique.", nameof(entries));
            if (!identities.Add(entry.Identity))
                throw new ArgumentException("Distinct deletion manifest objects must have unique native identities.", nameof(entries));
            RequireSameVolume(installDirectoryIdentity, entry.Identity);

            var parentPath = GetPathKey(entry.RelativeComponents.Take(entry.RelativeComponents.Length - 1));
            if (!childCounts.TryAdd(parentPath, 1))
                childCounts[parentPath]++;
            if (childCounts[parentPath] > MaximumChildrenPerDirectory)
                throw new ArgumentOutOfRangeException(nameof(entries), "A deletion manifest directory exceeds its direct-child limit.");
        }

        foreach (var entry in entries)
        {
            var parentPath = GetPathKey(entry.RelativeComponents.Take(entry.RelativeComponents.Length - 1));
            if (parentPath.Length == 0)
            {
                if (entry.ParentIdentity != installDirectoryIdentity || entry.ParentSemantics != installDirectorySemantics)
                    throw new ArgumentException("A direct child must bind the manifest install directory and its name profile.", nameof(entries));
                continue;
            }

            if (!byPath.TryGetValue(parentPath, out var parent) || parent.Kind != PhysicalStoreEntryKind.Directory ||
                parent.Identity != entry.ParentIdentity || parent.DirectorySemantics != entry.ParentSemantics)
            {
                throw new ArgumentException("A nested child must bind its exact manifest directory parent and name profile.", nameof(entries));
            }
        }
    }

    private static bool HasSameBinding(
        PhysicalStoreDeletionManifestEntry expected,
        PhysicalStoreDeletionManifestEntry observed)
        => expected.Kind == observed.Kind &&
           expected.Identity == observed.Identity &&
           expected.ParentIdentity == observed.ParentIdentity &&
           expected.ParentSemantics == observed.ParentSemantics &&
           expected.DirectorySemantics == observed.DirectorySemantics;

    private static void RequireSameVolume(PhysicalFileIdentity root, PhysicalFileIdentity entry)
    {
        if (!string.Equals(root.Provider, entry.Provider, StringComparison.Ordinal) ||
            !string.Equals(root.VolumeOrDeviceId, entry.VolumeOrDeviceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Every deletion manifest object must share the install directory's provider and volume.");
        }
    }

    private static PhysicalFileIdentity CopyIdentity(PhysicalFileIdentity identity)
        => new(identity.Provider, identity.VolumeOrDeviceId, identity.FileId);

    private static PhysicalStoreNameSemantics CopySemantics(PhysicalStoreNameSemantics semantics)
        => new(semantics.ProfileId, semantics.Encoding, semantics.CaseSensitive, semantics.NormalizationInsensitive);

    private static PackageStoreAdmissionException Refused(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    private sealed class ManifestEntryComparer : IComparer<PhysicalStoreDeletionManifestEntry>
    {
        internal static ManifestEntryComparer Instance { get; } = new();

        public int Compare(PhysicalStoreDeletionManifestEntry? left, PhysicalStoreDeletionManifestEntry? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var sharedLength = Math.Min(left.RelativeComponents.Length, right.RelativeComponents.Length);
            for (var index = 0; index < sharedLength; index++)
            {
                var comparison = StringComparer.Ordinal.Compare(
                    left.RelativeComponents[index], right.RelativeComponents[index]);
                if (comparison != 0)
                    return comparison;
            }

            return left.RelativeComponents.Length.CompareTo(right.RelativeComponents.Length);
        }
    }
}
