using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.Coordination.DeletionSerialization;

internal sealed class PackageStoreDeletionJournalDto
{
    public int? SchemaVersion { get; set; }
    public PhysicalRootIdentityDto? StoreRoot { get; set; }
    public long? EnrollmentEpoch { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? CandidateId { get; set; }
    public PackageInstallIdentityDto? OriginalInstall { get; set; }
    public List<string?>? PackageParentRelativeComponents { get; set; }
    public PhysicalFileIdentityDto? PackageParentIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? PackageParentSemantics { get; set; }
    public string? OriginalBasename { get; set; }
    public string? QuarantineBasename { get; set; }
    public PhysicalFileIdentityDto? CompletionMarkerIdentity { get; set; }
    public int? KeepNewestInactiveVersionsPerPackage { get; set; }
    public int? Phase { get; set; }
    public PhysicalStoreDeletionManifestDto? Manifest { get; set; }
    public string? IntegrityDigest { get; set; }

    internal PackageStoreDeletionJournal ToJournal()
    {
        if (SchemaVersion != PackageStoreDeletionJournal.CurrentSchemaVersion)
            throw new JsonException("The deletion-journal schema version is unsupported.");

        var digest = DeletionDtoChecks.RequiredReference(IntegrityDigest, nameof(IntegrityDigest), DeletionDtoChecks.JournalOwner);
        ProtectionDigest.ValidateCanonicalDigest(digest);
        var phase = ReadEnum<PackageStoreDeletionPhase>(Phase, nameof(Phase));
        var parentComponents = DeletionDtoChecks.MapRequired(
            PackageParentRelativeComponents,
            nameof(PackageParentRelativeComponents),
            DeletionDtoChecks.JournalOwner,
            static component => component);

        return new PackageStoreDeletionJournal(
            SchemaVersion.Value,
            DeletionDtoChecks.RequiredReference(StoreRoot, nameof(StoreRoot), DeletionDtoChecks.JournalOwner).ToIdentity(),
            DeletionDtoChecks.RequiredValue(EnrollmentEpoch, nameof(EnrollmentEpoch), DeletionDtoChecks.JournalOwner),
            DeletionDtoChecks.RequiredValue(OperationId, nameof(OperationId), DeletionDtoChecks.JournalOwner),
            DeletionDtoChecks.RequiredValue(CandidateId, nameof(CandidateId), DeletionDtoChecks.JournalOwner),
            DeletionDtoChecks.RequiredReference(OriginalInstall, nameof(OriginalInstall), DeletionDtoChecks.JournalOwner).ToInstallIdentity(),
            parentComponents,
            DeletionDtoChecks.RequiredReference(PackageParentIdentity, nameof(PackageParentIdentity), DeletionDtoChecks.JournalOwner).ToIdentity(),
            DeletionDtoChecks.RequiredReference(PackageParentSemantics, nameof(PackageParentSemantics), DeletionDtoChecks.JournalOwner).ToSemantics(),
            DeletionDtoChecks.RequiredReference(OriginalBasename, nameof(OriginalBasename), DeletionDtoChecks.JournalOwner),
            DeletionDtoChecks.RequiredReference(QuarantineBasename, nameof(QuarantineBasename), DeletionDtoChecks.JournalOwner),
            DeletionDtoChecks.RequiredReference(CompletionMarkerIdentity, nameof(CompletionMarkerIdentity), DeletionDtoChecks.JournalOwner).ToIdentity(),
            DeletionDtoChecks.RequiredValue(KeepNewestInactiveVersionsPerPackage, nameof(KeepNewestInactiveVersionsPerPackage), DeletionDtoChecks.JournalOwner),
            phase,
            DeletionDtoChecks.RequiredReference(Manifest, nameof(Manifest), DeletionDtoChecks.JournalOwner).ToManifest(),
            digest);
    }

    internal static PackageStoreDeletionJournalDto FromJournal(PackageStoreDeletionJournal journal)
        => new()
        {
            SchemaVersion = journal.SchemaVersion,
            StoreRoot = PhysicalRootIdentityDto.FromIdentity(journal.StoreRoot),
            EnrollmentEpoch = journal.EnrollmentEpoch,
            OperationId = journal.OperationId,
            CandidateId = journal.CandidateId,
            OriginalInstall = PackageInstallIdentityDto.FromInstallIdentity(journal.OriginalInstall),
            PackageParentRelativeComponents = journal.PackageParentRelativeComponents.Cast<string?>().ToList(),
            PackageParentIdentity = PhysicalFileIdentityDto.FromIdentity(journal.PackageParentIdentity),
            PackageParentSemantics = PhysicalStoreNameSemanticsDto.FromSemantics(journal.PackageParentSemantics),
            OriginalBasename = journal.OriginalBasename,
            QuarantineBasename = journal.QuarantineBasename,
            CompletionMarkerIdentity = PhysicalFileIdentityDto.FromIdentity(journal.CompletionMarkerIdentity),
            KeepNewestInactiveVersionsPerPackage = journal.KeepNewestInactiveVersionsPerPackage,
            Phase = (int)journal.Phase,
            Manifest = PhysicalStoreDeletionManifestDto.FromManifest(journal.Manifest),
            IntegrityDigest = journal.IntegrityDigest
        };

    private static TEnum ReadEnum<TEnum>(int? raw, string name) where TEnum : struct, Enum
    {
        var value = DeletionDtoChecks.RequiredValue(raw, name, DeletionDtoChecks.JournalOwner);
        var parsed = (TEnum)Enum.ToObject(typeof(TEnum), value);
        return Enum.IsDefined(parsed) ? parsed : throw new JsonException($"The deletion journal has an unsupported '{name}'.");
    }
}

internal sealed class PhysicalStoreDeletionManifestDto
{
    public PhysicalFileIdentityDto? InstallDirectoryIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? InstallDirectorySemantics { get; set; }
    public List<PhysicalStoreDeletionManifestEntryDto?>? Entries { get; set; }

