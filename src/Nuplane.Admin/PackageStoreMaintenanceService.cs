using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Maintenance;
using CoreExecutionService = Nuplane.Store.Maintenance.PackageStoreExecutionService;

namespace Nuplane.Admin;

/// <summary>Projects the core-owned maintenance kernels onto detached optional Admin contracts.</summary>
internal sealed partial class PackageStoreMaintenanceService(
    PackageStoreInspectionService inspection,
    CoreExecutionService execution,
    IPackageStoreEnrollmentPreviewService enrollmentPreview,
    PackageStoreEnrollmentService enrollment,
    IPackageStoreMaintenanceObservability observability) : INuplanePackageStoreMaintenanceOperations
{
    public async Task<PackageStoreEnrollmentPreviewResult> PreviewEnrollmentAsync(
        string rootLabel = "default", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLabel);
        var operation = observability.Begin("enrollment_preview", rootLabel, null);
        try
        {
            var snapshot = await enrollmentPreview.PreviewRootAsync(rootLabel, cancellationToken).ConfigureAwait(false);
            var result = new PackageStoreEnrollmentPreviewResult(snapshot.RootLabel, snapshot.RootIdentity,
                snapshot.Membership?.ToString(), snapshot.EnrollmentEpoch, snapshot.LedgerDigest,
                snapshot.IsNonQuiescentObservation, snapshot.Members.Select(member => new PackageStoreEnrollmentMemberObservation(
                    member.MemberId, member.BindingKind, member.Observation.ToString(), member.StateBodyDigest, Slot(member.StateSlot), member.StateFileIdentity,
                    member.ActiveVersionById, member.LastKnownGoodById,
                    Closure(member.ActiveClosure), Closure(member.RecoverableClosure), member.Migration.ToString(), member.Diagnostics))
                    .ToArray(), snapshot.Diagnostics, snapshot.CurrentMemberIds);
            observability.RecordEnrollmentPreview(operation, result);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            observability.RecordFailure(operation, exception, cancelled: true);
            throw;
        }
        catch (Exception exception)
        {
            observability.RecordFailure(operation, exception, cancelled: false);
            throw;
        }
    }

    public Task<PackageStoreMaintenanceResult> InspectAsync(
        string rootLabel = "default", CancellationToken cancellationToken = default)
        => PruneAsync(new PackageStorePruneRequest(rootLabel), cancellationToken);

    public async Task<PackageStoreMaintenanceResult> PruneAsync(
        PackageStorePruneRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var operation = observability.Begin(request.Mode == PackageStorePruneMode.Preview ? "preview" : "execute",
            request.RootLabel, request.CorrelationId);
        try
        {
            if (request.Mode == PackageStorePruneMode.Preview)
            {
                var snapshot = await inspection.InspectRootAsync(request.RootLabel,
                    request.Retention?.KeepNewestInactiveVersionsPerPackage, cancellationToken).ConfigureAwait(false);
                var result = Project(request, snapshot, "Preview", snapshot.RetentionPlan?.Entries.Select(row =>
                    new PackageStoreCandidateResult(row.Install, row.Classification.ToString(),
                        row.Reasons.Select(static reason => reason.ToString()).ToArray(), "NotAttempted", null, null,
                        null, null, null)).ToArray() ?? [], []);
                observability.RecordPrune(operation, result);
                return result;
            }

            var confirmation = request.Confirmation!;
            var outcome = await execution.ExecuteEligibleCandidatesAsync(request.RootLabel,
                confirmation.EnrollmentEpoch, request.Retention?.KeepNewestInactiveVersionsPerPackage,
                cancellationToken, confirmation.RootIdentity).ConfigureAwait(false);
            var executionResult = Project(request, outcome.Snapshot, outcome.Status.ToString(), outcome.Candidates.Select(candidate =>
                new PackageStoreCandidateResult(candidate.Install, candidate.Classification.ToString(),
                    candidate.Reasons.Select(static reason => reason.ToString()).ToArray(), candidate.Attempt.ToString(),
                    candidate.PhysicalOutcome?.ToString(), candidate.JournalPhase?.ToString(),
                    candidate.MissingAtStart?.ToArray(), candidate.MissingAtLastObservation?.ToArray(),
                    candidate.Diagnostic is null ? null : Diagnostic(candidate.Diagnostic))).ToArray(),
                outcome.Diagnostic is null ? [] : [Diagnostic(outcome.Diagnostic)]);
            observability.RecordPrune(operation, executionResult);
            return executionResult;
        }
        catch (PackageStoreAdmissionException exception)
        {
            var refusal = new PackageStoreMaintenanceResult(request.RootLabel, request.Mode, "Refused", exception.Root, null, null, "Unknown", false,
                request.Retention, [], [], [], [], [], [Diagnostic(exception)], request.CorrelationId);
            observability.RecordPrune(operation, refusal);
            return refusal;
        }
        catch (OperationCanceledException exception)
        {
            observability.RecordFailure(operation, exception, cancelled: true);
            throw;
        }
        catch (Exception exception)
        {
            observability.RecordFailure(operation, exception, cancelled: false);
            throw;
        }
    }

    private static void Validate(PackageStorePruneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootLabel);
        if (!Enum.IsDefined(request.Mode))
            throw new ArgumentOutOfRangeException(nameof(request), "The maintenance mode is unsupported.");
        if (request.Retention?.KeepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Inactive retention must be nonnegative.");
        if (request.Mode != PackageStorePruneMode.Execute)
            return;

        var confirmation = request.Confirmation;
        if (confirmation is null || confirmation.RootIdentity is null || confirmation.EnrollmentEpoch <= 0 ||
            !string.Equals(confirmation.RootLabel, request.RootLabel, StringComparison.Ordinal) ||
            confirmation.Retention != request.Retention)
            throw new ArgumentException("Execute requires explicit confirmation of the exact label, physical root, epoch and retention policy.", nameof(request));
    }

    private static PackageStoreMaintenanceResult Project(
        PackageStorePruneRequest request,
        PackageStoreInspectionSnapshot snapshot,
        string status,
        IReadOnlyList<PackageStoreCandidateResult> candidates,
        IReadOnlyList<PackageStoreMaintenanceDiagnostic> executionDiagnostics)
        => new(request.RootLabel, request.Mode, status, snapshot.Root, snapshot.EnrollmentEpoch, snapshot.LedgerDigest,
            snapshot.ProtectionKnowledge.ToString(), snapshot.Inventory?.IsComplete ?? false, request.Retention,
            snapshot.Members.Select(static member => new PackageStoreMemberObservation(
                member.MemberId, member.Revision, member.StateBodyDigest, member.ProtectionDigest)).ToArray(),
            snapshot.Inventory?.Entries.Select(static entry => new PackageStoreInventoryObservation(
                entry.RootRelativePath, entry.Kind.ToString(), entry.Reason, entry.InstallIdentity)).ToArray() ?? [],
            snapshot.Inventory?.Issues.Select(static issue => new PackageStoreInventoryUncertainty(
                issue.RootRelativePath, issue.Reason)).ToArray() ?? [],
            snapshot.Uses.Select(static use => new PackageStoreUseObservation(
                use.UseId, use.OwnershipState.ToString(), use.SnapshotState.ToString(), use.Graph)).ToArray(), candidates,
            snapshot.Diagnostics.Select(static diagnostic => new PackageStoreMaintenanceDiagnostic(diagnostic.ToString(), diagnostic.ToString()))
                .Concat(snapshot.RetentionPlan?.Reasons.Select(static reason => new PackageStoreMaintenanceDiagnostic(
                    reason.ToString(), reason.ToString())) ?? [])
                .Concat(executionDiagnostics).ToArray(), request.CorrelationId);

    private static PackageStoreEnrollmentClosureObservation Closure(PackageStoreEnrollmentPreviewClosure closure)
        => new(closure.Knowledge.ToString(), closure.UnknownReason, closure.GraphIds);

    private static PackageStoreStateSlotObservation? Slot(StateSlotIdentity? slot)
        => slot is null ? null : new(slot.ParentIdentity, slot.NameSemantics.ProfileId,
            slot.NameSemantics.Encoding.ToString(), slot.NameSemantics.CaseSensitive,
            slot.NameSemantics.NormalizationInsensitive, slot.CanonicalBasename);

    private static PackageStoreMaintenanceDiagnostic Diagnostic(Exception exception)
        => new(exception is PackageStoreAdmissionException admission ? admission.Reason.ToString() : exception.GetType().Name,
            exception.Message);
}
