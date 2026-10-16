using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Feeds;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Maintenance;

namespace Nuplane.PackageStore.TestHost;

internal static partial class Program
{
    private static async Task<int> RunPackageStoreExecutionAsync(string requestPath)
    {
        var request = await ReadExecutionRequestAsync(requestPath).ConfigureAwait(false);
        await WriteJsonLineAsync(new ExecutionStartedMessage("started", request.OperationId, Environment.ProcessId))
            .ConfigureAwait(false);

        // Keep this on the existing participant composition path so the test drives the actual
        // registered native catalog, root resolver, and ownership boundary.
        var graphUseRequest = new GraphUseProcessRequest(request.OperationId, request.RootPath,
            request.StateFilePath, [], request.CompleteRoster);
        await using var provider = BuildGraphUseProvider(graphUseRequest, request.CompleteRoster);
        var registry = ((PackageStoreAdmission)provider.GetRequiredService<IPackageStoreAdmission>()).Registry;
        var files = registry.Files;
        var inspection = new PackageStoreInspectionService(files, registry,
            provider.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            provider.GetRequiredService<IPackageStoreRootResolver>(),
            new PackageStoreInventory(files), new PackageStoreRetentionPlanner());

        using var cancellation = new CancellationTokenSource();
        if (request.CancelBeforeWork)
            cancellation.Cancel();

        var seenCandidates = new HashSet<Guid>();
        var publicationCheckpointPaused = false;
        void PauseForTermination(string point, PackageStoreDeletionJournal journal)
        {
            WriteJsonLineAsync(new ExecutionCheckpointMessage("checkpoint", request.OperationId,
                Environment.ProcessId, point, journal.Phase.ToString(), journal.CandidateId.ToString("N"),
                journal.OriginalInstall.PackageId, journal.OriginalInstall.Version,
                journal.QuarantineBasename, journal.Manifest.Entries.Select(static entry =>
                    string.Join("/", entry.RelativeComponents)).Order(StringComparer.Ordinal).ToArray()))
                .GetAwaiter().GetResult();
            WaitForContinueCommand(request.OperationId, "package-store execution checkpoint", "execution checkpoint");
        }

        void OnPublication(PackageStoreDeletionJournalPublicationPoint point, PackageStoreDeletionJournal journal)
        {
            if (!publicationCheckpointPaused && request.StopAtJournalPublicationPoint == point.ToString() &&
                (point != PackageStoreDeletionJournalPublicationPoint.AfterNativePublish ||
                 journal.Phase == PackageStoreDeletionPhase.Prepared))
            {
                publicationCheckpointPaused = true;
                PauseForTermination(point.ToString(), journal);
            }
        }

        void OnDeletion(PackageStoreDeletionExecutionPoint point, PackageStoreDeletionJournal journal)
        {
            if (request.StopAtDeletionCheckpoint == point.ToString())
            {
                PauseForTermination(point.ToString(), journal);
                return;
            }

            if (request.CancelAtDeletionCheckpoint != point.ToString())
                return;

            seenCandidates.Add(journal.CandidateId);
            if (seenCandidates.Count != request.CancelAtEligibleCandidateOrdinal)
                return;

            WriteJsonLineAsync(new ExecutionCheckpointMessage("checkpoint", request.OperationId,
                Environment.ProcessId, point.ToString(), journal.Phase.ToString(), journal.CandidateId.ToString("N"),
                journal.OriginalInstall.PackageId, journal.OriginalInstall.Version,
                journal.QuarantineBasename, journal.Manifest.Entries.Select(static entry =>
                    string.Join("/", entry.RelativeComponents)).Order(StringComparer.Ordinal).ToArray()))
                .GetAwaiter().GetResult();
            cancellation.Cancel();
        }

        var execution = new PackageStoreExecutionService(registry, inspection,
            OnPublication, deletionExecutionCheckpoint: OnDeletion);
        try
        {
            var outcome = await execution.ExecuteEligibleCandidatesAsync(request.RootLabel,
                request.EnrollmentEpoch, request.KeepNewestInactiveVersionsPerPackage, cancellation.Token)
                .ConfigureAwait(false);
            await WriteJsonLineAsync(new ExecutionResultMessage("execution-result", request.OperationId,
                Environment.ProcessId, outcome.Status.ToString(), outcome.Candidates.Select(static candidate =>
                    new ExecutionCandidateMessage(candidate.Install.PackageId, candidate.Install.Version,
                        candidate.Install.RootRelativeInstallPath, candidate.Install.DirectoryIdentity.FileId,
                        candidate.Classification.ToString(), candidate.Attempt.ToString(),
                        candidate.PhysicalOutcome?.ToString(), candidate.JournalPhase?.ToString(),
                        candidate.MissingAtStart?.ToArray(), candidate.MissingAtLastObservation?.ToArray(),
                        candidate.Reasons.Select(static reason => reason.ToString()).ToArray(),
                        candidate.Diagnostic?.GetType().Name)).ToArray(),
                outcome.Diagnostic?.GetType().Name, outcome.StopCandidate?.PackageId)).ConfigureAwait(false);
            return 0;
        }
        catch (PackageStoreAdmissionException exception)
        {
            await WriteJsonLineAsync(new ExecutionRefusedMessage("refused", request.OperationId,
                Environment.ProcessId, exception.Reason.ToString(), exception.GetType().Name)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            await WriteJsonLineAsync(new ExecutionCancelledMessage("cancelled-before-work", request.OperationId,
                Environment.ProcessId, exception.GetType().Name)).ConfigureAwait(false);
            return 0;
        }
    }

    private static async Task<PackageStoreExecutionProcessRequest> ReadExecutionRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<PackageStoreExecutionProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The package-store execution request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathFullyQualified(request.RootPath) ||
            string.IsNullOrWhiteSpace(request.StateFilePath) || !Path.IsPathFullyQualified(request.StateFilePath) ||
            string.IsNullOrWhiteSpace(request.RootLabel) || request.EnrollmentEpoch <= 0 ||
            request.KeepNewestInactiveVersionsPerPackage < 0 ||
            request.CompleteRoster is null || request.CompleteRoster.Length is < 1 or > 16 ||
            request.CompleteRoster.Any(static member => member is null ||
                string.IsNullOrWhiteSpace(member.RootLabel) || string.IsNullOrWhiteSpace(member.MemberId) ||
                string.IsNullOrWhiteSpace(member.StateFilePath) || !Path.IsPathFullyQualified(member.StateFilePath)) ||
            request.CompleteRoster.Select(static member => member.MemberId)
                .Distinct(StringComparer.Ordinal).Count() != request.CompleteRoster.Length ||
            request.CancelAtEligibleCandidateOrdinal < 0 ||
            (request.CancelAtDeletionCheckpoint is null && request.CancelAtEligibleCandidateOrdinal != 0) ||
            (request.CancelAtDeletionCheckpoint is not null && request.CancelAtEligibleCandidateOrdinal == 0))
        {
            throw new InvalidDataException("The package-store execution request fields were invalid.");
        }

