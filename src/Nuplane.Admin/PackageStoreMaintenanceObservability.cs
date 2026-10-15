using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Nuplane.Admin;

internal interface IPackageStoreMaintenanceObservability
{
    PackageStoreMaintenanceOperation Begin(string operation, string rootLabel, string? correlationId);

    void RecordEnrollmentPreview(PackageStoreMaintenanceOperation operation, PackageStoreEnrollmentPreviewResult result);

    void RecordPrune(PackageStoreMaintenanceOperation operation, PackageStoreMaintenanceResult result);

    void RecordEnrollment(PackageStoreMaintenanceOperation operation, PackageStoreEnrollmentResult result);

    void RecordFailure(PackageStoreMaintenanceOperation operation, Exception exception, bool cancelled);
}

internal sealed record PackageStoreMaintenanceOperation(string Name, string RootLabel, string CorrelationId);

/// <summary>Records bounded maintenance outcomes and operation-scoped diagnostics.</summary>
internal sealed partial class PackageStoreMaintenanceObservability : IPackageStoreMaintenanceObservability, IDisposable
{
    private readonly Meter _meter = new("Nuplane.Admin", "0.1.0");
    private readonly Counter<long> _operations;
    private readonly Counter<long> _physicalOutcomes;
    private readonly ILogger<PackageStoreMaintenanceService> _logger;
    private readonly PackageStoreMaintenanceOperationalState _operationalState;

