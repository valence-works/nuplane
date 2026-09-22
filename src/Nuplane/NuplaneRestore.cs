using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Restore;
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>
/// Dependency-injection-free entry point that populates a host's package install root and store
/// from that host's own configuration, without starting the host. It is the writing half of the
/// host-free trio: <see cref="NuplaneStore"/> reads the store, <c>NuplaneHostIntegratedLoader</c>
/// loads what the store records, and this restores what the configuration asks for.
/// </summary>
/// <remarks>
/// <para>
/// The intended caller is out-of-process tooling — a deployment step, a CLI, a container build —
/// that has the host's <c>appsettings</c> and its directories, but must not start the host to get
/// its packages on disk. Composing this by hand means encoding six pieces of Nuplane's internals in
/// the caller; this entry point owns them instead:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Nothing is loaded.</b> The composition never registers the loading module's auto-loading
/// observer, which is the only thing that turns a reconciled package into a loaded assembly. No
/// assembly from a restored package enters the process.
/// </description></item>
/// <item><description>
/// <b>No host is started.</b> Exactly one reconciliation cycle is run directly through
/// <see cref="IReconciliationService"/>. The queue-and-wait ingress a host uses is never touched,
/// because only the dispatcher hosted service completes it, so a request made through it without a
/// started host would never return.
/// </description></item>
/// <item><description>
/// <b>Paths are pinned, never defaulted to this process.</b> The install root, the state file, and
/// the package lock file are resolved from <see cref="NuplaneRestoreOptions"/> and the
/// configuration, and reported in the result. A path that nothing pins is refused rather than
/// resolved against the restoring tool's own directory; see
/// <see cref="NuplaneRestoreOptions.BasePath"/>.
/// </description></item>
/// <item><description>
/// <b>Failures are reported, not thrown.</b> A degraded cycle, a package that could not be
/// acquired, a feed whose credential reference could not be resolved, and a store another process is
/// already reconciling are all fields on <see cref="NuplaneRestoreResult"/>. Only a malformed request — a
/// null argument, a relative override, a path nothing pins, in-memory persistence, invalid
/// configuration — throws.
/// </description></item>
/// <item><description>
/// <b>Module feeds come from the caller.</b> Core <c>AddNuplane</c> skips every configured feed that
/// declares a directory path, because directory feeds belong to <c>Nuplane.Sources.Directory</c>.
/// <see cref="NuplaneRestoreOptions.ConfigureBuilder"/> is how a caller that references that module
/// adds them, without the core package depending on it — its second argument is the resolved
/// Nuplane configuration a module registration helper such as
/// <c>AddDirectoryFeedsFromConfiguration</c> expects, whether the caller passed the root or the
/// section to the entry point itself.
/// </description></item>
/// <item><description>
/// <b>Either configuration shape is accepted.</b> The <c>configuration</c> parameter on
/// <see cref="RestoreAsync"/> and <see cref="DescribeDesiredAsync"/> may be the host's configuration
/// root — the one that nests Nuplane's own keys under a <c>Nuplane</c> section, beside the host's
/// other sections — or that <c>Nuplane</c> section itself. Whichever is given, if it has a child
/// section named <c>Nuplane</c> that exists, that section is what actually gets composed; otherwise
/// the value is used as given. <c>AddNuplane</c> itself is never handed an unresolved root: a host
/// that calls it directly still resolves its own <c>Nuplane</c> section first, exactly as before.
/// </description></item>
/// <item><description>
/// <b>An empty composition is refused.</b> A configuration that, once composed, names no feed and no
/// desired package source — because neither accepted shape was actually present, or because the
/// resolved <c>Nuplane</c> section genuinely configures nothing — is refused as malformed rather than
/// reported as a restore that quietly did nothing. A configuration whose only feed was refused for
/// an unresolvable credential reference does not trip this: that outcome is reported on the result
/// instead.
/// </description></item>
/// <item><description>
/// <b>Pinned-ness is answerable.</b> The include-pattern parser and version-request classifier that
/// decide whether a request names exactly one version are internal;
/// <see cref="DescribeDesiredAsync"/> and
/// <see cref="NuplaneRestoreOptions.RequirePinnedVersions"/> expose their answer.
/// </description></item>
/// </list>
/// <para>
/// <b>Write set.</b> A restore writes only under the resolved install root (extracted packages and
/// its <c>.tmp</c> staging directory), the resolved state file, and the store lock file beside it.
/// It never deletes an installed package: cleanup during a cycle records decisions and removes
/// nothing from disk.
/// </para>
/// <para>
/// <b>Concurrency.</b> Every cycle — a host's and a restore's alike — holds an exclusive lock on the
/// store it writes for the duration of the cycle, so two processes never interleave their
/// read-modify-write of one <c>store-state.json</c>. A restore that cannot take the lock reports
/// <see cref="NuplaneRestoreSkipReason.StoreLockUnavailable"/> and does nothing, rather than waiting
/// or writing. See <c>ReconciliationOptions.EnableStoreLock</c>.
/// </para>
/// </remarks>
public static class NuplaneRestore
{
    /// <summary>
    /// Runs exactly one reconciliation cycle against the store the supplied configuration and
    /// <paramref name="options"/> resolve to, then disposes everything it composed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cycle is the host's own: the same desired sources, the same feed resolution, the same
    /// transactional apply, the same last-known-good bookkeeping, the same state persistence. What
    /// differs from a running host is only what is absent — no hosted services, no polling, no
    /// directory watchers, no loading, and no assembly ever entering this process.
    /// </para>
    /// <para>
    /// <see cref="NuplaneRestoreResult.ActivePackages"/> is read back from the state file after the
    /// cycle, through the same offline read <see cref="NuplaneStore.ReadActivePackagesAsync(string, CancellationToken)"/>
    /// performs, so it reports what a host reading that store would now see. Because that read is
    /// not inside the cycle's store lock, a host that starts rewriting the same store in the
    /// meantime can make it observe a sharing violation or torn JSON, exactly as documented on
    /// <see cref="NuplaneStore"/>; both are transient and safe to retry.
    /// </para>
    /// <para>
    /// Calling this twice over an unchanged configuration is idempotent: the second cycle finds
    /// every package already installed, adds nothing, and persists the same active set.
    /// </para>
    /// </remarks>
    /// <param name="configuration">
    /// The host's configuration root — the one that nests Nuplane's own keys under a <c>Nuplane</c>
    /// section — or that <c>Nuplane</c> section itself, the same value the host passes to
    /// <c>AddNuplane</c>. Whichever is given, a child section named <c>Nuplane</c> is used when one
    /// exists; otherwise the value is used as given.
    /// </param>
    /// <param name="options">The restore options, or <see langword="null"/> to use configuration alone, which requires every path to be configured absolutely.</param>
    /// <param name="cancellationToken">A token to cancel the cycle. Cancellation releases the store lock and surfaces as an <see cref="OperationCanceledException"/>, never as a failed package.</param>
    /// <returns>What the cycle did, what it could not do, the store's active package set afterwards, and the resolved paths it wrote to.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when an absolute override in <paramref name="options"/> is not an absolute path.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the configuration selects in-memory persistence, when the install root or state file is neither configured absolutely, nor overridden, nor resolvable against <see cref="NuplaneRestoreOptions.BasePath"/>, or when the resolved configuration names no feed and no desired package source at all.</exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">Thrown when the configuration fails Nuplane's own options validation, exactly as it would when a host starts.</exception>
    /// <exception cref="IOException">Thrown when the state file written by the cycle cannot be read back, for example while another process holds it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the state file read back after the cycle is torn or otherwise not valid JSON.</exception>
    public static async Task<NuplaneRestoreResult> RestoreAsync(
        IConfiguration configuration,
        NuplaneRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= new NuplaneRestoreOptions();

        await using var composition = await RestoreComposition.Create(configuration, options).ConfigureAwait(false);

        if (options.RequirePinnedVersions)
        {
            var desired = await composition.DescribeDesiredAsync(cancellationToken).ConfigureAwait(false);
            var unpinned = desired.Requests.Where(static request => !request.IsPinned).ToArray();
            if (unpinned.Length > 0)
            {
                return Skipped(composition, NuplaneRestoreSkipReason.UnpinnedRequests, unpinned);
            }
        }

        var run = await composition.Services.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), cancellationToken)
            .ConfigureAwait(false);

        if (run is { Skipped: true, SkipReason: ReconciliationSkipReason.StoreLockUnavailable })
        {
            return Skipped(composition, NuplaneRestoreSkipReason.StoreLockUnavailable, []);
        }

        var activePackages = await NuplaneStore
            .ReadActivePackagesAsync(composition.StateFilePath, cancellationToken)
            .ConfigureAwait(false);

        return new(
            Skipped: false,
            NuplaneRestoreSkipReason.None,
            run.IsDegraded,
            run.FailedPackages,
            activePackages,
            UnpinnedRequests: [],
            composition.CredentialRefusedFeeds,
            composition.StateFilePath,
            composition.InstallRoot);
    }

    /// <summary>
    /// Reports what <see cref="RestoreAsync"/> would ask for, and where it would put it, without
    /// doing any of it: a pre-flight that writes nothing and contacts no remote feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It composes the same provider a restore composes — so the same refusals apply, and the
    /// resolved paths it reports are the ones a restore would write — and then reads the desired
    /// requests through the same sources and the same aggregator a reconciliation cycle reads them
    /// with. It does not resolve versions, contact feeds, acquire packages, read the store, or
    /// persist a source snapshot; a cycle's desired-state middleware does persist snapshots, which
    /// is why this reads the sources directly instead.
    /// </para>
    /// <para>
    /// <b>What it touches.</b> Local disk only, and only where a desired source already lives: a
    /// directory-backed feed enumerates its own <c>.nupkg</c> files, and a convergence manifest
    /// source reads its manifest file. A remote feed's requests come from its configured include
    /// patterns alone — those sources run in direct mode and perform no I/O at all — so no service
    /// index, version list, or package is ever fetched.
    /// </para>
    /// <para>
    /// A source that throws while being read contributes no requests and is reported in
    /// <see cref="NuplaneDesiredDescription.SourceErrors"/> rather than propagating, so an empty
    /// request list with an error in it means "could not tell", not "nothing is desired".
    /// </para>
    /// <para>
    /// <see cref="NuplaneDesiredDescription.CapabilitySelections"/> reports the host's configured
    /// capability selections, not the roots they would contribute: a capability's injected root only
    /// exists once a reconciliation cycle has read the declaring package's <c>nuplane.json</c> from a
    /// resolved install path, which this pre-flight — resolving no package — cannot do. Contributed
    /// roots appear only in a cycle, never in this description.
    /// </para>
    /// </remarks>
    /// <param name="configuration">
    /// The host's configuration root — the one that nests Nuplane's own keys under a <c>Nuplane</c>
    /// section — or that <c>Nuplane</c> section itself. Whichever is given, a child section named
    /// <c>Nuplane</c> is used when one exists; otherwise the value is used as given.
    /// </param>
    /// <param name="options">The restore options, or <see langword="null"/>. Only the path, builder, and logging options matter here; <see cref="NuplaneRestoreOptions.RequirePinnedVersions"/> does not, because this method reports pinned-ness rather than acting on it.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The desired requests with their pinned-ness, the feeds refused for an unresolvable credential reference, any source read errors, the resolved state file and install root, and the host's configured capability selections.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when an absolute override in <paramref name="options"/> is not an absolute path.</exception>
    /// <exception cref="InvalidOperationException">Thrown for the same unresolvable-path, in-memory-persistence, and empty-composition reasons as <see cref="RestoreAsync"/>, so a pre-flight catches them before the restore does.</exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">Thrown when the configuration fails Nuplane's own options validation.</exception>
    public static async Task<NuplaneDesiredDescription> DescribeDesiredAsync(
        IConfiguration configuration,
        NuplaneRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await using var composition = await RestoreComposition.Create(configuration, options ?? new NuplaneRestoreOptions()).ConfigureAwait(false);

        return await composition.DescribeDesiredAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The result of a restore that deliberately did nothing. The active package set is left empty
    /// rather than read back, because no cycle ran and whoever does hold the store may be rewriting
    /// its state file right now.
    /// </summary>
    private static NuplaneRestoreResult Skipped(
        RestoreComposition composition,
        NuplaneRestoreSkipReason reason,
        IReadOnlyList<DesiredPackageDescription> unpinnedRequests) =>
        new(
            Skipped: true,
            reason,
            IsDegraded: false,
            FailedPackages: [],
            ActivePackages: [],
            unpinnedRequests,
            composition.CredentialRefusedFeeds,
            composition.StateFilePath,
            composition.InstallRoot);
}