        if (request.StopAtJournalPublicationPoint is { } publicationName &&
            (!Enum.TryParse<PackageStoreDeletionJournalPublicationPoint>(publicationName,
                 ignoreCase: false, out var publicationPoint) || !Enum.IsDefined(publicationPoint)))
            throw new InvalidDataException("The journal-publication checkpoint is not supported.");

        if (request.StopAtDeletionCheckpoint is { } deletionName &&
            (!Enum.TryParse<PackageStoreDeletionExecutionPoint>(deletionName,
                 ignoreCase: false, out var deletionPoint) || !Enum.IsDefined(deletionPoint)))
            throw new InvalidDataException("The deletion checkpoint is not supported.");

        if (request.CancelAtDeletionCheckpoint is { } cancellationName &&
            (!Enum.TryParse<PackageStoreDeletionExecutionPoint>(cancellationName,
                 ignoreCase: false, out var cancelPoint) || !Enum.IsDefined(cancelPoint)))
            throw new InvalidDataException("The cancellation checkpoint is not supported.");

        var actionCount = (request.StopAtJournalPublicationPoint is null ? 0 : 1) +
                          (request.StopAtDeletionCheckpoint is null ? 0 : 1) +
                          (request.CancelAtDeletionCheckpoint is null ? 0 : 1) +
                          (request.CancelBeforeWork ? 1 : 0);
        if (actionCount > 1)
            throw new InvalidDataException("One package-store execution request can select only one checkpoint action.");

        return request;
    }

    private sealed record PackageStoreExecutionProcessRequest(
        string OperationId,
        string RootPath,
        string StateFilePath,
        PackageStoreMemberDeclaration[] CompleteRoster,
        long EnrollmentEpoch,
        int KeepNewestInactiveVersionsPerPackage,
        string RootLabel = "default",
        string? StopAtJournalPublicationPoint = null,
        string? StopAtDeletionCheckpoint = null,
        string? CancelAtDeletionCheckpoint = null,
        int CancelAtEligibleCandidateOrdinal = 0,
        bool CancelBeforeWork = false);

    private sealed record ExecutionStartedMessage(string Kind, string OperationId, int ProcessId);
    private sealed record ExecutionCheckpointMessage(string Kind, string OperationId, int ProcessId, string Point,
        string JournalPhase, string CandidateId, string PackageId, string Version, string QuarantineBasename,
        string[] ManifestPaths);
    private sealed record ExecutionCandidateMessage(string PackageId, string Version, string RootRelativeInstallPath,
        string DirectoryFileId, string Classification, string Attempt, string? PhysicalOutcome, string? JournalPhase,
        string[]? MissingAtStart, string[]? MissingAtLastObservation, string[] Reasons, string? DiagnosticType);
    private sealed record ExecutionResultMessage(string Kind, string OperationId, int ProcessId, string Status,
        ExecutionCandidateMessage[] Candidates, string? DiagnosticType, string? StopCandidatePackageId);
    private sealed record ExecutionRefusedMessage(string Kind, string OperationId, int ProcessId, string Reason,
        string DiagnosticType);
    private sealed record ExecutionCancelledMessage(string Kind, string OperationId, int ProcessId,
        string DiagnosticType);
}
