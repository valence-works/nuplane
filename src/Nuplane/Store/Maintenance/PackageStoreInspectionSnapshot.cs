using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Maintenance;

/// <summary>Immutable descriptive observations of one package-store maintenance inspection.</summary>
/// <remarks>
/// An inspection snapshot and its retention plan grant no package access, deletion, or execution authority.
/// Unknown protection is distinct from a known empty set, and an unenrolled root has no fabricated epoch,
/// inventory, or retention plan.
/// </remarks>
internal sealed class PackageStoreInspectionSnapshot
{
    internal PackageStoreInspectionSnapshot(
        PackageStoreAdmissionStatus admissionStatus,
        PhysicalRootIdentity? root,
        long? enrollmentEpoch,
        string? ledgerDigest,
        PackageStoreRetentionProtectionKnowledge protectionKnowledge,
        IEnumerable<PackageStoreInspectionMember> members,
        PackageStoreInventorySnapshot? inventory,
        PackageStoreRetentionPlan? retentionPlan,
        IEnumerable<PackageStoreInspectionUse> uses,
        IEnumerable<PackageStoreInspectionDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(admissionStatus))
            throw new ArgumentOutOfRangeException(nameof(admissionStatus));
        if (!Enum.IsDefined(protectionKnowledge))
            throw new ArgumentOutOfRangeException(nameof(protectionKnowledge));
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(uses);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var memberArray = members.Select(static member =>
                (member ?? throw new ArgumentException("Inspection members cannot contain null entries.", nameof(members))).Copy())
            .OrderBy(static member => member.MemberId, StringComparer.Ordinal)
            .ToArray();
        var useArray = uses.Select(static use =>
                use ?? throw new ArgumentException("Inspection uses cannot contain null entries.", nameof(uses)))
            .OrderBy(static use => use.UseId)
            .ToArray();
        var diagnosticArray = diagnostics.Select(diagnostic =>
                Enum.IsDefined(diagnostic)
                    ? diagnostic
                    : throw new ArgumentOutOfRangeException(nameof(diagnostics)))
            .Distinct()
            .Order()
            .ToArray();
        if (memberArray.Select(static member => member.MemberId).Distinct(StringComparer.Ordinal).Count() != memberArray.Length)
            throw new ArgumentException("Inspection member identifiers must be unique.", nameof(members));
        if (useArray.Select(static use => use.UseId).Distinct().Count() != useArray.Length)
            throw new ArgumentException("Graph-use identifiers must be unique.", nameof(uses));

        if (admissionStatus == PackageStoreAdmissionStatus.Enrolled)
        {
            ArgumentNullException.ThrowIfNull(root);
            if (enrollmentEpoch is null or <= 0)
                throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
            ArgumentException.ThrowIfNullOrWhiteSpace(ledgerDigest);
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(retentionPlan);
            if (protectionKnowledge != retentionPlan.ProtectionKnowledge)
                throw new ArgumentException("The snapshot and retention plan must use the same protection knowledge.", nameof(protectionKnowledge));
            if (inventory.Root != root || inventory.Epoch != enrollmentEpoch.Value ||
                retentionPlan.Root != root || retentionPlan.EnrollmentEpoch != enrollmentEpoch.Value)
                throw new ArgumentException("Inspection inventory and plan must match the captured root and epoch.");
            if (useArray.Any(use => !use.Graph.Roots.Contains(root)))
                throw new ArgumentException("Every inspected graph-use record must include the captured physical root.", nameof(uses));
            if (diagnosticArray.Contains(PackageStoreInspectionDiagnostic.EnrollmentRequired))
                throw new ArgumentException("An enrolled inspection cannot require enrollment.", nameof(diagnostics));
        }
        else
        {
            if (enrollmentEpoch is not null)
                throw new ArgumentException("An unenrolled inspection cannot invent an enrollment epoch.", nameof(enrollmentEpoch));
            if (ledgerDigest is not null)
                throw new ArgumentException("An unenrolled inspection cannot report a membership ledger digest.", nameof(ledgerDigest));
            if (protectionKnowledge != PackageStoreRetentionProtectionKnowledge.Unknown)
                throw new ArgumentException("An unenrolled inspection has unknown protection.", nameof(protectionKnowledge));
            if (memberArray.Length != 0 || inventory is not null || retentionPlan is not null || useArray.Length != 0)
                throw new ArgumentException("An unenrolled inspection cannot fabricate member, inventory, use, or plan evidence.");
            if (!diagnosticArray.Contains(PackageStoreInspectionDiagnostic.EnrollmentRequired))
                throw new ArgumentException("An unenrolled inspection requires an explicit enrollment diagnostic.", nameof(diagnostics));
        }

        AdmissionStatus = admissionStatus;
        Root = root is null ? null : CopyRoot(root);
        EnrollmentEpoch = enrollmentEpoch;
        LedgerDigest = ledgerDigest;
        ProtectionKnowledge = protectionKnowledge;
        Members = new ReadOnlyCollection<PackageStoreInspectionMember>(memberArray);
        Inventory = inventory;
        RetentionPlan = retentionPlan;
        Uses = new ReadOnlyCollection<PackageStoreInspectionUse>(useArray);
        Diagnostics = new ReadOnlyCollection<PackageStoreInspectionDiagnostic>(diagnosticArray);
    }

    internal PackageStoreAdmissionStatus AdmissionStatus { get; }

    internal PhysicalRootIdentity? Root { get; }

    internal long? EnrollmentEpoch { get; }

    internal string? LedgerDigest { get; }

    internal PackageStoreRetentionProtectionKnowledge ProtectionKnowledge { get; }

    internal IReadOnlyList<PackageStoreInspectionMember> Members { get; }

    internal PackageStoreInventorySnapshot? Inventory { get; }

    internal PackageStoreRetentionPlan? RetentionPlan { get; }

    internal IReadOnlyList<PackageStoreInspectionUse> Uses { get; }

    internal IReadOnlyList<PackageStoreInspectionDiagnostic> Diagnostics { get; }

    internal bool HasStaleUses => Uses.Any(static use => use.OwnershipState == GraphUseRecordOwnershipState.Stale);

    private static PhysicalRootIdentity CopyRoot(PhysicalRootIdentity root)
        => new(new PhysicalFileIdentity(root.HandleIdentity.Provider, root.HandleIdentity.VolumeOrDeviceId,
            root.HandleIdentity.FileId));
}

/// <summary>Explains conservative conditions observed during a read-only inspection.</summary>
internal enum PackageStoreInspectionDiagnostic
{
    EnrollmentRequired = 0,
    StaleGraphUseRequiresRecovery = 1
}