    public PackageStoreMaintenanceObservability(
        ILogger<PackageStoreMaintenanceService> logger,
        PackageStoreMaintenanceOperationalState operationalState)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(operationalState);
        _logger = logger;
        _operationalState = operationalState;
        _operations = _meter.CreateCounter<long>("nuplane.package_store.maintenance.operations");
        _physicalOutcomes = _meter.CreateCounter<long>("nuplane.package_store.maintenance.physical");
    }

    public void Dispose() => _meter.Dispose();

    public PackageStoreMaintenanceOperation Begin(string operation, string rootLabel, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLabel);
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")
            : correlationId;
        return new(operation, rootLabel, effectiveCorrelationId);
    }

    public void RecordEnrollmentPreview(PackageStoreMaintenanceOperation operation, PackageStoreEnrollmentPreviewResult result)
    {
        var unknownClosures = result.Members.Count(static member =>
            member.ActiveClosure.Knowledge == "Unknown" || member.RecoverableClosure.Knowledge == "Unknown");
        var graphCount = result.Members.Sum(static member =>
            (member.ActiveClosure.GraphIds?.Count ?? 0) + (member.RecoverableClosure.GraphIds?.Count ?? 0));
        var details = Join(result.Diagnostics.Concat(result.Members.SelectMany(static member => member.Diagnostics)));
        RecordResult(operation, "preview", "Preview", result.EnrollmentEpoch, result.Membership ?? "Unknown",
            inventoryComplete: false, candidateCount: result.Members.Count, retainedCount: 0, eligibleCount: 0,
            refusedCount: unknownClosures, graphUseCount: graphCount, physicalDeleted: 0, alreadyAbsent: 0,
            partiallyRemoved: 0, physicalRetained: 0, quarantined: 0, quarantineRootAbsent: 0,
            failedCandidates: 0, protectionReasons: unknownClosures == 0 ? "Known" : "UnknownClosures",
            diagnostics: details, physicalOutcomes: EmptyPhysicalOutcomes);
    }

    public void RecordPrune(PackageStoreMaintenanceOperation operation, PackageStoreMaintenanceResult result)
    {
        var outcome = result.Status switch
        {
            "Preview" => "preview",
            "Completed" => "completed",
            "Refused" => "refused",
            "Cancelled" => "cancelled",
            _ => "failed"
        };
        var actualDeleted = result.Candidates.Where(static candidate => candidate.PhysicalOutcome == "Deleted").ToArray();
        var alreadyAbsent = result.Candidates.Count(static candidate => candidate.PhysicalOutcome == "AlreadyAbsent");
        var partiallyRemoved = result.Candidates.Count(static candidate => candidate.PhysicalOutcome == "PartiallyRemoved");
        var physical = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["deleted"] = actualDeleted.Length,
            ["already_absent"] = alreadyAbsent,
            ["partially_removed"] = partiallyRemoved,
            ["retained"] = result.Candidates.Count(static candidate => candidate.PhysicalOutcome == "Retained"),
            ["quarantined"] = result.Candidates.Count(static candidate => candidate.PhysicalOutcome == "Quarantined"),
            ["quarantine_root_absent_unattributed"] = result.Candidates.Count(static candidate =>
                candidate.PhysicalOutcome == "QuarantineRootAbsentUnattributed")
        };
        var reasons = result.Candidates.SelectMany(static candidate => candidate.Reasons)
            .Concat(result.Diagnostics.Select(static diagnostic => diagnostic.Code));
        RecordResult(operation, outcome, result.Status, result.EnrollmentEpoch, result.ProtectionKnowledge,
            result.InventoryComplete, result.Candidates.Count,
            result.Candidates.Count(static candidate => candidate.Classification == "Retained"),
            result.Candidates.Count(static candidate => candidate.Classification == "Eligible"),
            result.Candidates.Count(static candidate => candidate.Classification == "Refused"),
            result.Uses.Count, actualDeleted.Length, alreadyAbsent, partiallyRemoved,
            physical["retained"], physical["quarantined"], physical["quarantine_root_absent_unattributed"],
            result.Candidates.Count(static candidate => candidate.Attempt == "Failed"), Join(reasons),
            Join(result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
                .Concat(result.Candidates.Where(static candidate => candidate.Diagnostic is not null)
                    .Select(static candidate => $"{candidate.Diagnostic!.Code}: {candidate.Diagnostic.Message}"))), physical);
    }

    public void RecordEnrollment(PackageStoreMaintenanceOperation operation, PackageStoreEnrollmentResult result)
    {
        var outcome = result.Status switch
        {
            "Complete" => "completed",
            "Refused" => "refused",
            _ => "failed"
        };
        RecordResult(operation, outcome, result.Status, result.EnrollmentEpoch, "NotApplicable",
            inventoryComplete: false, candidateCount: result.Members.Count,
            retainedCount: result.Members.Count(static member => member.Disposition.Contains("Retain", StringComparison.OrdinalIgnoreCase)),
            eligibleCount: result.Members.Count(static member => member.Disposition.Contains("Complete", StringComparison.OrdinalIgnoreCase)),
            refusedCount: result.Members.Count(static member => member.Disposition.Contains("Refus", StringComparison.OrdinalIgnoreCase)),
            graphUseCount: 0, physicalDeleted: 0, alreadyAbsent: 0, partiallyRemoved: 0,
            physicalRetained: 0, quarantined: 0, quarantineRootAbsent: 0,
            failedCandidates: result.Members.Count(static member => member.Disposition.Contains("Fail", StringComparison.OrdinalIgnoreCase)),
            protectionReasons: Join(result.Members.SelectMany(static member => member.Diagnostics)),
            diagnostics: Join(result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
                .Concat(result.Members.SelectMany(static member => member.Diagnostics))),
            physicalOutcomes: EmptyPhysicalOutcomes);
    }

    public void RecordFailure(PackageStoreMaintenanceOperation operation, Exception exception, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var outcome = cancelled ? "cancelled" : "failed";
        _operationalState.RecordOutcome(operation.Name, outcome);
        RecordOperationMetric(operation.Name, outcome);
        LogOperation(
            outcome == "cancelled" ? LogLevel.Warning : LogLevel.Error,
            operation.Name,
            operation.RootLabel,
            operation.CorrelationId,
            outcome,
            outcome,
            enrollmentEpoch: null,
            protectionKnowledge: "Unknown",
            inventoryComplete: false,
            candidateCount: 0,
            retainedCount: 0,
            eligibleCount: 0,
            refusedCount: 0,
            graphUseCount: 0,
            physicalDeleted: 0,
            alreadyAbsent: 0,
            partiallyRemoved: 0,
            physicalRetained: 0,
            quarantined: 0,
            quarantineRootAbsent: 0,
            failedCandidates: 0,
            protectionReasons: string.Empty,
            diagnostics: $"{exception.GetType().Name}: {exception.Message}",
            exception: exception);
    }

    private void RecordResult(
        PackageStoreMaintenanceOperation operation,
        string outcome,
        string status,
        long? enrollmentEpoch,
        string protectionKnowledge,
        bool inventoryComplete,
        int candidateCount,
        int retainedCount,
        int eligibleCount,
        int refusedCount,
        int graphUseCount,
        int physicalDeleted,
        int alreadyAbsent,
        int partiallyRemoved,
        int physicalRetained,
        int quarantined,
        int quarantineRootAbsent,
        int failedCandidates,
        string protectionReasons,
        string diagnostics,
        IReadOnlyDictionary<string, int> physicalOutcomes)
    {
        _operationalState.RecordOutcome(operation.Name, outcome);
        RecordOperationMetric(operation.Name, outcome);
        foreach (var (physicalOutcome, count) in physicalOutcomes)
        {
            if (count <= 0)
                continue;
            var tags = new TagList { { "outcome", physicalOutcome } };
            try { _physicalOutcomes.Add(count, tags); }
            catch (Exception) { }
        }
        var level = outcome switch
        {
            "refused" => LogLevel.Warning,
            "failed" => LogLevel.Error,
            "cancelled" => LogLevel.Warning,
            _ => LogLevel.Information
        };
        LogOperation(level, operation.Name, operation.RootLabel, operation.CorrelationId, outcome, status,
            enrollmentEpoch, protectionKnowledge, inventoryComplete, candidateCount, retainedCount, eligibleCount,
            refusedCount, graphUseCount, physicalDeleted, alreadyAbsent, partiallyRemoved, physicalRetained,
            quarantined, quarantineRootAbsent, failedCandidates, protectionReasons, diagnostics, null);
    }

    private void RecordOperationMetric(string operation, string outcome)
    {
        try { _operations.Add(1, OperationTags(operation, outcome)); }
        catch (Exception) { }
    }

    private void LogOperation(
        LogLevel level,
        string operation,
        string rootLabel,
        string correlationId,
        string outcome,
        string status,
        long? enrollmentEpoch,
        string protectionKnowledge,
        bool inventoryComplete,
        int candidateCount,
        int retainedCount,
        int eligibleCount,
        int refusedCount,
        int graphUseCount,
        int physicalDeleted,
        int alreadyAbsent,
        int partiallyRemoved,
        int physicalRetained,
        int quarantined,
        int quarantineRootAbsent,
        int failedCandidates,
        string protectionReasons,
        string diagnostics,
        Exception? exception)
    {
        try
        {
            MaintenanceOperationLogged(_logger, level, operation, rootLabel, correlationId, outcome, status,
                enrollmentEpoch, protectionKnowledge, inventoryComplete, candidateCount, retainedCount,
                eligibleCount, refusedCount, graphUseCount, physicalDeleted, alreadyAbsent, partiallyRemoved,
                physicalRetained, quarantined, quarantineRootAbsent, failedCandidates, protectionReasons,
                diagnostics, exception);
        }
        catch (Exception) { }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyPhysicalOutcomes = new Dictionary<string, int>();

    private static TagList OperationTags(string operation, string outcome)
        => new() { { "operation", operation }, { "outcome", outcome } };

    private static string Join(IEnumerable<string> values)
        => string.Join("; ", values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    [LoggerMessage(EventId = 2000,
        Message = "Package-store maintenance [Operation={Operation}, RootLabel={RootLabel}, CorrelationId={CorrelationId}, Outcome={Outcome}, Status={Status}, EnrollmentEpoch={EnrollmentEpoch}, ProtectionKnowledge={ProtectionKnowledge}, InventoryComplete={InventoryComplete}, CandidateCount={CandidateCount}, RetainedCount={RetainedCount}, EligibleCount={EligibleCount}, RefusedCount={RefusedCount}, GraphUseCount={GraphUseCount}, PhysicalDeleted={PhysicalDeleted}, AlreadyAbsent={AlreadyAbsent}, PartiallyRemoved={PartiallyRemoved}, PhysicalRetained={PhysicalRetained}, Quarantined={Quarantined}, QuarantineRootAbsent={QuarantineRootAbsent}, FailedCandidates={FailedCandidates}, ProtectionReasons={ProtectionReasons}, Diagnostics={Diagnostics}]")]
    private static partial void MaintenanceOperationLogged(
        ILogger logger, LogLevel logLevel, string operation, string rootLabel, string correlationId,
        string outcome, string status, long? enrollmentEpoch, string protectionKnowledge,
        bool inventoryComplete, int candidateCount, int retainedCount, int eligibleCount, int refusedCount,
        int graphUseCount, int physicalDeleted, int alreadyAbsent, int partiallyRemoved, int physicalRetained,
        int quarantined, int quarantineRootAbsent, int failedCandidates, string protectionReasons,
        string diagnostics, Exception? exception);
}