    internal PhysicalStoreDeletionManifest ToManifest()
        => new(
            DeletionDtoChecks.RequiredReference(InstallDirectoryIdentity, nameof(InstallDirectoryIdentity), DeletionDtoChecks.ManifestOwner).ToIdentity(),
            DeletionDtoChecks.RequiredReference(InstallDirectorySemantics, nameof(InstallDirectorySemantics), DeletionDtoChecks.ManifestOwner).ToSemantics(),
            DeletionDtoChecks.MapRequired(Entries, nameof(Entries), DeletionDtoChecks.ManifestOwner, static entry => entry.ToEntry()));

    internal static PhysicalStoreDeletionManifestDto FromManifest(PhysicalStoreDeletionManifest manifest)
        => new()
        {
            InstallDirectoryIdentity = PhysicalFileIdentityDto.FromIdentity(manifest.InstallDirectoryIdentity),
            InstallDirectorySemantics = PhysicalStoreNameSemanticsDto.FromSemantics(manifest.InstallDirectorySemantics),
            Entries = manifest.Entries.Select(static entry => (PhysicalStoreDeletionManifestEntryDto?)PhysicalStoreDeletionManifestEntryDto.FromEntry(entry)).ToList()
        };

}

internal sealed class PhysicalStoreDeletionManifestEntryDto
{
    public List<string?>? RelativeComponents { get; set; }
    public int? Kind { get; set; }
    public PhysicalFileIdentityDto? Identity { get; set; }
    public PhysicalFileIdentityDto? ParentIdentity { get; set; }
    public PhysicalStoreNameSemanticsDto? ParentSemantics { get; set; }
    public PhysicalStoreNameSemanticsDto? DirectorySemantics { get; set; }

    internal PhysicalStoreDeletionManifestEntry ToEntry()
        => new(
            DeletionDtoChecks.MapRequired(
                RelativeComponents,
                nameof(RelativeComponents),
                DeletionDtoChecks.ManifestEntryOwner,
                static component => component,
                nullItemMessage: "A deletion manifest path cannot contain null components."),
            ReadEntryKind(Kind),
            DeletionDtoChecks.RequiredReference(Identity, nameof(Identity), DeletionDtoChecks.ManifestEntryOwner).ToIdentity(),
            DeletionDtoChecks.RequiredReference(ParentIdentity, nameof(ParentIdentity), DeletionDtoChecks.ManifestEntryOwner).ToIdentity(),
            DeletionDtoChecks.RequiredReference(ParentSemantics, nameof(ParentSemantics), DeletionDtoChecks.ManifestEntryOwner).ToSemantics(),
            DirectorySemantics?.ToSemantics());

    internal static PhysicalStoreDeletionManifestEntryDto FromEntry(PhysicalStoreDeletionManifestEntry entry)
        => new()
        {
            RelativeComponents = entry.RelativeComponents.Cast<string?>().ToList(),
            Kind = (int)entry.Kind,
            Identity = PhysicalFileIdentityDto.FromIdentity(entry.Identity),
            ParentIdentity = PhysicalFileIdentityDto.FromIdentity(entry.ParentIdentity),
            ParentSemantics = PhysicalStoreNameSemanticsDto.FromSemantics(entry.ParentSemantics),
            DirectorySemantics = entry.DirectorySemantics is null
                ? null
                : PhysicalStoreNameSemanticsDto.FromSemantics(entry.DirectorySemantics)
        };

    private static PhysicalStoreEntryKind ReadEntryKind(int? raw)
    {
        var value = DeletionDtoChecks.RequiredValue(raw, "kind", DeletionDtoChecks.ManifestEntryOwner);
        var kind = (PhysicalStoreEntryKind)Enum.ToObject(typeof(PhysicalStoreEntryKind), value);
        return kind is PhysicalStoreEntryKind.Directory or PhysicalStoreEntryKind.RegularFile
            ? kind
            : throw new JsonException("A deletion manifest entry has an unsupported kind.");
    }

}

internal sealed class PhysicalStoreNameSemanticsDto
{
    public string? ProfileId { get; set; }
    public int? Encoding { get; set; }
    public bool? CaseSensitive { get; set; }
    public bool? NormalizationInsensitive { get; set; }

    internal PhysicalStoreNameSemantics ToSemantics()
    {
        var encodingValue = DeletionDtoChecks.RequiredValue(Encoding, "encoding", DeletionDtoChecks.NameProfileOwner);
        var encoding = (PhysicalStoreNameEncoding)Enum.ToObject(typeof(PhysicalStoreNameEncoding), encodingValue);
        if (!Enum.IsDefined(encoding))
            throw new JsonException("A native name profile has an unsupported encoding.");
        return new PhysicalStoreNameSemantics(
            DeletionDtoChecks.RequiredReference(ProfileId, "profile ID", DeletionDtoChecks.NameProfileOwner),
            encoding,
            DeletionDtoChecks.RequiredValue(CaseSensitive, "case-sensitivity flag", DeletionDtoChecks.NameProfileOwner),
            DeletionDtoChecks.RequiredValue(NormalizationInsensitive, "normalization flag", DeletionDtoChecks.NameProfileOwner));
    }

    internal static PhysicalStoreNameSemanticsDto FromSemantics(PhysicalStoreNameSemantics semantics)
        => new()
        {
            ProfileId = semantics.ProfileId,
            Encoding = (int)semantics.Encoding,
            CaseSensitive = semantics.CaseSensitive,
            NormalizationInsensitive = semantics.NormalizationInsensitive
        };
}
