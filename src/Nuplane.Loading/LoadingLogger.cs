using Microsoft.Extensions.Logging;

namespace Nuplane.Loading;

internal static partial class LoadingLogger
{
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Evaluated load mode advisor {AdvisorName} for graph {GraphKey} with ResultCount={ResultCount}.")]
    public static partial void LoadModeAdvisorEvaluated(
        this ILogger logger,
        string advisorName,
        string graphKey,
        int resultCount);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "Ignored invalid package load-mode advisor result for {PackageId}@{Version} in graph {GraphKey}: {Diagnostic}")]
    public static partial void InvalidPackageLoadModeAdvisorResult(
        this ILogger logger,
        string packageId,
        string version,
        string graphKey,
        string diagnostic);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Information,
        Message = "Suppressed package load-mode advisor result for {PackageId}@{Version} in graph {GraphKey} because a higher-precedence load-mode policy was configured.")]
    public static partial void PackageLoadModeAdvisorResultSuppressed(
        this ILogger logger,
        string packageId,
        string version,
        string graphKey);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Warning,
        Message = "Resolved package load metadata conflict in graph {GraphKey} by selecting {LoadMode}.")]
    public static partial void PackageLoadMetadataConflict(
        this ILogger logger,
        string graphKey,
        PackageLoadMode loadMode);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Information,
        Message = "Selected graph load mode {LoadMode} for graph {GraphKey}.")]
    public static partial void GraphLoadModeSelected(
        this ILogger logger,
        string graphKey,
        PackageLoadMode loadMode);

    [LoggerMessage(
        EventId = 2106,
        Level = LogLevel.Information,
        Message = "Discovered package load metadata for {PackageId}@{Version} in graph {GraphKey}: LoadMode={LoadMode}, Scope={Scope}.")]
    public static partial void PackageLoadMetadataDiscovered(
        this ILogger logger,
        string packageId,
        string version,
        string graphKey,
        PackageLoadMode loadMode,
        string scope);

    [LoggerMessage(
        EventId = 2107,
        Level = LogLevel.Debug,
        Message = "Skipped graph load for packages {PackageKeys} because every member was already evaluated as an inert graph member that contributes no assemblies. CorrelationId={CorrelationId}")]
    public static partial void LogInertGraphSkipped(
        this ILogger logger,
        string packageKeys,
        string correlationId);

    [LoggerMessage(
        EventId = 2108,
        Level = LogLevel.Warning,
        Message = "Package activation gate {GateType} blocked graph {GraphKey} containing {PackageKeys} before loading: {Reason}")]
    public static partial void PackageActivationGateBlocked(
        this ILogger logger,
        string gateType,
        string graphKey,
        string packageKeys,
        string reason);

    [LoggerMessage(
        EventId = 2109,
        Level = LogLevel.Warning,
        Message = "Package activation gate {GateType} failed while evaluating graph {GraphKey} containing {PackageKeys} and was treated as a block.")]
    public static partial void PackageActivationGateFailed(
        this ILogger logger,
        string gateType,
        string graphKey,
        string packageKeys,
        Exception exception);
}
