using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuplane.Abstractions;

public enum FeedTrustLevel
{
    Trusted,
    Restricted,
    Untrusted
}

public enum FeedOverrideScope
{
    None,
    Package,
    FeedRule
}

public enum PackageUpdatePolicy
{
    Exact,
    Range
}

public sealed record FeedDefinition(
    string Name,
    Uri ServiceIndex,
    FeedTrustLevel TrustLevel,
    string? Credentials = null);

public sealed record UntrustedFeedOverride(
    FeedOverrideScope Scope,
    string Target,
    string Reason);

public sealed record PackageLockEntry(
    string Id,
    string Version,
    string Feed,
    string Hash,
    DateTimeOffset Timestamp);

public sealed record PackageLockFile(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PackageLockEntry> Packages);

public sealed record PackageRequest(
    string Id,
    string VersionRange,
    string? FeedName,
    PackageUpdatePolicy UpdatePolicy,
    string SourceName);

public sealed record ResolvedPackage(
    string Id,
    string Version,
    string FeedName,
    string InstallPath,
    DateTimeOffset InstalledAt,
    string SourceName = "");

public sealed record PackageChangeSet(
    IReadOnlyList<ResolvedPackage> Added,
    IReadOnlyList<ResolvedPackage> Updated,
    IReadOnlyList<string> Removed,
    string CorrelationId,
    DateTimeOffset Timestamp);

public interface IDesiredPackageSource
{
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct);
}

public interface INuplaneObserver
{
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct);

    Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct);

    Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct);
}
