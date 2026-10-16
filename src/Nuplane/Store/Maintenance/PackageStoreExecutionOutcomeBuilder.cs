using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;

namespace Nuplane.Store.Maintenance;

internal sealed class PackageStoreExecutionOutcomeBuilder
{
    private readonly Dictionary<PackageStoreRetentionPlanEntry, CandidateBuilder> _candidates =
        new(ReferenceEqualityComparer.Instance);
    private PackageStoreInspectionSnapshot? _snapshot;
    private PackageStoreExecutionRequestStatus? _status;
    private PackageInstallIdentity? _stopCandidate;
    private Exception? _diagnostic;

    internal bool IsInitialized => _snapshot is not null;

    internal bool HasStoppedCandidate => _stopCandidate is not null;

    internal void Initialize(PackageStoreInspectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_snapshot is not null)
            throw new InvalidOperationException("One execution outcome builder can describe only one fresh plan.");
        var plan = snapshot.RetentionPlan
            ?? throw new InvalidOperationException("Execution outcome reporting requires a fresh retention plan.");

        _snapshot = snapshot;
        foreach (var row in plan.Entries)
        {
            var candidate = new CandidateBuilder(row);
            if (row.Classification == PackageStoreRetentionClassification.Retained)
                candidate.PhysicalOutcome = PackageStoreExecutionPhysicalOutcome.Retained;
            _candidates.Add(row, candidate);
        }
    }

    internal void RecordObservation(
        PackageStoreRetentionPlanEntry row,
        PackageStoreDeletionPhase phase,
        PhysicalStoreDeletionPartialObservation observation,
        bool invocationStart = false)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var candidate = Find(row);
        var missing = MissingPaths(observation.PositivelyAbsentEntries);
        if (invocationStart && candidate.MissingAtStart.IsDefault)
            candidate.MissingAtStart = missing;
        candidate.MissingAtLastObservation = missing;
        candidate.JournalPhase = phase;
        candidate.PhysicalOutcome = missing.IsEmpty
            ? PackageStoreExecutionPhysicalOutcome.Quarantined
            : PackageStoreExecutionPhysicalOutcome.PartiallyRemoved;
    }

    internal void RecordDeleted(PackageStoreRetentionPlanEntry row, PhysicalStoreDeletionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var candidate = Find(row);
        candidate.JournalPhase = PackageStoreDeletionPhase.Finalizing;
        candidate.MissingAtLastObservation = MissingPaths(manifest.Entries);
        candidate.PhysicalOutcome = PackageStoreExecutionPhysicalOutcome.Deleted;
    }

    internal void RecordAlreadyAbsent(PackageStoreRetentionPlanEntry row, PackageStoreDeletionPhase phase)
    {
        var candidate = Find(row);
        candidate.JournalPhase = phase;
        candidate.PhysicalOutcome = PackageStoreExecutionPhysicalOutcome.AlreadyAbsent;
    }

    internal void RecordRootAbsentWithoutReceipt(PackageStoreRetentionPlanEntry row)
    {
        var candidate = Find(row);
        candidate.JournalPhase = PackageStoreDeletionPhase.Finalizing;
        candidate.PhysicalOutcome = PackageStoreExecutionPhysicalOutcome.QuarantineRootAbsentUnattributed;
    }

    internal void CompleteCandidate(PackageStoreRetentionPlanEntry row)
        => Find(row).Attempt = PackageStoreExecutionCandidateAttempt.Completed;

    internal void StopCandidate(PackageStoreRetentionPlanEntry row, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var candidate = Find(row);
        candidate.Attempt = AttemptFor(failure);
        candidate.Diagnostic = failure;
        _stopCandidate = row.Install;
        _status = StatusFor(failure);
        _diagnostic = failure;
    }

    internal void StopRequest(Exception failure, bool classifyAdmissionAsFailure = false)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _status = classifyAdmissionAsFailure && failure is PackageStoreAdmissionException
            ? PackageStoreExecutionRequestStatus.Failed
            : StatusFor(failure);
        _diagnostic = failure;
    }

    internal PackageStoreExecutionOutcome Build()
    {
        var snapshot = _snapshot
            ?? throw new InvalidOperationException("Execution did not enter its fresh-plan callback.");
        return new PackageStoreExecutionOutcome(snapshot,
            _status ?? PackageStoreExecutionRequestStatus.Completed,
            snapshot.RetentionPlan!.Entries.Select(row => Find(row).Build()).ToImmutableArray(),
            _stopCandidate,
            _diagnostic);
    }

    private CandidateBuilder Find(PackageStoreRetentionPlanEntry row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _candidates.TryGetValue(row, out var candidate)
            ? candidate
            : throw new InvalidOperationException("The outcome row is not the exact entry in this fresh execution plan.");
    }

    private static PackageStoreExecutionRequestStatus StatusFor(Exception failure)
    {
        if (failure is AggregateException)
            return PackageStoreExecutionRequestStatus.Failed;
        return failure switch
        {
            OperationCanceledException => PackageStoreExecutionRequestStatus.Cancelled,
            PackageStoreAdmissionException => PackageStoreExecutionRequestStatus.Refused,
            _ => PackageStoreExecutionRequestStatus.Failed
        };
    }

    private static PackageStoreExecutionCandidateAttempt AttemptFor(Exception failure)
        => failure switch
        {
            OperationCanceledException => PackageStoreExecutionCandidateAttempt.Cancelled,
            PackageStoreAdmissionException => PackageStoreExecutionCandidateAttempt.Refused,
            _ => PackageStoreExecutionCandidateAttempt.Failed
        };

    private static ImmutableArray<string> MissingPaths(
        IEnumerable<PhysicalStoreDeletionManifestEntry> entries)
        => entries.Select(static entry => string.Join("/", entry.RelativeComponents))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private sealed class CandidateBuilder(PackageStoreRetentionPlanEntry row)
    {
        internal PackageStoreExecutionCandidateAttempt Attempt { get; set; }
        internal PackageStoreExecutionPhysicalOutcome? PhysicalOutcome { get; set; }
        internal PackageStoreDeletionPhase? JournalPhase { get; set; }
        internal ImmutableArray<string> MissingAtStart { get; set; } = default;
        internal ImmutableArray<string> MissingAtLastObservation { get; set; } = default;
        internal Exception? Diagnostic { get; set; }

        internal PackageStoreExecutionCandidateOutcome Build()
            => new(row.Install, row.Classification, row.Reasons, Attempt, PhysicalOutcome, JournalPhase,
                MissingAtStart.IsDefault ? null : MissingAtStart,
                MissingAtLastObservation.IsDefault ? null : MissingAtLastObservation, Diagnostic);
    }
}

