using System.Text.Json.Serialization;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Admin;

/// <summary>Specifies how many unprotected inactive version groups to retain per package.</summary>
/// <param name="KeepNewestInactiveVersionsPerPackage">A nonnegative count; equivalent versions across feeds form one group.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRetentionPolicy(int KeepNewestInactiveVersionsPerPackage);

/// <summary>Distinguishes a descriptive preview from explicitly requested physical execution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageStorePruneMode>))]
public enum PackageStorePruneMode
{
    /// <summary>Observes and plans without deleting package files.</summary>
    Preview,
    /// <summary>Replans under retained ownership and executes freshly eligible candidates.</summary>
    Execute
}

/// <summary>Binds explicit execution intent to a configured label, physical root, epoch and retention policy.</summary>
/// <param name="RootLabel">The configured root label, never a filesystem path.</param>
/// <param name="RootIdentity">The observed physical root identity; this value grants no filesystem authority.</param>
/// <param name="EnrollmentEpoch">The expected complete enrollment epoch.</param>
/// <param name="Retention">The confirmed retention policy; null retains all installs.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStorePruneConfirmation(
    string RootLabel,
    PhysicalRootIdentity RootIdentity,
    long EnrollmentEpoch,
    PackageStoreRetentionPolicy? Retention);

/// <summary>Requests manual maintenance without supplying candidate or root paths.</summary>
/// <param name="RootLabel">The configured root label.</param>
/// <param name="Retention">The retention policy; null retains all installs.</param>
/// <param name="Mode">Defaults to Preview. Execute requires a matching explicit confirmation.</param>
/// <param name="Confirmation">Expected physical root, epoch, label and retention, required for Execute.</param>
/// <param name="CorrelationId">Optional diagnostic correlation only; never authorizes execution.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStorePruneRequest(
    string RootLabel = "default",
    PackageStoreRetentionPolicy? Retention = null,
    PackageStorePruneMode Mode = PackageStorePruneMode.Preview,
    PackageStorePruneConfirmation? Confirmation = null,
    string? CorrelationId = null);
