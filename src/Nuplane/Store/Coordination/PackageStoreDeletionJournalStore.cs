using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.DeletionSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Discovers bounded deletion journals and publishes their exact native control-file transitions.</summary>
/// <remarks>
/// The journal is descriptive data. This store accepts only a live execution caller's callbacks for fresh
/// candidate validation and returns readback evidence only after reopening the exact canonical bytes.
/// It never moves or removes package content and does not recover pre-existing stages.
/// </remarks>
internal sealed class PackageStoreDeletionJournalStore
{
    internal const int MaximumControlEntries = 4096;
    internal const int MaximumPruneArtifacts = 1024;
    internal const long MaximumAggregatePruneBytes = 64L * 1024 * 1024;

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
    private readonly IPhysicalStorePublicationFileSystem? _publication;
    private readonly PackageStoreDeletionJournalSerializer _serializer = new();
    private readonly Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? _checkpoint;

    internal PackageStoreDeletionJournalStore(
        IPhysicalStoreFileSystem files,
        Action<PackageStoreDeletionJournalPublicationPoint, PackageStoreDeletionJournal>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("Deletion journal publication requires native file-name observations.");
        _directories = files as IPhysicalStoreDirectoryNameFileSystem
            ?? throw Unsupported("Deletion journal publication requires native directory-name observations.");
        _enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw Unsupported("Deletion journal publication requires bounded native enumeration.");
        _publication = files as IPhysicalStorePublicationFileSystem;
        _checkpoint = checkpoint;
    }