internal sealed record PackageStoreExecutionOutcome(
    PackageStoreInspectionSnapshot Snapshot,
    PackageStoreExecutionRequestStatus Status,
    ImmutableArray<PackageStoreExecutionCandidateOutcome> Candidates,
    PackageInstallIdentity? StopCandidate,
    Exception? Diagnostic);

internal sealed record PackageStoreExecutionCandidateOutcome(
    PackageInstallIdentity Install,
    PackageStoreRetentionClassification Classification,
    ImmutableArray<PackageStoreRetentionReason> Reasons,
    PackageStoreExecutionCandidateAttempt Attempt,
    PackageStoreExecutionPhysicalOutcome? PhysicalOutcome,
    PackageStoreDeletionPhase? JournalPhase,
    ImmutableArray<string>? MissingAtStart,
    ImmutableArray<string>? MissingAtLastObservation,
    Exception? Diagnostic);

internal enum PackageStoreExecutionRequestStatus
{
    Completed,
    Refused,
    Failed,
    Cancelled
}

internal enum PackageStoreExecutionCandidateAttempt
{
    NotAttempted,
    Completed,
    Refused,
    Failed,
    Cancelled
}

internal enum PackageStoreExecutionPhysicalOutcome
{
    Retained,
    Quarantined,
    PartiallyRemoved,
    Deleted,
    AlreadyAbsent,
    QuarantineRootAbsentUnattributed
}
