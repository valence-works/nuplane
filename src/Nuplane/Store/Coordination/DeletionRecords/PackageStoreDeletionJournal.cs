using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.DeletionRecords;

/// <summary>Holds detached phase and identity evidence for one deletion candidate.</summary>
/// <remarks>The journal is never a capability; each invocation must establish current native authority independently.</remarks>
internal sealed class PackageStoreDeletionJournal
{
    internal const int CurrentSchemaVersion = 1;
    internal const string CompletionMarkerName = ".nuplane-ready";

    internal PackageStoreDeletionJournal(
        int schemaVersion,
        PhysicalRootIdentity storeRoot,
        long enrollmentEpoch,
        Guid operationId,
        Guid candidateId,
        PackageInstallIdentity originalInstall,
        IEnumerable<string> packageParentRelativeComponents,
        PhysicalFileIdentity packageParentIdentity,
        PhysicalStoreNameSemantics packageParentSemantics,
        string originalBasename,
        string quarantineBasename,
        PhysicalFileIdentity completionMarkerIdentity,
        int keepNewestInactiveVersionsPerPackage,
        PackageStoreDeletionPhase phase,
        PhysicalStoreDeletionManifest manifest,
        string? expectedIntegrityDigest = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The deletion journal schema version is unsupported.");
        ArgumentNullException.ThrowIfNull(storeRoot);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (operationId == Guid.Empty)
            throw new ArgumentException("A deletion operation ID cannot be empty.", nameof(operationId));
        if (candidateId == Guid.Empty || candidateId == operationId)
            throw new ArgumentException("A candidate ID must be nonempty and distinct from its operation ID.", nameof(candidateId));
        ArgumentNullException.ThrowIfNull(originalInstall);
        ArgumentNullException.ThrowIfNull(packageParentRelativeComponents);
        ArgumentNullException.ThrowIfNull(packageParentIdentity);
        ArgumentNullException.ThrowIfNull(packageParentSemantics);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalBasename);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineBasename);
        ArgumentNullException.ThrowIfNull(completionMarkerIdentity);
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        ArgumentNullException.ThrowIfNull(manifest);

        var parentComponents = CopyParentComponents(packageParentRelativeComponents);
        var copiedRoot = ProtectionRecordValueCopies.CopyRoot(storeRoot);
        var copiedParentIdentity = ProtectionRecordValueCopies.CopyIdentity(packageParentIdentity);
        var copiedParentSemantics = CopySemantics(packageParentSemantics);
        var copiedMarker = ProtectionRecordValueCopies.CopyIdentity(completionMarkerIdentity);
        var copiedInstall = CopyInstallIdentity(originalInstall);

        var installPathComponents = PackageInstallIdentityReader.ValidateRelativeInstallPath(
            copiedInstall.RootRelativeInstallPath,
            copiedInstall.PackageId,
            copiedInstall.Version);
        if (parentComponents.Length != 2 ||
            !parentComponents.SequenceEqual(installPathComponents.Take(2), StringComparer.Ordinal) ||
            !string.Equals(originalBasename, installPathComponents[2], StringComparison.Ordinal))
        {
            throw new ArgumentException("The journal parent locator and original basename must match the install's exact logical path.");
        }

        foreach (var component in parentComponents)
        {
            PhysicalStoreNames.ValidateSingleComponent(component);
            if (component.Length > 255)
                throw new ArgumentException("A package-parent locator component exceeds the UTF-16 code-unit limit.", nameof(packageParentRelativeComponents));
        }

        PhysicalStoreDeletionManifestEntry.ValidateComponent(originalBasename, copiedParentSemantics);
        var expectedQuarantineBasename = QuarantineBasenameFor(candidateId);
        if (!string.Equals(quarantineBasename, expectedQuarantineBasename, StringComparison.Ordinal))
            throw new ArgumentException("The quarantine basename must be the canonical name derived from the candidate ID.", nameof(quarantineBasename));
        PhysicalStoreDeletionManifestEntry.ValidateComponent(quarantineBasename, copiedParentSemantics);

        if (copiedInstall.Root != copiedRoot || copiedInstall.DirectoryIdentity != manifest.InstallDirectoryIdentity)
            throw new ArgumentException("The journal root and install-directory identities must match its original install and manifest.", nameof(manifest));
        if (manifest.Entries.All(static entry =>
                entry.RelativeComponents.Length != 1 ||
                !string.Equals(entry.RelativeComponents[0], CompletionMarkerName, StringComparison.Ordinal) ||
                entry.Kind != PhysicalStoreEntryKind.RegularFile) ||
            manifest.Entries.Count(static entry =>
                entry.RelativeComponents.Length == 1 &&
                string.Equals(entry.RelativeComponents[0], CompletionMarkerName, StringComparison.Ordinal)) != 1)
        {
            throw new ArgumentException("A deletion baseline must contain exactly one root completion-marker regular-file row.", nameof(manifest));
        }

        var markerEntry = manifest.Entries.Single(static entry =>
            entry.RelativeComponents.Length == 1 &&
            string.Equals(entry.RelativeComponents[0], CompletionMarkerName, StringComparison.Ordinal));
        if (markerEntry.Identity != copiedMarker ||
            !string.Equals(copiedInstall.CompletionIdentity,
                ProtectionDigest.PackageInstallCompletionIdentity(copiedMarker),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The journal marker identity and completion digest must match the install baseline.", nameof(completionMarkerIdentity));
        }

        var relatedIdentities = new HashSet<PhysicalFileIdentity>
        {
            copiedRoot.HandleIdentity,
            copiedParentIdentity,
            manifest.InstallDirectoryIdentity
        };
        if (relatedIdentities.Count != 3)
            throw new ArgumentException("The store root, package-ID parent, and install directory must be distinct native objects.");
        foreach (var entry in manifest.Entries)
        {
            if (!relatedIdentities.Add(entry.Identity))
                throw new ArgumentException("Distinct deletion-journal objects must have unique native identities.", nameof(manifest));
        }
        RequireSameVolume(copiedRoot.HandleIdentity, copiedParentIdentity);
        RequireSameVolume(copiedRoot.HandleIdentity, manifest.InstallDirectoryIdentity);

        SchemaVersion = schemaVersion;
        StoreRoot = copiedRoot;
        EnrollmentEpoch = enrollmentEpoch;
        OperationId = operationId;
        CandidateId = candidateId;
        OriginalInstall = copiedInstall;
        PackageParentRelativeComponents = parentComponents;
        PackageParentIdentity = copiedParentIdentity;
        PackageParentSemantics = copiedParentSemantics;
        OriginalBasename = originalBasename;
        QuarantineBasename = quarantineBasename;
        CompletionMarkerIdentity = copiedMarker;
        KeepNewestInactiveVersionsPerPackage = keepNewestInactiveVersionsPerPackage;
        Phase = phase;
        Manifest = manifest;

        if (expectedIntegrityDigest is not null)
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedIntegrityDigest);
            var actual = ProtectionDigest.PackageStoreDeletion(this);
            if (!string.Equals(actual, expectedIntegrityDigest, StringComparison.Ordinal))
                throw new ArgumentException("The deletion journal integrity digest does not match its fields.", nameof(expectedIntegrityDigest));
            IntegrityDigest = expectedIntegrityDigest;
        }
        else
        {
            IntegrityDigest = ProtectionDigest.PackageStoreDeletion(this);
        }
    }

    internal int SchemaVersion { get; }
    internal PhysicalRootIdentity StoreRoot { get; }
    internal long EnrollmentEpoch { get; }
    internal Guid OperationId { get; }
    internal Guid CandidateId { get; }
    internal PackageInstallIdentity OriginalInstall { get; }
    internal ImmutableArray<string> PackageParentRelativeComponents { get; }
    internal PhysicalFileIdentity PackageParentIdentity { get; }
    internal PhysicalStoreNameSemantics PackageParentSemantics { get; }
    internal string OriginalBasename { get; }
    internal string QuarantineBasename { get; }
    internal PhysicalFileIdentity CompletionMarkerIdentity { get; }
    internal int KeepNewestInactiveVersionsPerPackage { get; }
    internal PackageStoreDeletionPhase Phase { get; }
    internal PhysicalStoreDeletionManifest Manifest { get; }
    internal string IntegrityDigest { get; }

    internal PackageStoreDeletionJournal WithPhase(PackageStoreDeletionPhase phase)
        => new(SchemaVersion, StoreRoot, EnrollmentEpoch, OperationId, CandidateId, OriginalInstall,
            PackageParentRelativeComponents, PackageParentIdentity, PackageParentSemantics,
            OriginalBasename, QuarantineBasename, CompletionMarkerIdentity,
            KeepNewestInactiveVersionsPerPackage, phase, Manifest);

    internal static string QuarantineBasenameFor(Guid candidateId)
    {
        if (candidateId == Guid.Empty)
            throw new ArgumentException("A candidate ID cannot be empty.", nameof(candidateId));
        return $".nuplane-quarantine-{candidateId:N}";
    }

    private static ImmutableArray<string> CopyParentComponents(IEnumerable<string> components)
    {
        var builder = ImmutableArray.CreateBuilder<string>(3);
        foreach (var component in components)
        {
            if (builder.Count == 2)
                throw new ArgumentException("A package-parent locator must contain exactly two components.", nameof(components));
            if (component is null)
                throw new ArgumentException("A package-parent locator cannot contain a null component.", nameof(components));
            builder.Add(component);
        }

        if (builder.Count != 2)
            throw new ArgumentException("A package-parent locator must contain exactly two components.", nameof(components));
        return builder.ToImmutable();
    }

    private static PackageInstallIdentity CopyInstallIdentity(PackageInstallIdentity install)
        => new(
            ProtectionRecordValueCopies.CopyRoot(install.Root),
            install.PackageId,
            install.Version,
            install.RootRelativeInstallPath,
            ProtectionRecordValueCopies.CopyIdentity(install.DirectoryIdentity),
            install.CompletionIdentity,
            install.VerifiedArchiveHash);

    private static PhysicalStoreNameSemantics CopySemantics(PhysicalStoreNameSemantics semantics)
        => new(semantics.ProfileId, semantics.Encoding, semantics.CaseSensitive, semantics.NormalizationInsensitive);

    private static void RequireSameVolume(PhysicalFileIdentity root, PhysicalFileIdentity other)
    {
        if (!string.Equals(root.Provider, other.Provider, StringComparison.Ordinal) ||
            !string.Equals(root.VolumeOrDeviceId, other.VolumeOrDeviceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The store root, parent, and install directory must share one native provider and volume.");
        }
    }
}
