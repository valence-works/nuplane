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
        Message = "Ignored invalid package load metadata for {PackageId}@{Version} in graph {GraphKey}: {Diagnostic}")]
    public static partial void InvalidPackageLoadMetadata(
        this ILogger logger,
        string packageId,
        string version,
        string graphKey,
        string diagnostic);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Information,
        Message = "Suppressed package load metadata for {PackageId}@{Version} in graph {GraphKey} because an explicit package override was configured.")]
    public static partial void PackageLoadMetadataSuppressed(
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
}
