using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Replans under retained full-catalog ownership before invoking internal candidate work.</summary>
/// <remarks>
/// This internal boundary accepts explicit epoch and retention inputs, never a preview as authority.
/// Candidate work must independently bind and revalidate native install evidence before any mutation.
/// Verified Prepared and Quarantined records participate in fresh planning and bounded quarantine recovery.
/// Optional Admin execution delegates to this boundary; native deletion authority remains internal.
/// </remarks>
internal sealed class PackageStoreExecutionService(
    RootMembershipRegistry registry,
    PackageStoreInspectionService inspection,
    Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? journalPublicationCheckpoint = null,
    Action<PackageStoreQuarantineTransitionPoint, PackageStoreDeletionJournal>? quarantineTransitionCheckpoint = null,
    Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? deletionExecutionCheckpoint = null)
{
    private readonly PackageStoreDeletionCandidateBinder _candidateBinder = new(registry.Files);
    private readonly Lazy<PackageStoreDeletionJournalStore> _journalStore =
        new(() => new PackageStoreDeletionJournalStore(registry.Files, journalPublicationCheckpoint));

    internal async Task<TResult> PlanAndRunAsync<TResult>(
        string? rootLabel,
        long expectedEnrollmentEpoch,
        int? keepNewestInactiveVersionsPerPackage,
        Func<Context, CancellationToken, Task<TResult>> candidateWork,
        CancellationToken cancellationToken = default,
        PhysicalRootIdentity? expectedRootIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(candidateWork);
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));

        return await inspection.RunUnderCatalogOwnershipAsync(rootLabel, async (owner, borrow, configuredRoot) =>
        {
            using var operation = borrow.EnterOperation(registry);
            var selectedRoot = owner.ResolveConfiguredRoot(borrow, configuredRoot)
                ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.IncompleteEnrollment,
                    "Execution requires a positively enrolled configured root.");
            if (selectedRoot.EnrollmentEpoch != expectedEnrollmentEpoch)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                    "Execution confirmation does not match the current enrollment epoch.", selectedRoot.RootIdentity);
            if (expectedRootIdentity is not null && selectedRoot.RootIdentity != expectedRootIdentity)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                    "Execution confirmation does not match the configured physical root.", selectedRoot.RootIdentity);

            var journalStore = _journalStore.Value;
            var rootHandle = selectedRoot.PrimaryResolution.AuthorityRoot
                ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Execution requires a retained native root handle.", selectedRoot.RootIdentity);

            // The exact root and epoch confirmation is checked above. Only this confirmed Execute
            // path may reclaim stale use-control artifacts; Preview and Inspect remain descriptive.
            // Verify the complete catalog under the held owner before and after the bounded recovery,
            // then build the deletion plan from a fresh inspection of the resulting namespace.
            borrow.RequireOperation(operation, registry);
            owner.RevalidateRetainedEvidence();
            await registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken)
                .ConfigureAwait(false);
            owner.RevalidateRetainedEvidence();
            await new PackageGraphUseRecordStore(registry.Files)
                .RecoverAsync(rootHandle, selectedRoot.RootIdentity, selectedRoot.Ledger, cancellationToken)
                .ConfigureAwait(false);
            borrow.RequireOperation(operation, registry);
            owner.RevalidateRetainedEvidence();
            await registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken)
                .ConfigureAwait(false);
            owner.RevalidateRetainedEvidence();

            using var executionJoin = new PackageStoreDeletionExecutionJoin(
                journalStore, _candidateBinder, rootHandle, selectedRoot.RootIdentity, expectedEnrollmentEpoch,
                token =>
                {
                    borrow.RequireOperation(operation, registry);
                    owner.RevalidateRetainedEvidence();
                    token.ThrowIfCancellationRequested();
                }, cancellationToken);
            var snapshot = await inspection.InspectUnderCatalogOwnershipAsync(owner, borrow, configuredRoot,
                keepNewestInactiveVersionsPerPackage, cancellationToken, operation,
                executionOnlyInstallOverlay: inventory => executionJoin.BuildOverlay(inventory, cancellationToken)).ConfigureAwait(false);
            var plan = snapshot.RetentionPlan
                ?? throw new InvalidOperationException("An enrolled execution plan is required.");
            if (plan.Root != selectedRoot.RootIdentity || plan.EnrollmentEpoch != expectedEnrollmentEpoch ||
                plan.KeepNewestInactiveVersionsPerPackage != keepNewestInactiveVersionsPerPackage ||
                plan.InventoryStatus != PackageStoreRetentionInventoryStatus.Complete ||
                plan.ProtectionKnowledge != PackageStoreRetentionProtectionKnowledge.Known || plan.HasUnresolvedEvidence)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "Execution requires a fresh complete retention plan with known protection and exact confirmation.",
                    selectedRoot.RootIdentity);

            var context = new Context(registry, borrow, operation, configuredRoot, snapshot,
                _candidateBinder, _journalStore, executionJoin, quarantineTransitionCheckpoint,
                deletionExecutionCheckpoint);
            TResult result;
            try
            {
                context.Revalidate(cancellationToken);
                result = await candidateWork(context, cancellationToken).ConfigureAwait(false);
                context.Revalidate(cancellationToken);
                await registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken)
                    .ConfigureAwait(false);
                context.Revalidate(cancellationToken);
            }
            catch (Exception candidateFailure)
            {
                try
                {
                    context.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Execution candidate work failed and native candidate cleanup also failed.",
                        candidateFailure,
                        cleanupFailure);
                }
                throw;
            }
            context.Dispose();
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PackageStoreExecutionOutcome> ExecuteEligibleCandidatesAsync(
        string? rootLabel,
        long expectedEnrollmentEpoch,
        int? keepNewestInactiveVersionsPerPackage,
        CancellationToken cancellationToken = default,
        PhysicalRootIdentity? expectedRootIdentity = null)
    {
        var outcomes = new PackageStoreExecutionOutcomeBuilder();
        try
        {
            await PlanAndRunAsync(rootLabel, expectedEnrollmentEpoch, keepNewestInactiveVersionsPerPackage,
                (context, token) =>
                {
                    outcomes.Initialize(context.Snapshot);
                    var plan = context.Snapshot.RetentionPlan!;
                    foreach (var row in plan.Entries)
                    {
                        if (row.Classification != PackageStoreRetentionClassification.Eligible)
                            continue;

                        try
                        {
                            context.DeleteEligibleCandidate(row, token, outcomes);
                            outcomes.CompleteCandidate(row);
                        }
                        catch (Exception candidateFailure)
                        {
                            outcomes.StopCandidate(row, candidateFailure);
                            throw;
                        }
                    }

                    return Task.FromResult(true);
                }, cancellationToken, expectedRootIdentity).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Admission failures before the fresh-plan callback remain ordinary exceptions.
            if (!outcomes.IsInitialized)
                throw;
            outcomes.StopRequest(failure, classifyAdmissionAsFailure: !outcomes.HasStoppedCandidate);
        }

        return outcomes.Build();
    }

    /// <summary>Live planning scope; escaped snapshots remain descriptive and native access expires with the operation.</summary>
    internal sealed class Context(
        RootMembershipRegistry registry,
        RootMembershipRegistry.NativeCatalogOwnerBorrow borrow,
        RootMembershipRegistry.NativeCatalogOwnerBorrow.Operation operation,
        TrustedPackageStoreRoot configuredRoot,
        PackageStoreInspectionSnapshot snapshot,
        PackageStoreDeletionCandidateBinder candidateBinder,
        Lazy<PackageStoreDeletionJournalStore> journalStore,
        PackageStoreDeletionExecutionJoin executionJoin,
        Action<PackageStoreQuarantineTransitionPoint, PackageStoreDeletionJournal>? quarantineTransitionCheckpoint,
        Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? deletionExecutionCheckpoint) : IDisposable
    {
        private readonly PackageStoreDeletionCandidateBinder _candidateBinder = candidateBinder;
        private readonly Lazy<PackageStoreDeletionJournalStore> _journalStore = journalStore;
        private readonly PackageStoreDeletionExecutionJoin _executionJoin = executionJoin;
        private readonly IPhysicalStoreFileSystem _files = registry.Files;
        private readonly Action<PackageStoreQuarantineTransitionPoint, PackageStoreDeletionJournal>? _quarantineTransitionCheckpoint = quarantineTransitionCheckpoint;
        private readonly Action<PackageStoreDeletionExecutionPoint, PackageStoreDeletionJournal>? _deletionExecutionCheckpoint = deletionExecutionCheckpoint;
        private readonly object _gate = new();
        private readonly List<PackageStoreDeletionCandidate> _candidates = [];
        private readonly HashSet<PackageStoreRetentionPlanEntry> _boundRows = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PackageStoreDeletionCandidate> _journalAttempts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Guid> _candidateIds = [];
        private readonly Guid _operationId = Guid.NewGuid();
        private bool _disposed;

        internal PackageStoreInspectionSnapshot Snapshot { get; } = snapshot;

        internal PhysicalStoreDirectoryHandle RootHandle
        {
            get
            {
                Revalidate(CancellationToken.None);
                return borrow.Owner.ResolveConfiguredRoot(borrow, configuredRoot)!.PrimaryResolution.AuthorityRoot
                    ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                        "Execution requires a retained native root handle.", Snapshot.Root);
            }
        }

        internal void Revalidate(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context has ended.", Snapshot.Root);
            }
            cancellationToken.ThrowIfCancellationRequested();
            borrow.RequireOperation(operation, registry);
            borrow.Owner.RevalidateRetainedEvidence();
        }

        /// <summary>Binds only the exact Eligible row in this execution's fresh plan to native install evidence.</summary>
        /// <remarks>The returned observation is owned by this context and expires when the operation ends.</remarks>
        internal PackageStoreDeletionCandidate BindEligibleCandidate(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exactRow);
            RequireEligibleRow(exactRow, cancellationToken);
            if (_executionJoin.TryFind(exactRow.Install, out var existing) && existing.QuarantinedCandidate is not null)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "This current logical row is backed by a quarantine; use the quarantine-specific binder.");

            lock (_gate)
            {
                if (_disposed)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context has ended.", Snapshot.Root);
                if (!_boundRows.Add(exactRow))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "One execution context can bind an exact retention row only once.");
            }

            var candidate = _candidateBinder.Bind(exactRow, Snapshot.Root!, RootHandle, Revalidate, cancellationToken);
            lock (_gate)
            {
                if (_disposed)
                {
                    candidate.Dispose();
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context ended during native candidate binding.", Snapshot.Root);
                }
                _candidates.Add(candidate);
            }
            return candidate;
        }

        /// <summary>Binds a journal-joined Eligible logical row to its current exact quarantine tree.</summary>
        internal PackageStoreQuarantinedCandidate BindEligibleQuarantinedCandidate(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exactRow);
            RequireEligibleRow(exactRow, cancellationToken);
            var binding = _executionJoin.RequireQuarantined(exactRow.Install);
            if (binding.Discovered.Journal.Phase is not (PackageStoreDeletionPhase.Prepared or PackageStoreDeletionPhase.Quarantined or
                PackageStoreDeletionPhase.Deleting or PackageStoreDeletionPhase.Finalizing))
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Only a current known deletion phase can bind its verified quarantine.");

            lock (_gate)
            {
                if (_disposed)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context has ended.", Snapshot.Root);
                if (!_boundRows.Add(exactRow))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "One execution context can bind an exact retention row only once.");
            }
            var candidate = binding.QuarantinedCandidate
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The exact journal binding no longer owns its verified quarantine candidate.");
            candidate.Revalidate(cancellationToken);
            return candidate;
        }

        /// <summary>Publishes the first durable Prepared description for one exact candidate.</summary>
        /// <remarks>The receipt proves only the exact native control-file publication; it grants no package mutation authority.</remarks>
        internal PreparedJournalReceipt PublishPreparedJournal(
            PackageStoreDeletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            var receipt = PreparedJournalReceipt.Publish(this, candidate, cancellationToken);
            _executionJoin.RegisterPrepared(candidate, receipt.PublishedEvidence);
            return receipt;
        }

        /// <summary>Moves one exact current Prepared original install to its reserved quarantine name.</summary>
        /// <remarks>The method requires either this scope's exact readback receipt or a verified discovered Prepared journal; it does not recursively delete content.</remarks>
        internal PackageStoreDeletionJournalStore.PublishedJournal QuarantinePreparedCandidate(
            PackageStoreDeletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            RequireOwnedCandidate(candidate, requireUnprepared: true, cancellationToken);
            RequireEligibleRow(candidate.PlanEntry, cancellationToken);
            var binding = _executionJoin.RequireOriginalPrepared(candidate);
            var journal = binding.Discovered.Journal;
            if (candidate.PlanEntry.Classification != PackageStoreRetentionClassification.Eligible ||
                journal.Manifest.RequireExactSubset(candidate.Manifest) != 0 ||
                candidate.Manifest.RequireExactSubset(journal.Manifest) != 0)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The original Prepared journal no longer matches this exact current Eligible row and full baseline.");

            _journalStore.Value.VerifyDiscovered(RootHandle, Snapshot.Root!, binding.Discovered, cancellationToken);
            candidate.RequireCurrentBaseline(cancellationToken);
            var parent = candidate.PrepareForMove(cancellationToken);
            parent.Revalidate(cancellationToken);
            _journalStore.Value.VerifyDiscovered(RootHandle, Snapshot.Root!, binding.Discovered, cancellationToken);
            var publication = _files as IPhysicalStoreDirectoryPublicationFileSystem
                ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "Quarantine transition requires native same-parent no-replace directory publication.", Snapshot.Root);
            var prepared = PhysicalStoreDirectoryPublicationChecks.PreparePublish(_files, publication,
                parent.PackageParentHandle, parent.OriginalBasename, parent.InstallDirectoryIdentity,
                journal.QuarantineBasename);
            if (prepared.ParentIdentity != parent.PackageParentIdentity ||
                prepared.Semantics != parent.PackageParentSemantics)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The original install and quarantine destination do not share the exact journal-bound parent profile.");

            Exception? moveFailure = null;
            try
            {
                _quarantineTransitionCheckpoint?.Invoke(PackageStoreQuarantineTransitionPoint.BeforeDirectoryMove, journal);
                var moved = publication.PublishDirectoryNoReplaceAt(parent.PackageParentHandle,
                    parent.OriginalBasename, parent.InstallDirectoryIdentity, journal.QuarantineBasename);
                if (moved.Kind != PhysicalStoreEntryKind.Directory || moved.Identity != parent.InstallDirectoryIdentity)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The native quarantine publisher returned a different directory identity.");
                _quarantineTransitionCheckpoint?.Invoke(PackageStoreQuarantineTransitionPoint.AfterDirectoryMove, journal);
            }
            catch (Exception exception)
            {
                moveFailure = exception;
            }

            PackageStoreQuarantinedCandidate? quarantined = null;
            try
            {
                var source = _files.InspectChildNoFollow(parent.PackageParentHandle, parent.OriginalBasename);
                var destination = _files.InspectChildNoFollow(parent.PackageParentHandle, journal.QuarantineBasename);
                if (source?.Identity == parent.InstallDirectoryIdentity && destination is null)
                {
                    _candidateBinder.VerifyPreparedOriginal(binding.Discovered, RootHandle, Revalidate, cancellationToken);
                    if (moveFailure is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(moveFailure).Throw();
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The native quarantine publisher returned without moving the exact source directory.");
                }

                if (source is not null || destination?.Identity != parent.InstallDirectoryIdentity)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The quarantine move ended in an ambiguous namespace state; both observed names were preserved.");

                quarantined = _candidateBinder.BindQuarantinedCandidate(binding.Discovered, RootHandle,
                    Revalidate, cancellationToken, parent.PackageParentHandle, parent.PackageParentIdentity,
                    parent.PackageParentSemantics, parent.Revalidate);
                if (quarantined.InspectOriginalSlot(cancellationToken) is not null)
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The original install name was replaced during quarantine publication; phase advancement was refused.");
                _executionJoin.AttachQuarantinedCandidate(binding, quarantined);
                quarantined = null;

                var boundQuarantine = binding.QuarantinedCandidate!;
                var published = _journalStore.Value.PublishQuarantinedPhase(
                    RootHandle, Snapshot.Root!, binding.Discovered,
                    token =>
                    {
                        RequireOwnedCandidate(candidate, requireUnprepared: false, token);
                        parent.Revalidate(token);
                    },
                    token =>
                    {
                        boundQuarantine.Revalidate(token);
                        if (boundQuarantine.InspectOriginalSlot(token) is not null)
                            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                                "The original install slot is no longer positively absent before phase advancement.");
                    },
                    cancellationToken);
                _executionJoin.Advance(binding, published);
                _deletionExecutionCheckpoint?.Invoke(
                    PackageStoreDeletionExecutionPoint.AfterQuarantinedPublished, published.Journal);
                return published;
            }
            catch (Exception transitionFailure)
            {
                if (quarantined is not null)
                {
                    try { quarantined.Dispose(); }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Quarantine transition failed and its native candidate also failed to close.",
                            transitionFailure, cleanupFailure);
                    }
                }
                if (moveFailure is not null && !ReferenceEquals(moveFailure, transitionFailure))
                    throw new AggregateException("Quarantine publication failed and exact moved-state reconciliation also failed.",
                        moveFailure, transitionFailure);
                throw;
            }
        }

        /// <summary>Advances a verified moved-but-still-Prepared quarantine after fresh current eligibility.</summary>
        internal PackageStoreDeletionJournalStore.PublishedJournal AdvancePreparedQuarantine(
            PackageStoreQuarantinedCandidate candidate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            Revalidate(cancellationToken);
            var binding = _executionJoin.RequireQuarantined(candidate.Journal.OriginalInstall);
            var currentRow = FindCurrentRow(candidate.Journal.OriginalInstall);
            if (!ReferenceEquals(binding.QuarantinedCandidate, candidate) ||
                binding.Discovered.Journal.Phase != PackageStoreDeletionPhase.Prepared ||
                currentRow.Classification != PackageStoreRetentionClassification.Eligible ||
                !IsRowBound(currentRow))
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The moved Prepared quarantine is not the exact current Eligible row in this execution.");
            candidate.Revalidate(cancellationToken);
            if (candidate.InspectOriginalSlot(cancellationToken) is not null)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The original install slot is occupied; Prepared quarantine phase advancement is refused.");

            var published = _journalStore.Value.PublishQuarantinedPhase(
                RootHandle, Snapshot.Root!, binding.Discovered,
                token => { Revalidate(token); candidate.Revalidate(token); },
                token =>
                {
                    candidate.Revalidate(token);
                    if (candidate.InspectOriginalSlot(token) is not null)
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The original install slot is no longer positively absent before phase advancement.");
                },
                cancellationToken);
            _executionJoin.Advance(binding, published);
            return published;
        }

        /// <summary>Deletes one exact currently Eligible install through the internal marker-last quarantine protocol.</summary>
        internal PackageStoreDeletionExecutionStatus DeleteEligibleCandidate(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken)
            => DeleteEligibleCandidate(exactRow, cancellationToken, outcomeBuilder: null);

        internal PackageStoreDeletionExecutionStatus DeleteEligibleCandidate(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken,
            PackageStoreExecutionOutcomeBuilder? outcomeBuilder)
        {
            ArgumentNullException.ThrowIfNull(exactRow);
            PackageStoreQuarantinedCandidate? activeQuarantine = null;
            try
            {
                RequireEligibleRow(exactRow, cancellationToken);

                if (_executionJoin.TryFind(exactRow.Install, out var existing))
                {
                    if (existing.QuarantinePositivelyAbsent)
                    {
                        if (existing.Discovered.Journal.Phase != PackageStoreDeletionPhase.Finalizing ||
                            exactRow.Classification != PackageStoreRetentionClassification.Eligible)
                            throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                                "Only an Eligible Finalizing candidate may report a quarantine absent before this invocation.");
                        Revalidate(cancellationToken);
                        _candidateBinder.VerifyFinalizingQuarantineAbsent(existing.Discovered,
                            RootHandle, Revalidate, cancellationToken);
                        outcomeBuilder?.RecordAlreadyAbsent(exactRow, existing.Discovered.Journal.Phase);
                        return PackageStoreDeletionExecutionStatus.AlreadyAbsent;
                    }

                    if (existing.OriginalAtOriginalName)
                    {
                        var original = BindEligibleCandidate(exactRow, cancellationToken);
                        QuarantinePreparedCandidate(original, cancellationToken);
                    }
                    else
                    {
                        var quarantined = BindEligibleQuarantinedCandidate(exactRow, cancellationToken);
                        if (quarantined.Journal.Phase == PackageStoreDeletionPhase.Prepared)
                            AdvancePreparedQuarantine(quarantined, cancellationToken);
                    }
                }
                else
                {
                    var candidate = BindEligibleCandidate(exactRow, cancellationToken);
                    PublishPreparedJournal(candidate, cancellationToken);
                    QuarantinePreparedCandidate(candidate, cancellationToken);
                }

                var binding = _executionJoin.RequireQuarantined(exactRow.Install);
                var quarantine = binding.QuarantinedCandidate
                    ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A current eligible deletion candidate has no exact quarantine observation.");
                activeQuarantine = quarantine;
                if (outcomeBuilder is not null)
                {
                    var initialObservation = quarantine.ObserveCurrentSubset(cancellationToken);
                    outcomeBuilder.RecordObservation(exactRow, quarantine.Journal.Phase,
                        initialObservation, invocationStart: true);
                }

                new PhysicalStoreDeletionExecutor(_files, _deletionExecutionCheckpoint).Execute(quarantine,
                    PublishDeletingPhase, PublishFinalizingPhase,
                    token => RequireBoundEligibleJournal(binding, quarantine, token),
                    cancellationToken);
                outcomeBuilder?.RecordDeleted(exactRow, quarantine.Manifest);
                return PackageStoreDeletionExecutionStatus.Deleted;
            }
            catch (Exception candidateFailure)
            {
                if (activeQuarantine is null &&
                    candidateFailure is not PackageStoreAdmissionException and not AggregateException)
                {
                    try
                    {
                        if (_executionJoin.TryFind(exactRow.Install, out var binding))
                            activeQuarantine = binding.QuarantinedCandidate;
                    }
                    catch
                    {
                        // Preserve the candidate failure; a refused/expired join must not trigger a new read.
                    }
                }

                if (outcomeBuilder is not null && activeQuarantine is not null)
                {
                    if (activeQuarantine.LastObservation is { } lastObservation)
                        outcomeBuilder.RecordObservation(exactRow,
                            activeQuarantine.LastObservationPhase ?? activeQuarantine.Journal.Phase, lastObservation);
                    if (candidateFailure is not PackageStoreAdmissionException and not AggregateException)
                        RecordFailureObservation(activeQuarantine, exactRow, outcomeBuilder);
                }
                throw;
            }
        }

        private void RecordFailureObservation(
            PackageStoreQuarantinedCandidate candidate,
            PackageStoreRetentionPlanEntry row,
            PackageStoreExecutionOutcomeBuilder outcomeBuilder)
        {
            try
            {
                // This bounded recovery read is permitted only while the same plan operation and owner are live.
                Revalidate(CancellationToken.None);
                if (candidate.QuarantineRootRemovalAttempted)
                {
                    if (candidate.LastObservation is { } last)
                        outcomeBuilder.RecordObservation(row,
                            candidate.LastObservationPhase ?? candidate.Journal.Phase, last);
                    candidate.VerifyQuarantineRemoved(CancellationToken.None);
                    if (candidate.QuarantineRootRemovalReturned)
                        outcomeBuilder.RecordDeleted(row, candidate.Manifest);
                    else
                        outcomeBuilder.RecordRootAbsentWithoutReceipt(row);
                    return;
                }

                var afterFailure = candidate.ObserveCurrentSubset(CancellationToken.None);
                outcomeBuilder.RecordObservation(row,
                    candidate.LastObservationPhase ?? candidate.Journal.Phase, afterFailure);
            }
            catch
            {
                // Preserve the primary refusal/failure and the last verified facts. No retry or owner bypass.
            }
        }

        private void PublishDeletingPhase(
            PackageStoreQuarantinedCandidate candidate,
            CancellationToken cancellationToken)
        {
            var binding = _executionJoin.RequireQuarantined(candidate.Journal.OriginalInstall);
            RequireSameBoundCandidate(binding, candidate, PackageStoreDeletionPhase.Quarantined, cancellationToken);
            var observation = candidate.ObserveCurrentSubset(cancellationToken);
            if (!observation.PositivelyAbsentEntries.IsEmpty)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The Quarantined phase requires its complete baseline before Deleting publication.");
            var published = _journalStore.Value.PublishDeletingPhase(
                RootHandle, Snapshot.Root!, binding.Discovered,
                token => RequireSameBoundCandidate(binding, candidate, PackageStoreDeletionPhase.Quarantined, token),
                token =>
                {
                    candidate.Revalidate(token);
                    if (candidate.InspectOriginalSlot(token) is not null)
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The original logical install slot is occupied before Deleting publication.");
                }, cancellationToken);
            _executionJoin.Advance(binding, published);
        }

        private void PublishFinalizingPhase(
            PackageStoreQuarantinedCandidate candidate,
            CancellationToken cancellationToken)
        {
            var binding = _executionJoin.RequireQuarantined(candidate.Journal.OriginalInstall);
            RequireSameBoundCandidate(binding, candidate, PackageStoreDeletionPhase.Deleting, cancellationToken);
            var observation = candidate.ObserveCurrentSubset(cancellationToken);
            if (observation.Manifest.Entries.Length != 1 ||
                observation.Manifest.Entries[0].RelativeComponents.Length != 1 ||
                !string.Equals(observation.Manifest.Entries[0].RelativeComponents[0],
                    PackageStoreDeletionJournal.CompletionMarkerName, StringComparison.Ordinal))
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Finalizing publication requires a complete native observation of the exact marker as the sole child.");
            var published = _journalStore.Value.PublishFinalizingPhase(
                RootHandle, Snapshot.Root!, binding.Discovered,
                token => RequireSameBoundCandidate(binding, candidate, PackageStoreDeletionPhase.Deleting, token),
                token =>
                {
                    candidate.Revalidate(token);
                    if (candidate.InspectOriginalSlot(token) is not null)
                        throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                            "The original logical install slot is occupied before Finalizing publication.");
                }, cancellationToken);
            _executionJoin.Advance(binding, published);
        }

        private void RequireBoundEligibleRow(PackageInstallIdentity install, CancellationToken cancellationToken)
        {
            var row = FindCurrentRow(install);
            RequireEligibleRow(row, cancellationToken);
            if (!IsRowBound(row))
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Deletion requires the exact current Eligible row to remain bound in this operation.");
        }

        private void RequireSameBoundCandidate(
            PackageStoreDeletionExecutionJoin.JournalBinding binding,
            PackageStoreQuarantinedCandidate candidate,
            PackageStoreDeletionPhase phase,
            CancellationToken cancellationToken)
        {
            RequireBoundEligibleRow(candidate.Journal.OriginalInstall, cancellationToken);
            Revalidate(cancellationToken);
            if (!ReferenceEquals(binding.QuarantinedCandidate, candidate) || binding.Discovered.Journal.Phase != phase ||
                candidate.Journal.Phase != phase)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Deletion phase advancement requires the exact bound candidate at the expected current phase.");
            _journalStore.Value.VerifyDiscovered(RootHandle, Snapshot.Root!, binding.Discovered, cancellationToken);
            candidate.Revalidate(cancellationToken);
            if (candidate.InspectOriginalSlot(cancellationToken) is not null)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The original logical install slot must remain positively absent during deletion.");
        }

        private void RequireBoundEligibleJournal(
            PackageStoreDeletionExecutionJoin.JournalBinding binding,
            PackageStoreQuarantinedCandidate candidate,
            CancellationToken cancellationToken)
        {
            RequireBoundEligibleRow(candidate.Journal.OriginalInstall, cancellationToken);
            var phase = candidate.Journal.Phase;
            if (!ReferenceEquals(binding.QuarantinedCandidate, candidate) ||
                binding.Discovered.Journal.Phase != phase)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Native removal requires the exact current journal-bound candidate at its discovered phase.");
            _journalStore.Value.VerifyDiscovered(RootHandle, Snapshot.Root!, binding.Discovered, cancellationToken);
        }

        private PackageStoreRetentionPlan RequireEligibleRow(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken)
        {
            Revalidate(cancellationToken);
            var plan = Snapshot.RetentionPlan
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires the current execution retention plan.");
            if (Snapshot.Root is null || plan.Root != Snapshot.Root ||
                Snapshot.EnrollmentEpoch != plan.EnrollmentEpoch ||
                Snapshot.AdmissionStatus != PackageStoreAdmissionStatus.Enrolled ||
                plan.InventoryStatus != PackageStoreRetentionInventoryStatus.Complete ||
                plan.ProtectionKnowledge != PackageStoreRetentionProtectionKnowledge.Known ||
                plan.HasUnresolvedEvidence)
            {
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires a complete, known, unresolved-free plan for the enrolled root.");
            }

            if (plan.Entries.Count(entry => ReferenceEquals(entry, exactRow)) != 1)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires the exact row object from this execution's current plan.");
            if (exactRow.Classification != PackageStoreRetentionClassification.Eligible)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Only a currently Eligible plan row can be bound to native install evidence.");
            return plan;
        }

        private PackageStoreRetentionPlanEntry FindCurrentRow(PackageInstallIdentity install)
        {
            var matches = Snapshot.RetentionPlan?.Entries.Where(entry => entry.Install == install).ToArray() ?? [];
            if (matches.Length != 1)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The journal-bound logical install does not have one exact row in this current plan.");
            return matches[0];
        }

        private bool IsRowBound(PackageStoreRetentionPlanEntry row)
        {
            lock (_gate)
                return _boundRows.Contains(row);
        }

        private PackageStoreDeletionJournalStore.PublishedJournal PublishPreparedJournalCore(
            PackageStoreDeletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            RequireOwnedCandidate(candidate, requireUnprepared: true, cancellationToken);

            Guid candidateId;
            lock (_gate)
            {
                if (!_journalAttempts.Add(candidate))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "A deletion candidate can attempt Prepared publication only once in one execution context.");
                do candidateId = Guid.NewGuid();
                while (candidateId == Guid.Empty || candidateId == _operationId || !_candidateIds.Add(candidateId));
            }

            var plan = Snapshot.RetentionPlan!;
            var enrollmentEpoch = plan.EnrollmentEpoch;
            var keepNewest = plan.KeepNewestInactiveVersionsPerPackage
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Prepared publication requires the current plan's retention policy.");
            var journal = candidate.CreatePreparedJournal(Snapshot.Root!, enrollmentEpoch,
                _operationId, candidateId, keepNewest, cancellationToken);
            var root = RootHandle;
            var journalStore = _journalStore.Value;
            var result = journalStore.PublishPrepared(root, Snapshot.Root!, journal,
                token => RequireOwnedCandidate(candidate, requireUnprepared: true, token),
                token => candidate.RequireCurrentBaseline(token), cancellationToken);

            RequireOwnedCandidate(candidate, requireUnprepared: false, cancellationToken);
            return result;
        }

        private void RequireOwnedCandidate(
            PackageStoreDeletionCandidate candidate,
            bool requireUnprepared,
            CancellationToken cancellationToken)
        {
            Revalidate(cancellationToken);
            lock (_gate)
            {
                if (!_candidates.Any(item => ReferenceEquals(item, candidate)))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "The deletion candidate is not owned by this exact execution context.");
            }
            candidate.RequireJournalScopeLive(cancellationToken);
            if (requireUnprepared)
                candidate.RequireUnprepared(cancellationToken);
        }

        private void RequireReceiptLive(
            PackageStoreDeletionCandidate candidate,
            CancellationToken cancellationToken)
        {
            RequireOwnedCandidate(candidate, requireUnprepared: false, cancellationToken);
            candidate.Revalidate(cancellationToken);
        }

        /// <summary>Opaque readback evidence minted only after exact canonical bytes and stage absence were reopened.</summary>
        internal sealed class PreparedJournalReceipt
        {
            private readonly Context _scope;
            private readonly PackageStoreDeletionCandidate _candidate;
            private readonly PackageStoreDeletionJournalStore _store;
            private readonly PhysicalStoreDirectoryHandle _root;
            private readonly PhysicalRootIdentity _expectedRoot;
            private readonly PackageStoreDeletionJournalStore.PublishedJournal _published;

            private PreparedJournalReceipt(Context scope, PackageStoreDeletionCandidate candidate,
                PackageStoreDeletionJournalStore store, PhysicalStoreDirectoryHandle root,
                PhysicalRootIdentity expectedRoot, PackageStoreDeletionJournalStore.PublishedJournal published,
                CancellationToken cancellationToken)
            {
                _scope = scope;
                _candidate = candidate;
                _store = store;
                _root = root;
                _expectedRoot = expectedRoot;
                _published = published;
                Revalidate(cancellationToken);
            }

            internal static PreparedJournalReceipt Publish(
                Context scope,
                PackageStoreDeletionCandidate candidate,
                CancellationToken cancellationToken)
            {
                var published = scope.PublishPreparedJournalCore(candidate, cancellationToken);
                return new PreparedJournalReceipt(scope, candidate, scope._journalStore.Value,
                    scope.RootHandle, scope.Snapshot.Root!, published, cancellationToken);
            }

            internal PackageStoreDeletionJournal Journal
            {
                get
                {
                    EnsureLive(CancellationToken.None);
                    return _published.Journal;
                }
            }

            internal PhysicalFileIdentity JournalFileIdentity
            {
                get
                {
                    EnsureLive(CancellationToken.None);
                    return _published.FileIdentity;
                }
            }

            internal PackageStoreDeletionJournalStore.PublishedJournal PublishedEvidence
            {
                get
                {
                    EnsureLive(CancellationToken.None);
                    return _published;
                }
            }

            internal string CanonicalBasename
            {
                get
                {
                    EnsureLive(CancellationToken.None);
                    return _published.CanonicalName;
                }
            }

            internal void Revalidate(CancellationToken cancellationToken)
            {
                EnsureLive(cancellationToken);
                _store.VerifyPublished(_root, _expectedRoot, _published, cancellationToken);
            }

            private void EnsureLive(CancellationToken cancellationToken)
            {
                _scope.RequireReceiptLive(_candidate, cancellationToken);
            }
        }

        public void Dispose()
        {
            PackageStoreDeletionCandidate[] candidates;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                candidates = _candidates.ToArray();
                _candidates.Clear();
            }

            List<Exception>? failures = null;
            for (var index = candidates.Length - 1; index >= 0; index--)
            {
                try
                {
                    candidates[index].Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                _executionJoin.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException("Native deletion observations failed to close.", failures);
        }

        private static PackageStoreAdmissionException Refuse(PackageStoreAdmissionReason reason, string message)
            => new(reason, message);
    }
}

internal enum PackageStoreQuarantineTransitionPoint
{
    BeforeDirectoryMove,
    AfterDirectoryMove
}

internal enum PackageStoreDeletionExecutionStatus
{
    Deleted,
    AlreadyAbsent
}
