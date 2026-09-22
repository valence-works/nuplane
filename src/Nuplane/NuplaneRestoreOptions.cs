using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nuplane.Builder;

namespace Nuplane;

/// <summary>
/// Options for <see cref="NuplaneRestore"/>. Every option is about the one thing a host-free caller
/// cannot inherit from the host it is restoring for: where on disk the store lives, which builder
/// modules the configuration needs, and how loudly to refuse a desired set that is not pinned.
/// </summary>
public sealed class NuplaneRestoreOptions
{
    /// <summary>
    /// Gets or sets the absolute directory that relative configured paths resolve against, and that
    /// the host's default <c>.nuplane</c> layout is anchored to when a path is not configured at
    /// all — normally the content root of the host whose packages are being restored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A running host resolves its defaults against its own process: the install root against
    /// <see cref="AppContext.BaseDirectory"/>, the state file likewise, and relative configured
    /// paths against the current directory. A restoring tool is a different process in a different
    /// directory, so those defaults would silently point at the tool. <see cref="BasePath"/>
    /// replaces them.
    /// </para>
    /// <para>
    /// When neither this nor an explicit override nor a configured absolute path pins the install
    /// root or the state file, <see cref="NuplaneRestore"/> refuses rather than falling back to the
    /// restoring process's own directory — writing a host's packages into a CLI tool's install
    /// directory is exactly the failure that looks like success.
    /// </para>
    /// </remarks>
    public string? BasePath { get; set; }

    /// <summary>
    /// Gets or sets an absolute override for the package install root, taking precedence over
    /// <c>Nuplane:FeedResolution:PackageInstallRoot</c> and over <see cref="BasePath"/>.
    /// </summary>
    public string? InstallRoot { get; set; }

    /// <summary>
    /// Gets or sets an absolute override for the store's state file, taking precedence over
    /// <c>Nuplane:StoreRegistry:StateFilePath</c>, over the <c>Nuplane:Setup:StateFilePath</c>
    /// shorthand, and over <see cref="BasePath"/>.
    /// </summary>
    public string? StateFilePath { get; set; }

    /// <summary>
    /// Gets or sets an absolute override for the package lock file — <c>Nuplane:LockFile:Path</c>,
    /// the file that pins resolved versions in <c>Enforce</c> and <c>Strict</c> modes, not the
    /// store lock <see cref="NuplaneRestore"/> holds while it reconciles.
    /// </summary>
    /// <remarks>
    /// Its configured default is the bare relative name <c>nuplane.lock.json</c>, which a host reads
    /// from its current directory. Without this override a relative value resolves against
    /// <see cref="BasePath"/> when one is given, and otherwise against the directory of the resolved
    /// state file, so it is never read from the restoring process's current directory.
    /// </remarks>
    public string? LockFilePath { get; set; }

    /// <summary>
    /// Gets or sets whether every desired request must name a single version before anything is
    /// acquired. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="true"/> and any request carries a bare identifier, a range, or a floating
    /// version, the restore does nothing: it reports
    /// <see cref="NuplaneRestoreResult.Skipped"/> with
    /// <see cref="NuplaneRestoreSkipReason.UnpinnedRequests"/> and lists the offenders in
    /// <see cref="NuplaneRestoreResult.UnpinnedRequests"/>, before any package is resolved,
    /// downloaded, installed, or written to the store. Use it when the restore has to be
    /// reproducible — a build or deployment step that must install the same versions every time.
    /// </para>
    /// <para>
    /// Contributed requests — the roots a resolved package itself asks for, such as a selected
    /// capability option — are checked <i>in</i> the cycle rather than ahead of it, because the
    /// package that declares one has to be acquired before its declaration can be read. The order
    /// is: every desired request is checked first, and an unpinned one skips the restore outright;
    /// then, inside the cycle, a declaring package is acquired, its contributions are read, and an
    /// unpinned contribution is refused <i>before</i> the contributed package is resolved or
    /// downloaded. Nothing unpinned is ever fetched either way, but an unpinned contribution yields
    /// a degraded restore that did acquire its roots rather than a skip, with the offender in
    /// <see cref="NuplaneRestoreResult.UnpinnedRequests"/> and the package that declared it in
    /// <see cref="NuplaneRestoreResult.FailedPackages"/>.
    /// </para>
    /// </remarks>
    public bool RequirePinnedVersions { get; set; }

    /// <summary>
    /// Gets or sets the logger factory the throwaway composition logs through. When
    /// <see langword="null"/>, the restore emits no logs.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets a callback that runs against the same <see cref="NuplaneBuilder"/> a host's
    /// <c>AddNuplane</c> callback runs against, after configuration has been applied. The second
    /// argument is the already-resolved Nuplane configuration — the <c>Nuplane</c> section, whether
    /// <c>RestoreAsync</c>/<c>DescribeDesiredAsync</c> were given that section directly or the host's
    /// configuration root that nests it.
    /// </summary>
    /// <remarks>
    /// This is how a caller adds the module-owned pieces the core package deliberately does not
    /// know about. Directory-backed feeds are the usual one: core <c>AddNuplane</c> skips every
    /// feed declaring <c>DirectoryPath</c>, so a caller that references
    /// <c>Nuplane.Sources.Directory</c> passes
    /// <c>(builder, configuration) =&gt; builder.AddDirectoryFeedsFromConfiguration(configuration)</c>
    /// here and the same feeds the host would have are registered — without the core package taking a
    /// dependency on that module. It is also where a caller registers an
    /// <c>ISecretReferenceProvider</c> of its own, so a feed can reference a secret that lives
    /// somewhere other than the process environment; the built-in <c>env</c> provider is registered
    /// without any of this and needs no callback at all. Use the callback's own <c>configuration</c> parameter rather than a
    /// configuration captured from the call to <c>RestoreAsync</c>/<c>DescribeDesiredAsync</c>: when
    /// that call was given the host's configuration root, the captured value is still the unresolved
    /// root, and a module registration helper expecting Nuplane's own keys would find none.
    /// </remarks>
    public Action<NuplaneBuilder, IConfiguration>? ConfigureBuilder { get; set; }
}
