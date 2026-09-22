using Nuplane.Abstractions;

namespace Nuplane;

/// <summary>
/// The outcome of a host-free restore: whether it ran, what it could not install, what the store
/// says afterwards, and exactly where on disk it wrote — so a caller can prove which store it
/// populated instead of inferring it from configuration.
/// </summary>
/// <param name="Skipped">
/// Whether the restore declined to run. Read this first: when it is <see langword="true"/> nothing
/// was resolved, acquired, or written, and <paramref name="ActivePackages"/> is empty because no
/// read-back was attempted.
/// </param>
/// <param name="SkipReason">Why the restore declined, or <see cref="NuplaneRestoreSkipReason.None"/> when it ran.</param>
/// <param name="IsDegraded">
/// Whether the cycle completed degraded — a feed outage, a failed package, a blocked cleanup. A
/// degraded restore still wrote what it could; it is reported here, never thrown.
/// </param>
/// <param name="FailedPackages">The identifiers of packages the cycle could not apply.</param>
/// <param name="ActivePackages">
/// The active package set read back from the state file this restore wrote, through the same
/// offline read <c>NuplaneStore.ReadActivePackagesAsync</c> performs — the packages a host reading
/// that store would now see, not the ones this cycle happened to touch.
/// </param>
/// <param name="UnpinnedRequests">
/// The desired requests that name more than one version. Populated only when
/// <see cref="NuplaneRestoreOptions.RequirePinnedVersions"/> refused the restore; empty otherwise.
/// </param>
/// <param name="CredentialRefusedFeeds">
/// The names of configured feeds whose <c>secrets://</c> credential reference could not be resolved —
/// no registered <c>ISecretReferenceProvider</c> claims its provider, the provider holds no value
/// under that name, or reading it failed. These feeds were removed before any network call rather
/// than contacted without credentials, and a package that could only have come from one of them is
/// also reported in <paramref name="FailedPackages"/>. A feed whose reference does resolve is used
/// like any other feed and is not named here.
/// </param>
/// <param name="StateFilePath">The resolved state file this restore wrote.</param>
/// <param name="InstallRoot">The resolved install root this restore extracted packages into.</param>
public sealed record NuplaneRestoreResult(
    bool Skipped,
    NuplaneRestoreSkipReason SkipReason,
    bool IsDegraded,
    IReadOnlyList<string> FailedPackages,
    IReadOnlyList<ActivePackage> ActivePackages,
    IReadOnlyList<DesiredPackageDescription> UnpinnedRequests,
    IReadOnlyList<string> CredentialRefusedFeeds,
    string StateFilePath,
    string InstallRoot);