    internal IReadOnlyList<DiscoveredJournal> Discover(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        long expectedEnrollmentEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));
        cancellationToken.ThrowIfCancellationRequested();

        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);
        var names = _enumeration.EnumerateChildNamesNoFollow(control, MaximumControlEntries);
        var artifacts = InspectPruneArtifacts(control, controlIdentity, controlSemantics, names, expectedRoot);
        if (artifacts.Any(static artifact => artifact.IsStage))
            throw Refuse("A reserved prune publication stage is unresolved; it is preserved for explicit recovery.", expectedRoot);

        var journals = new List<DiscoveredJournal>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadExactFile(control, controlIdentity, controlSemantics, artifact.Name,
                artifact.Info.Identity, expectedRoot.HandleIdentity,
                PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
            PackageStoreDeletionJournal journal;
            try { journal = _serializer.Deserialize(payload); }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
            {
                throw Refuse("A reserved canonical prune journal is malformed and was preserved.", expectedRoot, exception);
            }

            if (journal.CandidateId != artifact.CandidateId || journal.StoreRoot != expectedRoot ||
                journal.EnrollmentEpoch != expectedEnrollmentEpoch ||
                !IsKnownPhase(journal.Phase))
            {
                throw Refuse("A reserved canonical prune journal is not a supported current-root/current-epoch record.", expectedRoot);
            }

            journals.Add(new DiscoveredJournal(journal, artifact.Name, artifact.Info.Identity, payload,
                rootSemantics, controlIdentity, controlSemantics));
        }

        ValidateJournalSet(journals, expectedRoot);
        return journals;
    }

    internal void VerifyDiscovered(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        DiscoveredJournal observed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observed);
        cancellationToken.ThrowIfCancellationRequested();
        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        if (rootSemantics != observed.RootSemantics)
            throw Refuse("The held store root profile changed after prune-journal discovery.", expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);
        if (controlIdentity != observed.ControlDirectoryIdentity || controlSemantics != observed.ControlDirectorySemantics)
            throw Refuse("The reserved control directory changed after prune-journal discovery.", expectedRoot);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, observed.CanonicalName,
            observed.FileIdentity, expectedRoot.HandleIdentity, observed.Payload.Length);
        var current = ReadExactFile(control, controlIdentity, controlSemantics, observed.CanonicalName,
            observed.FileIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        if (!current.AsSpan().SequenceEqual(observed.Payload))
            throw Refuse("A discovered prune journal's exact native bytes changed during the execution scope.", expectedRoot);
    }

    internal PublishedJournal PublishPrepared(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PackageStoreDeletionJournal journal,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentBaseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(requireCurrentCandidate);
        ArgumentNullException.ThrowIfNull(requireCurrentBaseline);
        var publication = RequirePublication();
        if (journal.Phase != PackageStoreDeletionPhase.Prepared || journal.StoreRoot != expectedRoot)
            throw Refuse("Prepared publication requires a journal for the exact held root.", expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentBaseline(cancellationToken);
        var payload = _serializer.Serialize(journal);
        if (payload.Length > PackageStoreDeletionJournalSerializer.MaximumPayloadBytes)
            throw Refuse("The Prepared journal exceeds the per-record payload limit.", expectedRoot);

        var canonicalName = CanonicalName(journal.CandidateId);
        var publicationId = NewDistinctId(journal.OperationId, journal.CandidateId);
        var stagedName = StagedName(journal.CandidateId, publicationId);

        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);

        var existingNames = _enumeration.EnumerateChildNamesNoFollow(control, MaximumControlEntries);
        if (existingNames.Count + 1 > MaximumControlEntries)
            throw Refuse("Publishing the stage would exceed the complete control-directory enumeration limit.", expectedRoot);

        var artifacts = InspectPruneArtifacts(control, controlIdentity, controlSemantics,
            existingNames, expectedRoot);
        if (artifacts.Count + 1 > MaximumPruneArtifacts)
            throw Refuse("Publishing the stage would exceed the reserved prune-artifact count limit.", expectedRoot);
        var aggregateBytes = artifacts.Aggregate(0L, static (sum, item) => checked(sum + item.Info.Length));
        if (checked(aggregateBytes + payload.Length) > MaximumAggregatePruneBytes)
            throw Refuse("Publishing the stage would exceed the aggregate prune-record byte limit.", expectedRoot);

        if (artifacts.Any(static item => item.IsStage))
            throw Refuse("An existing prune publication stage is unresolved; it is preserved for explicit recovery.", expectedRoot);

        ValidateExistingJournals(control, controlIdentity, controlSemantics, artifacts, expectedRoot, journal);
        if (existingNames.Contains(canonicalName, StringComparer.Ordinal) ||
            existingNames.Contains(stagedName, StringComparer.Ordinal))
        {
            throw Refuse("The candidate's canonical journal or exact publication stage already exists.", expectedRoot);
        }

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentBaseline(cancellationToken);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);

        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, canonicalName), expectedIdentity: null);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, stagedName), expectedIdentity: null);

        var stagedIdentity = CreateAndVerifyStage(control, controlIdentity, controlSemantics, stagedName,
            payload, journal, expectedRoot);
        return PublishStagedRecord(heldRoot, expectedRoot, rootSemantics, control, controlIdentity,
            controlSemantics, canonicalName, stagedName, stagedIdentity, payload, journal,
            cancellationToken,
            expectedDestinationIdentity: null,
            publication,
            requireCurrent: token =>
            {
                requireCurrentCandidate(token);
                requireCurrentBaseline(token);
            },
            verifyPublished: () => VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics,
                control, controlIdentity, controlSemantics, canonicalName, stagedName,
                stagedIdentity, payload, journal),
            verifyUnpublished: () => VerifyUnpublishedStageInControl(heldRoot, expectedRoot,
                rootSemantics, control, controlIdentity, controlSemantics, canonicalName,
                stagedName, stagedIdentity, payload, journal),
            beforePublishPoint: PackageStoreDeletionJournalPublicationPoint.BeforeNativePublish,
            afterPublishPoint: PackageStoreDeletionJournalPublicationPoint.AfterNativePublish,
            refusalMessage: "Prepared publication ended in an unknown native state; all observed artifacts were preserved.");
    }

    internal PublishedJournal PublishQuarantinedPhase(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        DiscoveredJournal prior,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentQuarantine,
        CancellationToken cancellationToken)
        => PublishPhase(heldRoot, expectedRoot, prior, PackageStoreDeletionPhase.Prepared,
            PackageStoreDeletionPhase.Quarantined, requireCurrentCandidate, requireCurrentQuarantine,
            PackageStoreDeletionJournalPublicationPoint.BeforeQuarantinedPhasePublish,
            PackageStoreDeletionJournalPublicationPoint.AfterQuarantinedPhasePublish, cancellationToken);

    internal PublishedJournal PublishDeletingPhase(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        DiscoveredJournal prior,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentQuarantine,
        CancellationToken cancellationToken)
        => PublishPhase(heldRoot, expectedRoot, prior, PackageStoreDeletionPhase.Quarantined,
            PackageStoreDeletionPhase.Deleting, requireCurrentCandidate, requireCurrentQuarantine,
            PackageStoreDeletionJournalPublicationPoint.BeforeDeletingPhasePublish,
            PackageStoreDeletionJournalPublicationPoint.AfterDeletingPhasePublish, cancellationToken);

    internal PublishedJournal PublishFinalizingPhase(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        DiscoveredJournal prior,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentQuarantine,
        CancellationToken cancellationToken)
        => PublishPhase(heldRoot, expectedRoot, prior, PackageStoreDeletionPhase.Deleting,
            PackageStoreDeletionPhase.Finalizing, requireCurrentCandidate, requireCurrentQuarantine,
            PackageStoreDeletionJournalPublicationPoint.BeforeFinalizingPhasePublish,
            PackageStoreDeletionJournalPublicationPoint.AfterFinalizingPhasePublish, cancellationToken);

    private PublishedJournal PublishPhase(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        DiscoveredJournal prior,
        PackageStoreDeletionPhase expectedPhase,
        PackageStoreDeletionPhase nextPhase,
        Action<CancellationToken> requireCurrentCandidate,
        Action<CancellationToken> requireCurrentQuarantine,
        PackageStoreDeletionJournalPublicationPoint beforePublishPoint,
        PackageStoreDeletionJournalPublicationPoint afterPublishPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(requireCurrentCandidate);
        ArgumentNullException.ThrowIfNull(requireCurrentQuarantine);
        var publication = RequirePublication();
        if (prior.Journal.Phase != expectedPhase || prior.Journal.StoreRoot != expectedRoot)
            throw Refuse($"Only the exact {expectedPhase} journal for the held root can advance to {nextPhase}.", expectedRoot);

        var updated = prior.Journal.WithPhase(nextPhase);
        var payload = _serializer.Serialize(updated);
        if (payload.Length > PackageStoreDeletionJournalSerializer.MaximumPayloadBytes)
            throw Refuse($"The {nextPhase} journal exceeds the per-record payload limit.", expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentQuarantine(cancellationToken);
        VerifyDiscovered(heldRoot, expectedRoot, prior, cancellationToken);
        var currentSet = Discover(heldRoot, expectedRoot, prior.Journal.EnrollmentEpoch, cancellationToken);
        if (currentSet.Count(item => item.Journal.CandidateId == prior.Journal.CandidateId) != 1)
            throw Refuse($"The exact {expectedPhase} journal is no longer uniquely discoverable.", expectedRoot);

        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        if (rootSemantics != prior.RootSemantics)
            throw Refuse("The held store root profile changed before phase replacement.", expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);
        if (controlIdentity != prior.ControlDirectoryIdentity || controlSemantics != prior.ControlDirectorySemantics)
            throw Refuse("The reserved control directory changed before phase replacement.", expectedRoot);

        var names = _enumeration.EnumerateChildNamesNoFollow(control, MaximumControlEntries);
        if (names.Count + 1 > MaximumControlEntries)
            throw Refuse("Publishing the phase stage would exceed the complete control-directory enumeration limit.", expectedRoot);
        var artifacts = InspectPruneArtifacts(control, controlIdentity, controlSemantics, names, expectedRoot);
        if (artifacts.Any(static artifact => artifact.IsStage))
            throw Refuse("A reserved prune stage appeared before phase replacement; it is preserved.", expectedRoot);
        if (artifacts.Count + 1 > MaximumPruneArtifacts)
            throw Refuse("Publishing the phase stage would exceed the reserved prune-artifact count limit.", expectedRoot);
        var aggregateBytes = artifacts.Aggregate(0L, static (sum, item) => checked(sum + item.Info.Length));
        if (checked(aggregateBytes + payload.Length) > MaximumAggregatePruneBytes)
            throw Refuse("Publishing the phase stage would exceed the aggregate prune-record byte limit.", expectedRoot);

        var canonicalName = prior.CanonicalName;
        var publicationId = NewDistinctId(prior.Journal.OperationId, prior.Journal.CandidateId);
        var stagedName = StagedName(prior.Journal.CandidateId, publicationId);
        if (!names.Contains(canonicalName, StringComparer.Ordinal) || names.Contains(stagedName, StringComparer.Ordinal))
            throw Refuse("The exact prior journal name is absent or the new stage name is already occupied.", expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        requireCurrentCandidate(cancellationToken);
        requireCurrentQuarantine(cancellationToken);
        VerifyDiscovered(heldRoot, expectedRoot, prior, cancellationToken);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, stagedName), expectedIdentity: null);

        var stagedIdentity = CreateAndVerifyStage(control, controlIdentity, controlSemantics, stagedName,
            payload, updated, expectedRoot);
        return PublishStagedRecord(heldRoot, expectedRoot, rootSemantics, control, controlIdentity,
            controlSemantics, canonicalName, stagedName, stagedIdentity, payload, updated,
            cancellationToken,
            prior.FileIdentity,
            publication,
            requireCurrent: token =>
            {
                requireCurrentCandidate(token);
                requireCurrentQuarantine(token);
                VerifyDiscovered(heldRoot, expectedRoot, prior, token);
            },
            verifyPublished: () => VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics,
                control, controlIdentity, controlSemantics, canonicalName, stagedName,
                stagedIdentity, payload, updated),
            verifyUnpublished: () => VerifyUnpublishedUpdateInControl(heldRoot, expectedRoot,
                rootSemantics, control, controlIdentity, controlSemantics, prior, stagedName,
                stagedIdentity, payload, updated),
            beforePublishPoint,
            afterPublishPoint,
            refusalMessage: $"{nextPhase} phase replacement ended in an unknown native state; all artifacts were preserved.");
    }

    internal void VerifyPublished(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PublishedJournal published,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);
        cancellationToken.ThrowIfCancellationRequested();
        var rootSemantics = VerifyRoot(heldRoot, expectedRoot);
        if (rootSemantics != published.RootSemantics)
            throw Refuse("The held store root's native name profile changed after Prepared publication.", expectedRoot);
        using var control = OpenAndVerifyControl(heldRoot, expectedRoot, rootSemantics,
            out var controlIdentity, out var controlSemantics);
        if (controlIdentity != published.ControlDirectoryIdentity || controlSemantics != published.ControlDirectorySemantics)
            throw Refuse("The reserved control directory identity or native profile changed after Prepared publication.", expectedRoot);
        VerifyPublishedInControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity,
            controlSemantics, published.CanonicalName, published.StagedName,
            published.FileIdentity, published.Payload, published.Journal);
    }

    private PhysicalFileIdentity CreateAndVerifyStage(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string stagedName,
        byte[] payload,
        PackageStoreDeletionJournal journal,
        PhysicalRootIdentity expectedRoot)
    {
        var stagedIdentity = CreateFlushedStage(control, stagedName, payload, expectedRoot.HandleIdentity);
        _checkpoint?.Invoke(PackageStoreDeletionJournalPublicationPoint.StageFlushed, journal);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, stagedName, stagedIdentity,
            expectedRoot.HandleIdentity, payload.Length);
        var stagedBytes = ReadExactFile(control, controlIdentity, controlSemantics, stagedName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(stagedBytes, payload, journal, expectedRoot);
        return stagedIdentity;
    }

    private PublishedJournal PublishStagedRecord(
        PhysicalStoreDirectoryHandle heldRoot,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string canonicalName,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal journal,
        CancellationToken cancellationToken,
        PhysicalFileIdentity? expectedDestinationIdentity,
        IPhysicalStorePublicationFileSystem publication,
        Action<CancellationToken> requireCurrent,
        Action verifyPublished,
        Action verifyUnpublished,
        PackageStoreDeletionJournalPublicationPoint beforePublishPoint,
        PackageStoreDeletionJournalPublicationPoint afterPublishPoint,
        string refusalMessage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        requireCurrent(cancellationToken);
        VerifyRootAndControl(heldRoot, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        var prepared = PhysicalStorePublicationChecks.PreparePublish(_files, _names, control,
            stagedName, stagedIdentity, canonicalName, expectedDestinationIdentity);
        if (prepared.ParentIdentity != controlIdentity || prepared.Semantics != controlSemantics)
            throw Refuse("The exact journal stage does not bind the expected native control-directory profile.", expectedRoot);

        try
        {
            _checkpoint?.Invoke(beforePublishPoint, journal);
            publication.PublishControlFileAt(control, stagedName, stagedIdentity, canonicalName,
                expectedDestinationIdentity);
            _checkpoint?.Invoke(afterPublishPoint, journal);
            verifyPublished();
            return new PublishedJournal(journal, canonicalName, stagedName, stagedIdentity, payload,
                rootSemantics, controlIdentity, controlSemantics);
        }
        catch (Exception publicationFailure)
        {
            try
            {
                verifyPublished();
                return new PublishedJournal(journal, canonicalName, stagedName, stagedIdentity, payload,
                    rootSemantics, controlIdentity, controlSemantics);
            }
            catch (Exception publishedStateFailure)
            {
                try { verifyUnpublished(); }
                catch (Exception stageStateFailure)
                {
                    throw Refuse(refusalMessage, expectedRoot,
                        new AggregateException("Publication and both exact native state inspections failed.",
                            publicationFailure, publishedStateFailure, stageStateFailure));
                }

                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
                throw new InvalidOperationException("Unreachable after rethrowing a preserved publication failure.");
            }
        }
    }

    private List<PruneArtifact> InspectPruneArtifacts(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        IReadOnlyList<string> names,
        PhysicalRootIdentity expectedRoot)
    {
        var reservedCount = names.Count(static name => name.StartsWith("prune", StringComparison.OrdinalIgnoreCase));
        if (reservedCount > MaximumPruneArtifacts)
            throw Refuse("The reserved prune-artifact namespace exceeds its name-count limit.", expectedRoot);

        var artifacts = new List<PruneArtifact>(reservedCount);
        long totalBytes = 0;
        foreach (var name in names)
        {
            if (!name.StartsWith("prune", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = ParseArtifactName(name, expectedRoot);
            try { PhysicalStoreDeletionManifestEntry.ValidateComponent(name, controlSemantics); }
            catch (ArgumentException exception)
            {
                throw Refuse("A reserved prune artifact name is invalid under the native control-directory profile.",
                    expectedRoot, exception);
            }
            var entry = _files.InspectChildNoFollow(control, name)
                ?? throw Refuse("A reserved prune artifact disappeared during native inspection.", expectedRoot);
            if (entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1 ||
                entry.ReparseTag is not (null or 0) || entry.Length < 0 ||
                entry.Length > PackageStoreDeletionJournalSerializer.MaximumPayloadBytes ||
                !SameVolume(entry.Identity, expectedRoot.HandleIdentity))
            {
                throw Refuse("A reserved prune artifact is not a bounded regular single-link control file.", expectedRoot);
            }

            var canonical = PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, control, name, entry.Identity);
            if (canonical.ParentIdentity != controlIdentity || canonical.Semantics != controlSemantics ||
                !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            {
                throw Refuse("A reserved prune artifact is not at its exact canonical native basename.", expectedRoot);
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumAggregatePruneBytes)
                throw Refuse("Existing prune artifacts exceed their aggregate byte limit.", expectedRoot);
            artifacts.Add(new PruneArtifact(name, parsed.CandidateId, parsed.IsStage, entry));
        }

        return artifacts;
    }

    private void ValidateExistingJournals(
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        IReadOnlyList<PruneArtifact> artifacts,
        PhysicalRootIdentity expectedRoot,
        PackageStoreDeletionJournal requested)
    {
        foreach (var artifact in artifacts)
        {
            if (artifact.IsStage)
                continue;

            var bytes = ReadExactFile(control, controlIdentity, controlSemantics, artifact.Name,
                artifact.Info.Identity, expectedRoot.HandleIdentity,
                PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
            PackageStoreDeletionJournal existing;
            try { existing = _serializer.Deserialize(bytes); }
            catch (System.Text.Json.JsonException exception)
            {
                throw Refuse("A reserved canonical prune journal is malformed and was preserved.", expectedRoot, exception);
            }

            if (existing.CandidateId != artifact.CandidateId ||
                !IsKnownPhase(existing.Phase) ||
                existing.StoreRoot != expectedRoot || existing.EnrollmentEpoch != requested.EnrollmentEpoch)
            {
                throw Refuse("An existing prune journal is unknown to this publisher and was preserved.", expectedRoot);
            }

            if (existing.CandidateId == requested.CandidateId ||
                existing.OriginalInstall.DirectoryIdentity == requested.OriginalInstall.DirectoryIdentity ||
                existing.PackageParentIdentity == requested.PackageParentIdentity &&
                string.Equals(existing.OriginalBasename, requested.OriginalBasename, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.OriginalInstall.RootRelativeInstallPath,
                    requested.OriginalInstall.RootRelativeInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse("An existing Prepared record already describes this exact or possibly aliased install slot.", expectedRoot);
            }
        }
    }

    private static void ValidateJournalSet(
        IReadOnlyList<DiscoveredJournal> journals,
        PhysicalRootIdentity expectedRoot)
    {
        for (var leftIndex = 0; leftIndex < journals.Count; leftIndex++)
        {
            var left = journals[leftIndex].Journal;
            if (left.StoreRoot != expectedRoot)
                throw Refuse("A discovered prune journal belongs to another physical store root.", expectedRoot);
            for (var rightIndex = leftIndex + 1; rightIndex < journals.Count; rightIndex++)
            {
                var right = journals[rightIndex].Journal;
                if (left.CandidateId == right.CandidateId ||
                    left.OriginalInstall.DirectoryIdentity == right.OriginalInstall.DirectoryIdentity ||
                    left.PackageParentIdentity == right.PackageParentIdentity &&
                    string.Equals(left.OriginalBasename, right.OriginalBasename, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(left.OriginalInstall.RootRelativeInstallPath,
                        right.OriginalInstall.RootRelativeInstallPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw Refuse("Multiple prune journals describe one exact or possibly aliased install slot.", expectedRoot);
                }
            }
        }
    }

    private IPhysicalStorePublicationFileSystem RequirePublication()
        => _publication ?? throw Unsupported("Deletion journal publication requires native no-replace control publication.");

    private PhysicalStoreDirectoryHandle OpenAndVerifyControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        out PhysicalFileIdentity controlIdentity,
        out PhysicalStoreNameSemantics controlSemantics)
    {
        var entry = _files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory ||
            entry.ReparseTag is not (null or 0) || !SameVolume(entry.Identity, expectedRoot.HandleIdentity))
            throw Refuse("The reserved control directory is missing or does not share the held root's native volume.", expectedRoot);

        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
            root, RootMembershipRegistry.ControlDirectoryName, entry.Identity);
        if (canonical.ParentIdentity != expectedRoot.HandleIdentity || canonical.FileIdentity != entry.Identity ||
            canonical.Semantics != rootSemantics ||
            !string.Equals(canonical.Basename, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal))
            throw Refuse("The reserved control directory is not the exact canonical child of the held root.", expectedRoot);

        var control = _files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        try
        {
            var info = _files.InspectHandle(control);
            if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != entry.Identity ||
                info.ReparseTag is not (null or 0))
                throw Refuse("The opened control directory differs from its no-follow native observation.", expectedRoot);
            controlIdentity = info.Identity;
            controlSemantics = _names.ObserveDirectoryNameSemantics(control);
            VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
            return control;
        }
        catch (Exception primaryFailure)
        {
            try { control.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Opening the control directory failed and its handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
    }

    private void VerifyRootAndControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics expectedRootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity expectedControlIdentity,
        PhysicalStoreNameSemantics expectedControlSemantics)
    {
        var rootInfo = _files.InspectHandle(root);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != expectedRoot.HandleIdentity ||
            _names.ObserveDirectoryNameSemantics(root) != expectedRootSemantics)
            throw Refuse("The held store root identity or native name profile changed.", expectedRoot);

        var controlInfo = _files.InspectHandle(control);
        var namedControl = _files.InspectChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var canonical = _directories.ObserveCanonicalDirectoryNameNoFollow(
            root, RootMembershipRegistry.ControlDirectoryName, expectedControlIdentity);
        if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != expectedControlIdentity ||
            namedControl is null || namedControl.Kind != PhysicalStoreEntryKind.Directory ||
            namedControl.Identity != expectedControlIdentity || !SameVolume(expectedControlIdentity, expectedRoot.HandleIdentity) ||
            canonical.ParentIdentity != expectedRoot.HandleIdentity || canonical.FileIdentity != expectedControlIdentity ||
            canonical.Semantics != expectedRootSemantics ||
            !string.Equals(canonical.Basename, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal) ||
            _names.ObserveDirectoryNameSemantics(control) != expectedControlSemantics)
            throw Refuse("The held control directory no longer binds the exact root and native profile.", expectedRoot);
    }

    private PhysicalStoreNameSemantics VerifyRoot(PhysicalStoreDirectoryHandle root, PhysicalRootIdentity expectedRoot)
    {
        var info = _files.InspectHandle(root);
        if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != expectedRoot.HandleIdentity)
            throw Refuse("The supplied held directory does not match the expected physical store root.", expectedRoot);
        return _names.ObserveDirectoryNameSemantics(root);
    }

    private PhysicalFileIdentity CreateFlushedStage(
        PhysicalStoreDirectoryHandle control,
        string stagedName,
        byte[] payload,
        PhysicalFileIdentity rootIdentity)
    {
        var staged = _files.CreateFileExclusiveAt(control, stagedName);
        PhysicalFileIdentity identity;
        try
        {
            _files.WriteNewControlFile(staged, payload);
            var info = _files.InspectHandle(staged);
            RequireControlFile(info, expectedIdentity: null, rootIdentity, payload.Length,
                "The flushed Prepared stage is not a regular single-link file on the held root's volume.");
            identity = info.Identity;
        }
        catch (Exception primaryFailure)
        {
            try { staged.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Writing the Prepared stage failed and its creation handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }

        staged.Dispose();
        return identity;
    }

    private void VerifyPublishedInControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string canonicalName,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal journal)
    {
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        var prepared = new PhysicalStoreCanonicalName(controlIdentity, stagedIdentity, stagedName, controlSemantics);
        var published = PhysicalStorePublicationChecks.VerifyPublished(
            _files, _names, control, stagedName, canonicalName, prepared);
        RequireControlFile(published, stagedIdentity, expectedRoot.HandleIdentity, payload.Length,
            "The canonical Prepared journal does not match the flushed stage identity and length.");
        var bytes = ReadExactFile(control, controlIdentity, controlSemantics, canonicalName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(bytes, payload, journal, expectedRoot);
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
    }

    private void VerifyUnpublishedStageInControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        string canonicalName,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal journal)
    {
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(control, canonicalName), expectedIdentity: null);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, stagedName, stagedIdentity,
            expectedRoot.HandleIdentity, payload.Length);
        var bytes = ReadExactFile(control, controlIdentity, controlSemantics, stagedName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(bytes, payload, journal, expectedRoot);
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
    }

    private void VerifyUnpublishedUpdateInControl(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalStoreDirectoryHandle control,
        PhysicalFileIdentity controlIdentity,
        PhysicalStoreNameSemantics controlSemantics,
        DiscoveredJournal prior,
        string stagedName,
        PhysicalFileIdentity stagedIdentity,
        byte[] payload,
        PackageStoreDeletionJournal updated)
    {
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
        VerifyCanonicalFile(control, controlIdentity, controlSemantics, prior.CanonicalName,
            prior.FileIdentity, expectedRoot.HandleIdentity, prior.Payload.Length);
        var oldBytes = ReadExactFile(control, controlIdentity, controlSemantics, prior.CanonicalName,
            prior.FileIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        if (!oldBytes.AsSpan().SequenceEqual(prior.Payload))
            throw Refuse("The expected prior journal changed while phase publication was in progress.", expectedRoot);
        var restoredPrior = _serializer.Deserialize(oldBytes);
        if (restoredPrior.IntegrityDigest != prior.Journal.IntegrityDigest)
            throw Refuse("The prior journal no longer has its exact expected integrity digest.", expectedRoot);

        VerifyCanonicalFile(control, controlIdentity, controlSemantics, stagedName, stagedIdentity,
            expectedRoot.HandleIdentity, payload.Length);
        var stageBytes = ReadExactFile(control, controlIdentity, controlSemantics, stagedName,
            stagedIdentity, expectedRoot.HandleIdentity, PackageStoreDeletionJournalSerializer.MaximumPayloadBytes);
        RequireExactPayload(stageBytes, payload, updated, expectedRoot);
        VerifyRootAndControl(root, expectedRoot, rootSemantics, control, controlIdentity, controlSemantics);
    }

    private void VerifyCanonicalFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        long expectedLength)
    {
        var canonical = PhysicalStorePublicationChecks.PrepareRemoval(_files, _names, parent, name, expectedIdentity);
        if (canonical.ParentIdentity != parentIdentity || canonical.Semantics != semantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
            throw Refuse("A deletion journal control file is not at its exact canonical native name.");
        var entry = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(entry, expectedIdentity, rootIdentity, expectedLength,
            "A deletion journal control file has the wrong identity, type, link count, length or volume.");
    }

    private byte[] ReadExactFile(
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics semantics,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity rootIdentity,
        int maximumBytes)
    {
        var before = PhysicalStorePublicationChecks.RequireExpectedEntry(
            _files.InspectChildNoFollow(parent, name), expectedIdentity)!;
        RequireControlFile(before, expectedIdentity, rootIdentity, before.Length,
            "A deletion journal file changed before its bounded read.");
        VerifyCanonicalFile(parent, parentIdentity, semantics, name, expectedIdentity, rootIdentity, before.Length);

        var opened = _files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        byte[] bytes;
        try
        {
            var info = _files.InspectHandle(opened);
            RequireControlFile(info, expectedIdentity, rootIdentity, before.Length,
                "The reopened deletion journal changed identity, type, link count, volume or length.");
            bytes = _files.ReadControlFile(opened, maximumBytes);
            var after = _files.InspectHandle(opened);
            RequireControlFile(after, expectedIdentity, rootIdentity, bytes.Length,
                "The deletion journal changed during its bounded read.");
            if (bytes.Length != before.Length)
                throw Refuse("The deletion journal length changed during its bounded read.");
        }
        catch (Exception primaryFailure)
        {
            try { opened.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Reading a deletion journal failed and its temporary handle could not be closed.",
                    primaryFailure, cleanupFailure);
            }
            throw;
        }
        opened.Dispose();
        VerifyCanonicalFile(parent, parentIdentity, semantics, name, expectedIdentity, rootIdentity, bytes.Length);
        return bytes;
    }

    private void RequireExactPayload(byte[] actual, byte[] expected, PackageStoreDeletionJournal journal,
        PhysicalRootIdentity expectedRoot)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw Refuse("The reopened deletion journal differs from the exact flushed candidate bytes.", expectedRoot);
        var restored = _serializer.Deserialize(actual);
        if (restored.IntegrityDigest != journal.IntegrityDigest || restored.CandidateId != journal.CandidateId ||
            restored.OperationId != journal.OperationId || restored.Phase != journal.Phase ||
            restored.StoreRoot != expectedRoot || restored.Manifest.InstallDirectoryIdentity != journal.Manifest.InstallDirectoryIdentity)
            throw Refuse("The reopened deletion journal does not bind the exact phase candidate.", expectedRoot);
    }

    private static PruneArtifactName ParseArtifactName(string name, PhysicalRootIdentity root)
    {
        const string prefix = "prune-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            throw Refuse("A malformed or aliased name occupies the reserved prune namespace.", root);

        var stageSeparator = name.IndexOf(".stage-", StringComparison.Ordinal);
        if (stageSeparator >= 0)
        {
            var canonical = name[..stageSeparator];
            var publicationText = name[(stageSeparator + ".stage-".Length)..];
            var candidateId = ParseCanonicalName(canonical, root);
            var publicationId = ParseGuidN(publicationText, root);
            if (publicationId == Guid.Empty || publicationId == candidateId)
                throw Refuse("A prune stage has an invalid or non-distinct publication ID.", root);
            return new PruneArtifactName(candidateId, IsStage: true);
        }

        return new PruneArtifactName(ParseCanonicalName(name, root), IsStage: false);
    }

    private static Guid ParseCanonicalName(string name, PhysicalRootIdentity root)
    {
        const string prefix = "prune-";
        const string suffix = ".json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + 32 + suffix.Length)
            throw Refuse("A malformed or aliased name occupies the reserved prune namespace.", root);
        return ParseGuidN(name.Substring(prefix.Length, 32), root);
    }

    private static Guid ParseGuidN(string value, PhysicalRootIdentity root)
    {
        if (value.Length != 32 || value.Any(static character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !Guid.TryParseExact(value, "N", out var parsed) ||
            !string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal))
            throw Refuse("A reserved prune artifact has a noncanonical GUID component.", root);
        return parsed;
    }

    private static string CanonicalName(Guid candidateId) => $"prune-{candidateId:N}.json";

    private static string StagedName(Guid candidateId, Guid publicationId)
        => $"{CanonicalName(candidateId)}.stage-{publicationId:N}";

    private static Guid NewDistinctId(Guid first, Guid second)
    {
        Guid id;
        do id = Guid.NewGuid(); while (id == Guid.Empty || id == first || id == second);
        return id;
    }

    private static void RequireControlFile(PhysicalStoreEntryInfo info, PhysicalFileIdentity? expectedIdentity,
        PhysicalFileIdentity rootIdentity, long expectedLength, string message)
    {
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 ||
            info.ReparseTag is not (null or 0) || (expectedIdentity is not null && info.Identity != expectedIdentity) ||
            info.Length != expectedLength || !SameVolume(info.Identity, rootIdentity))
            throw Refuse(message);
    }

    private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
           string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal);

    private static bool IsKnownPhase(PackageStoreDeletionPhase phase)
        => phase is PackageStoreDeletionPhase.Prepared or PackageStoreDeletionPhase.Quarantined or
            PackageStoreDeletionPhase.Deleting or PackageStoreDeletionPhase.Finalizing;

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private static PackageStoreAdmissionException Refuse(string message, PhysicalRootIdentity? root = null,
        Exception? innerException = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root, innerException);

    internal sealed record PublishedJournal(
        PackageStoreDeletionJournal Journal,
        string CanonicalName,
        string StagedName,
        PhysicalFileIdentity FileIdentity,
        byte[] Payload,
        PhysicalStoreNameSemantics RootSemantics,
        PhysicalFileIdentity ControlDirectoryIdentity,
        PhysicalStoreNameSemantics ControlDirectorySemantics);

    internal sealed record DiscoveredJournal(
        PackageStoreDeletionJournal Journal,
        string CanonicalName,
        PhysicalFileIdentity FileIdentity,
        byte[] Payload,
        PhysicalStoreNameSemantics RootSemantics,
        PhysicalFileIdentity ControlDirectoryIdentity,
        PhysicalStoreNameSemantics ControlDirectorySemantics);

    private sealed record PruneArtifact(string Name, Guid CandidateId, bool IsStage, PhysicalStoreEntryInfo Info);
    private readonly record struct PruneArtifactName(Guid CandidateId, bool IsStage);
}

internal enum PackageStoreDeletionJournalPublicationPoint
{
    StageFlushed,
    BeforeNativePublish,
    AfterNativePublish,
    BeforeQuarantinedPhasePublish,
    AfterQuarantinedPhasePublish,
    BeforeDeletingPhasePublish,
    AfterDeletingPhasePublish,
    BeforeFinalizingPhasePublish,
    AfterFinalizingPhasePublish
}
