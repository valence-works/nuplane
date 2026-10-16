using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

internal static partial class ProtectionDigest
{
    /// <summary>Hashes every deletion-journal field except the stored integrity digest.</summary>
    internal static string PackageStoreDeletion(PackageStoreDeletionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return Digest("PackageStoreDeletionJournal", writer =>
        {
            writer.Field(1, EncodeInt32(journal.SchemaVersion));
            writer.Field(2, EncodePhysicalRoot(journal.StoreRoot));
            writer.Field(3, EncodeInt64(journal.EnrollmentEpoch));
            writer.Field(4, GuidBytes(journal.OperationId));
            writer.Field(5, GuidBytes(journal.CandidateId));
            writer.Field(6, EncodeInstall(journal.OriginalInstall));
            writer.Field(7, EncodeSequence(journal.PackageParentRelativeComponents, EncodeString));
            writer.Field(8, EncodePhysicalFile(journal.PackageParentIdentity));
            writer.Field(9, EncodeNameSemantics(journal.PackageParentSemantics));
            writer.Field(10, EncodeString(journal.OriginalBasename));
            writer.Field(11, EncodeString(journal.QuarantineBasename));
            writer.Field(12, EncodePhysicalFile(journal.CompletionMarkerIdentity));
            writer.Field(13, EncodeInt32(journal.KeepNewestInactiveVersionsPerPackage));
            writer.Field(14, EncodeEnum(journal.Phase));
            writer.Field(15, EncodeDeletionManifest(journal.Manifest));
        });
    }

    private static byte[] EncodeDeletionManifest(PhysicalStoreDeletionManifest manifest)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalFile(manifest.InstallDirectoryIdentity));
            writer.Field(2, EncodeNameSemantics(manifest.InstallDirectorySemantics));
            writer.Field(3, EncodeSequence(manifest.Entries, EncodeDeletionManifestEntry));
        });

    private static byte[] EncodeDeletionManifestEntry(PhysicalStoreDeletionManifestEntry entry)
        => Record(writer =>
        {
            writer.Field(1, EncodeSequence(entry.RelativeComponents, EncodeString));
            writer.Field(2, EncodeEnum(entry.Kind));
            writer.Field(3, EncodePhysicalFile(entry.Identity));
            writer.Field(4, EncodePhysicalFile(entry.ParentIdentity));
            writer.Field(5, EncodeNameSemantics(entry.ParentSemantics));
            writer.Field(6, entry.DirectorySemantics is null
                ? [0]
                : [1, .. EncodeNameSemantics(entry.DirectorySemantics)]);
        });
}
