using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    internal const string EnrollmentStagePrefix = ".nuplane-enrollment-";

    /// <summary>Publishes a complete Incomplete declaration before binding or writing any member state.</summary>
    /// <remarks>
    /// The operator keeps every user stopped through subsequent verified enrollment completion. This
    /// method cannot discover legacy users and grants no ordinary package access. It never adopts an
    /// existing final namespace, repairs a missing ledger, or removes an orphan stage. An interruption
    /// can leave an inert stage or a valid final Incomplete ledger; explicit recovery must inspect the
    /// actual final entry and complete member set. No machine-power-loss durability is claimed.
    /// </remarks>
    internal RootMembershipRecord InitializeIncomplete(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long enrollmentEpoch,
        IReadOnlyList<RootMemberRecord> declaredMembers,
        bool quiescentCutoverConfirmed,
        CancellationToken cancellationToken,
        Action<RootMembershipEnrollmentPoint>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(declaredMembers);
        cancellationToken.ThrowIfCancellationRequested();
        if (!quiescentCutoverConfirmed)
            throw Refused("Initial enrollment requires explicit confirmation of the fully quiescent cutover.");
        var declarations = declaredMembers.ToArray();
        if (declarations.Length == 0 || declarations.Any(member => member is null || member.Binding is not RootMemberRecord.DeclaredBinding))
            throw Refused("Initial enrollment requires a non-empty complete set of declared members.");

        // Construct and copy the complete declaration before any filesystem mutation.
        var candidate = new RootMembershipRecord(RootMembershipRecord.CurrentSchemaVersion, expectedRoot,
            enrollmentEpoch, RootMembershipStatus.Incomplete, declarations,
            declarations.Select(member => member.MemberId), [], null, PlaceholderDigest);
        var declaration = Rebuild(candidate, RootMembershipStatus.Incomplete, candidate.Members, pending: null);
        var bytes = _ledgerSerializer.Serialize(declaration);
        if (bytes.Length > MaximumStateBytes)
            throw Refused("The initial membership declaration exceeds the bounded control payload limit.");
        var directories = _files as IPhysicalStoreDirectoryPublicationFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "Initial enrollment requires held-parent native directory publication.");
        RequireRoot(root, expectedRoot);
        RequireAbsent(root, ControlDirectoryName);
        cancellationToken.ThrowIfCancellationRequested();

        var stagingName = EnrollmentStagePrefix + Guid.NewGuid().ToString("N");
        PhysicalFileIdentity stagingIdentity;
        using (var staging = _files.CreateDirectoryExclusiveAt(root, stagingName))
        {
            stagingIdentity = _files.InspectHandle(staging).Identity;
            checkpoint?.Invoke(RootMembershipEnrollmentPoint.StagingCreated);
            cancellationToken.ThrowIfCancellationRequested();
            var rootLockIdentity = CreateFile(staging, "root.lock", []);
            var ledgerIdentity = CreateFile(staging, LedgerName, bytes);
            var reopened = _ledgerSerializer.Deserialize(ReadFile(staging, LedgerName, ledgerIdentity, MaximumStateBytes).Bytes);
            if (!string.Equals(reopened.LedgerDigest, declaration.LedgerDigest, StringComparison.Ordinal))
                throw Refused("The prepared initial membership declaration did not verify.");
            if (ReadFile(staging, "root.lock", rootLockIdentity, maximumBytes: 1).Bytes.Length != 0)
                throw Refused("The prepared initial root lock is not empty.");
            PhysicalStorePublicationChecks.RequireParent(_files, staging, stagingIdentity);
            RequireRoot(root, expectedRoot);
            checkpoint?.Invoke(RootMembershipEnrollmentPoint.DeclarationPrepared);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // No staged directory/file/lock handles remain open across the native move.
        RequireRoot(root, expectedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var published = directories.PublishDirectoryNoReplaceAt(root, stagingName, stagingIdentity, ControlDirectoryName);
        PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(published, stagingIdentity);
        checkpoint?.Invoke(RootMembershipEnrollmentPoint.ControlPublished);
        cancellationToken.ThrowIfCancellationRequested();
        using var control = OpenControl(root);
        PhysicalStorePublicationChecks.RequireParent(_files, control, stagingIdentity);
        var actual = ReadLedger(root, control);
        if (!string.Equals(actual.LedgerDigest, declaration.LedgerDigest, StringComparison.Ordinal))
            throw Refused("The published initial membership declaration did not verify.");
        return actual;
    }
}
