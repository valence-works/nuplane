using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Resolves configured store locators component by component using metadata-only held handles.</summary>
/// <remarks>
/// Resolution returns a retained structural membership candidate, never operation admission. It does not
/// normalize paths, read package/state/archive payloads, create entries, acquire locks, or enumerate package/content directories.
/// </remarks>
internal sealed class PackageStoreAuthorityResolver
{
    private const int MaximumPathLength = 32768;
    private const int MaximumPathComponents = 4096;
    private const int MaximumAliasExpansions = 40;
    private const int MaximumEvidenceItems = 16384;
    private const string ControlDirectoryName = RootMembershipRegistry.ControlDirectoryName;
    private const string LedgerName = RootMembershipRegistry.LedgerName;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly RootMembershipRegistry _registry;

    internal PackageStoreAuthorityResolver(IPhysicalStoreFileSystem files, RootMembershipRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem ?? throw Refusal(
            PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem provider cannot establish stable native directory-name semantics.");
        _registry = registry;
    }

    /// <summary>Resolves one exact configured locator without lexical normalization or payload access.</summary>
    /// <param name="exactLocator">The configured absolute locator, or a relative locator paired with its captured absolute base.</param>
    /// <param name="target">The required final object kind and final-link policy.</param>
    /// <param name="requiredRoot">An optional physical root that every observed authority must match.</param>
    /// <param name="exactBaseLocator">The exact captured absolute base for a relative locator.</param>
    /// <returns>An owner of the final handle and all metadata evidence needed for revalidation.</returns>
    /// <exception cref="PackageStoreAdmissionException">The path or authority is missing, ambiguous, unsafe, or unsupported.</exception>
    internal ResolvedPackageStorePath Resolve(
        string exactLocator,
        PhysicalStorePathTarget target,
        PhysicalRootIdentity? requiredRoot = null,
        string? exactBaseLocator = null)
    {
        ArgumentNullException.ThrowIfNull(exactLocator);
        if (string.IsNullOrWhiteSpace(exactLocator))
            throw Unknown("The configured locator is blank.");
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));

        var path = ParseRequest(exactLocator, exactBaseLocator);
        return ResolveParsed(path, target, requiredRoot, memberLocatorScope: null);
    }

    /// <summary>Resolves an unscoped package probe with a precise signal for a concurrently-created missing directory.</summary>
    /// <remarks>
    /// The caller may reclassify the entire locator before any callback when a stable ordinary directory appears
    /// at its first previously-missing edge. All other path, alias, profile, authority, and ledger changes remain
    /// ordinary typed refusals. The returned result still uses strict replay unless the caller explicitly requests
    /// the same pre-callback classification replay.
    /// </remarks>
    internal ResolvedPackageStorePath ResolveForUnenrolledPackageProbe(
        string exactLocator,
        string? exactBaseLocator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactLocator);
        var path = ParseRequest(exactLocator, exactBaseLocator);
        return ResolveParsed(path, PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix,
            requiredRoot: null, memberLocatorScope: null,
            allowStableMissingDirectoryRetry: true);
    }

    /// <summary>Resolves a directory-backed desired source under positive Unenrolled or supplied exact root authority.</summary>
    internal ResolvedPackageStorePath ResolveDesiredSourceDirectory(
        string exactLocator,
        RootMembershipRegistry.MemberLocatorReplayScope? scope,
        string? exactBaseLocator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactLocator);
        scope?.EnsureActive();
        if (scope is { Status: not RootMembershipStatus.Complete })
            throw Refusal(PackageStoreAdmissionReason.IncompleteEnrollment,
                "A directory desired source requires a Complete root membership snapshot.", scope.RootIdentity);

        var path = ParseRequest(exactLocator, exactBaseLocator);
        return ResolveParsed(path, PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix,
            requiredRoot: scope?.RootIdentity, memberLocatorScope: scope);
    }

    /// <summary>Replays one persisted absolute member-state locator under an active registry lock scope.</summary>
    /// <remarks>Only the parent path is expanded. The final state entry is observed no-follow as metadata.</remarks>
    internal ResolvedMemberStateLocation ResolveMemberStateLocation(
        string exactLocator,
        RootMembershipRegistry.MemberLocatorReplayScope scope)
    {
        ArgumentNullException.ThrowIfNull(exactLocator);
        ArgumentNullException.ThrowIfNull(scope);
        scope.EnsureActive();
        if (string.IsNullOrWhiteSpace(exactLocator))
            throw Unknown("A persisted member locator is blank.");

        // Persisted hints must already be fully qualified. Relative runtime configuration needs to be
        // captured before declaration; replay never consults the current working directory.
        var path = ParseRequest(exactLocator, exactBaseLocator: null);
        if (path.Anchor is null || path.Components.Length == 0)
            throw Unknown("A persisted member locator must be fully qualified and name a final state file.", scope.RootIdentity);

        var requestedBasename = path.Components[^1];
        if (requestedBasename is "." or "..")
            throw Unknown("A member state locator must end in one ordinary file name.", scope.RootIdentity);
        try
        {
            PhysicalStoreNames.ValidateSingleComponent(requestedBasename);
            ValidateComponentEncoding(requestedBasename);
        }
        catch (ArgumentException exception)
        {
            throw Unknown("A member state locator has an unsupported final name.", scope.RootIdentity, exception);
        }

        var parentPath = new ParsedPath(path.Anchor, path.Components[..^1]);
        var parentResolution = ResolveParsed(parentPath, PhysicalStorePathTarget.ConfiguredRootDirectory,
            requiredRoot: null, memberLocatorScope: scope);
        try
        {
            scope.EnsureActive();
            var parent = parentResolution.Target as PhysicalStoreDirectoryHandle
                ?? throw Unknown("A member state locator did not resolve to a held parent directory.", scope.RootIdentity);
            var parentBefore = _files.InspectHandle(parent);
            if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A member state locator parent is not a directory.", scope.RootIdentity);

            var entry = _files.InspectChildNoFollow(parent, requestedBasename);
            if (entry is null)
            {
                var semantics = _names.ObserveDirectoryNameSemantics(parent);
                var parentAfter = _files.InspectHandle(parent);
                if (parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
                    _files.InspectChildNoFollow(parent, requestedBasename) is not null)
                {
                    throw Unknown("A prospective member state slot changed during native metadata observation.", scope.RootIdentity);
                }

                var slot = new StateSlotIdentity(parentBefore.Identity, semantics, requestedBasename);
                parentResolution.Revalidate();
                return new ResolvedMemberStateLocation(_files, _names, scope, parentResolution,
                    requestedBasename, slot, existingFileIdentity: null);
            }

            if (entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1)
                throw Unknown("A member state slot must be a regular single-link file without a final alias.", scope.RootIdentity);

            var observed = new PhysicalStoreIdentity(_files).ObserveStateSlot(parent, requestedBasename);
            if (observed.FileIdentity != entry.Identity || observed.Slot.ParentIdentity != parentBefore.Identity)
                throw Unknown("The member state slot changed during native canonical-name observation.", scope.RootIdentity);
            parentResolution.Revalidate();
            return new ResolvedMemberStateLocation(_files, _names, scope, parentResolution,
                requestedBasename, observed.Slot, observed.FileIdentity);
        }
        catch
        {
            parentResolution.Dispose();
            throw;
        }
    }

    /// <summary>Resolves a protected install path while the caller holds the exact root/member replay scope.</summary>
    /// <remarks>This metadata-only enrollment/verifier seam grants no ordinary package access.</remarks>
    internal ResolvedPackageStorePath ResolveProtectedInstallPath(
        string exactLocator,
        RootMembershipRegistry.MemberLocatorReplayScope scope)
    {
        ArgumentNullException.ThrowIfNull(exactLocator);
        ArgumentNullException.ThrowIfNull(scope);
        scope.EnsureActive();
        var path = ParseRequest(exactLocator, exactBaseLocator: null);
        return ResolveParsed(path, PhysicalStorePathTarget.PackageDirectory, scope.RootIdentity, scope);
    }

    /// <summary>Replays an exact install path for a graph whose immutable use lease is already live.</summary>
    /// <remarks>
    /// A retained reader relies on the membership and complete graph verified when its lease was published.
    /// Another coordinated state publication may temporarily expose a pending ledger, so this native path
    /// replay verifies the required physical root and every no-follow path edge without requiring a fresh
    /// Complete/non-pending membership snapshot or acquiring root/member locks.
    /// </remarks>
    internal ResolvedPackageStorePath ResolveRetainedInstallPath(
        string exactLocator,
        PackageInstallIdentity expectedInstall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactLocator);
        ArgumentNullException.ThrowIfNull(expectedInstall);
        var path = ParseRequest(exactLocator, exactBaseLocator: null);
        return ResolveParsed(path, PhysicalStorePathTarget.PackageDirectory, expectedInstall.Root,
            memberLocatorScope: null, retainedInstall: expectedInstall);
    }

    private ResolvedPackageStorePath ResolveParsed(
        ParsedPath path,
        PhysicalStorePathTarget target,
        PhysicalRootIdentity? requiredRoot,
        RootMembershipRegistry.MemberLocatorReplayScope? memberLocatorScope,
        PackageInstallIdentity? retainedInstall = null,
        bool allowStableMissingDirectoryRetry = false)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        memberLocatorScope?.EnsureActive();
        var state = new ResolutionState(this, requiredRoot, memberLocatorScope, retainedInstall);
        try
        {
            var anchor = OpenAnchor(state, path.Anchor
                ?? throw Unknown("The configured path has no namespace anchor."));
            var current = anchor;
            var frames = new List<ComponentFrame> { new(path.Components) };
            PhysicalStoreFileHandle? finalFile = null;
            PhysicalStoreDirectoryHandle? finalFileParent = null;
            string? finalFileName = null;
            var prospectiveMissingSuffix = false;

            while (frames.Count > 0)
            {
                var frame = frames[^1];
                if (frame.Index == frame.Components.Length)
                {
                    frames.RemoveAt(frames.Count - 1);
                    if (frame.Alias is { } completedAlias)
                    {
                        state.ActiveAliases.Remove(completedAlias.Identity);
                        if (frame.RequireBeneathOnCompletion)
                            RequireAliasDestinationInsideAuthority(state, finalFileParent ?? current);
                    }
                    continue;
                }

                state.ObserveDirectory(current);
                state.ObserveReservedAuthority(current);
                var component = frame.Components[frame.Index++];

                if (component == ".")
                    continue;

                if (component == "..")
                {
                    var absoluteAliasPrefix = frames.Any(static candidate => candidate.AbsoluteAliasScope);
                    var currentAuthority = state.AuthorityRootIdentity?.HandleIdentity;
                    var currentlyInsideAuthority = currentAuthority is not null && IsBeneath(state, current, currentAuthority);
                    var parent = OpenParent(state, current);
                    current = parent;
                    if (state.AuthorityRootIdentity is not null &&
                        (!absoluteAliasPrefix || currentlyInsideAuthority))
                    {
                        RequireDirectoryInsideAuthority(state, current);
                    }
                    continue;
                }

                try { PhysicalStoreNames.ValidateSingleComponent(component); }
                catch (ArgumentException exception)
                {
                    throw Unknown("A configured path component is not one supported native name.", state.RootIdentity, exception);
                }
                ValidateComponentEncoding(component);
                var entry = _files.InspectChildNoFollow(current, component);
                if (entry is null)
                {
                    if (target is not (PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix or
                            PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix or
                            PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix or
                            PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix) ||
                        (state.RootIdentity is not null &&
                            target is not (PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix or
                                PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix)) ||
                        (state.RootIdentity is null &&
                            target == PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix) ||
                        (target == PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix &&
                            state.RootIdentity is not null &&
                            (state.MemberLocatorScope is null || state.RequiredRoot != state.RootIdentity)) ||
                        frames.Any(static candidate => candidate.Alias is not null) ||
                        !TryGetOrdinaryRemainingSuffix(frames, out var remainingSuffix))
                    {
                        throw Unknown("The configured path contains a missing component.", state.RootIdentity);
                    }

                    state.RecordMissingSuffix(current, component, remainingSuffix, target,
                        allowStableMissingDirectoryRetry);
                    prospectiveMissingSuffix = true;
                    break;
                }

                if (entry.Kind == PhysicalStoreEntryKind.SymbolicLink)
                {
                    var terminalLink = !HasMeaningfulRemainingComponents(frames);
                    if (terminalLink && !AllowsConfiguredRootAlias(target))
                    {
                        throw Unknown("A final package, member, content, or archive link is not an accepted target.", state.RootIdentity);
                    }

                    var alias = state.ReadAlias(current, component, entry.Identity);
                    if (!state.ActiveAliases.Add(alias.Identity) || ++state.AliasExpansions > MaximumAliasExpansions)
                        throw Unknown("The configured path contains a symbolic-link cycle or exceeds the bounded expansion limit.", state.RootIdentity);

                    var aliasTarget = ParseAliasTarget(alias.Target);
                    var sourceAuthority = state.AuthorityRootIdentity?.HandleIdentity;
                    var sourceWasInsideAuthority = sourceAuthority is not null && IsBeneath(state, current, sourceAuthority);
                    var alreadyResolvingAliasExpansion = frames.Any(static candidate => candidate.Alias is not null);
                    if (aliasTarget.Anchor is not null)
                    {
                        current = OpenAnchor(state, aliasTarget.Anchor);
                    }

                    frames.Add(new ComponentFrame(
                        aliasTarget.Components,
                        alias,
                        requireBeneathOnCompletion: sourceWasInsideAuthority ||
                            (aliasTarget.Anchor is not null && !alreadyResolvingAliasExpansion),
                        absoluteAliasScope: aliasTarget.Anchor is not null));
                    continue;
                }

                var hasAnyRemaining = HasAnyRemainingComponents(frames);
                if (entry.Kind == PhysicalStoreEntryKind.Directory)
                {
                    var child = _files.OpenDirectoryChildNoFollow(current, component);
                    state.Track(child);
                    var opened = _files.InspectHandle(child);
                    if (opened.Kind != PhysicalStoreEntryKind.Directory || opened.Identity != entry.Identity)
                        throw Unknown("A configured directory changed between no-follow inspection and held open.", state.RootIdentity);
                    state.RecordChildEdge(current, component, child, entry.Identity);
                    current = child;
                    continue;
                }

                if (entry.Kind == PhysicalStoreEntryKind.RegularFile && !hasAnyRemaining && target == PhysicalStorePathTarget.ArchiveFile)
                {
                    var file = _files.OpenFileChildNoFollow(current, component, FileAccess.Read);
                    state.Track(file);
                    var opened = _files.InspectHandle(file);
                    if (opened.Kind != PhysicalStoreEntryKind.RegularFile || opened.LinkCount != 1 || opened.Identity != entry.Identity)
                        throw Unknown("An archive target is not one stable regular file without hard-link ambiguity.", state.RootIdentity);
                    state.RecordFileEdge(current, component, file, entry.Identity);
                    finalFile = file;
                    finalFileParent = current;
                    finalFileName = component;
                    continue;
                }

                throw Unknown("The configured path component has an unexpected or unsupported kind.", state.RootIdentity);
            }

            if (finalFile is null)
            {
                state.ObserveDirectory(current);
                state.ObserveReservedAuthority(current);
                if (target == PhysicalStorePathTarget.ArchiveFile)
                    throw Unknown("The configured archive target is not a regular file.", state.RootIdentity);
            }
            else if (target != PhysicalStorePathTarget.ArchiveFile)
            {
                throw Unknown("The configured directory target is a regular file.", state.RootIdentity);
            }

            var finalTarget = (PhysicalStoreHandle?)finalFile ?? current;
            if (retainedInstall is not null)
            {
                if (finalTarget is not PhysicalStoreDirectoryHandle installDirectory)
                    throw Unknown("The retained graph-use target is not a package directory.", retainedInstall.Root);
                state.BindRetainedInstallRoot(installDirectory, retainedInstall);
            }
            if (requiredRoot is not null && state.RootIdentity != requiredRoot)
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The configured path did not resolve to the required physical authority root.", requiredRoot);

            if (state.AuthorityRootIdentity is not null)
                RequireTargetInsideAuthority(state, finalTarget, finalFileParent);

            state.Revalidate(finalTarget, target, finalFileParent,
                allowStableMissingDirectoryRetry);
            var transferredHandles = state.SnapshotHandles();
            var result = new ResolvedPackageStorePath(
                finalTarget,
                state.AuthorityRoot,
                state.RootIdentity,
                state.MembershipCandidate,
                state.MembershipLedgerIdentity,
                transferredHandles,
                allowMissingDirectoryRetry => state.Revalidate(finalTarget, target, finalFileParent,
                    allowMissingDirectoryRetry),
                targetParent: finalFileParent,
                targetName: finalFileName,
                isProspectiveConfiguredRoot: prospectiveMissingSuffix &&
                    target == PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix,
                isProspectiveMissingSuffix: prospectiveMissingSuffix,
                isRetainedGraphUseRoot: retainedInstall is not null);
            state.DetachHandles();
            return result;
        }
        catch
        {
            state.DisposeHandles();
            throw;
        }
    }

    private PhysicalStoreDirectoryHandle OpenAnchor(ResolutionState state, string anchor)
    {
        var directory = _files.OpenNamespaceRoot(anchor);
        state.Track(directory);
        var info = _files.InspectHandle(directory);
        if (info.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("A configured namespace anchor is not a directory.", state.RootIdentity);
        state.RecordAnchor(anchor, directory, info.Identity);
        return directory;
    }

    private PhysicalStoreDirectoryHandle OpenParent(ResolutionState state, PhysicalStoreDirectoryHandle child)
    {
        var childInfo = _files.InspectHandle(child);
        if (childInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("A parent transition started from a non-directory.", state.RootIdentity);

        var parent = _files.OpenParentDirectory(child);
        state.Track(parent);
        var parentInfo = _files.InspectHandle(parent);
        if (parentInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("A configured parent component did not resolve to a directory.", state.RootIdentity);
        state.RecordParentEdge(child, childInfo.Identity, parent, parentInfo.Identity);
        return parent;
    }

    private void RequireDirectoryInsideAuthority(ResolutionState state, PhysicalStoreDirectoryHandle directory)
    {
        var requiredIdentity = state.AuthorityRootIdentity?.HandleIdentity;
        if (requiredIdentity is not null && !IsBeneath(state, directory, requiredIdentity))
            throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                "The configured path explicitly escaped an observed physical authority root.", state.RootIdentity ?? state.RequiredRoot);
    }

    private void RequireAliasDestinationInsideAuthority(ResolutionState state, PhysicalStoreDirectoryHandle directory)
    {
        var requiredIdentity = state.AuthorityRootIdentity?.HandleIdentity;
        if (requiredIdentity is not null && !IsBeneath(state, directory, requiredIdentity))
            throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                "A configured alias resolved outside the observed physical authority root.", state.RootIdentity ?? state.RequiredRoot);
    }

    private void RequireTargetInsideAuthority(
        ResolutionState state,
        PhysicalStoreHandle target,
        PhysicalStoreDirectoryHandle? targetParent)
    {
        var requiredIdentity = state.AuthorityRootIdentity?.HandleIdentity;
        if (requiredIdentity is null)
            return;

        if (target is PhysicalStoreDirectoryHandle directory)
        {
            if (!IsBeneath(state, directory, requiredIdentity))
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The configured directory target is outside the observed physical authority root.", state.RootIdentity ?? state.RequiredRoot);
            return;
        }

        if (targetParent is null || !IsBeneath(state, targetParent, requiredIdentity))
            throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                "The configured archive target is outside the observed physical authority root.", state.RootIdentity ?? state.RequiredRoot);
    }

    private bool IsBeneath(ResolutionState state, PhysicalStoreDirectoryHandle directory, PhysicalFileIdentity ancestorIdentity)
    {
        var cursor = directory;
        for (var depth = 0; depth < MaximumPathComponents; depth++)
        {
            state.ObserveDirectory(cursor);
            state.ObserveReservedAuthority(cursor);
            var info = _files.InspectHandle(cursor);
            if (info.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A held ancestry component is no longer a directory.", state.RootIdentity);
            if (info.Identity == ancestorIdentity)
                return true;

            var parent = OpenParent(state, cursor);
            var parentInfo = _files.InspectHandle(parent);
            if (parentInfo.Identity == info.Identity)
                return false;
            cursor = parent;
        }

        throw Unknown("The physical ancestry walk exceeded its bounded depth.", state.RootIdentity);
    }

    private ParsedPath ParseRequest(string exactLocator, string? exactBaseLocator)
    {
        if (exactLocator.Length > MaximumPathLength || exactBaseLocator?.Length > MaximumPathLength)
            throw Unknown("A configured locator exceeds the bounded path length.");

        var requested = ParseAbsoluteOrRelative(exactLocator, allowNtSubstitute: false);
        if (requested.Anchor is not null)
        {
            if (exactBaseLocator is not null)
                throw Unknown("An absolute configured locator cannot also specify a relative base.");
            EnsureComponentLimit(requested.Components);
            return requested;
        }

        if (exactBaseLocator is null)
            throw Unknown("A relative configured locator requires an explicit captured absolute base.");
        var basePath = ParseAbsoluteOrRelative(exactBaseLocator, allowNtSubstitute: false);
        if (basePath.Anchor is null)
            throw Unknown("The explicit base locator must be fully qualified.");
        var combined = basePath.Components.Concat(requested.Components).ToArray();
        EnsureComponentLimit(combined);
        return new ParsedPath(basePath.Anchor, combined);
    }

    private ParsedPath ParseAliasTarget(string target)
    {
        if (target.Length > MaximumPathLength)
            throw Unknown("A symbolic-link target exceeds the bounded path length.");
        var parsed = ParseAbsoluteOrRelative(target, allowNtSubstitute: true);
        EnsureComponentLimit(parsed.Components);
        return parsed;
    }

    private ParsedPath ParseAbsoluteOrRelative(string value, bool allowNtSubstitute)
    {
        if (value.Length == 0 || value.IndexOf('\0') >= 0)
            throw Unknown("A configured path or alias target is empty or contains NUL.");

        if (OperatingSystem.IsWindows())
        {
            if (allowNtSubstitute && value.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
            {
                var substitute = value[4..];
                if (substitute.Contains('/'))
                    throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                        "Windows native alias targets must use their exact backslash-separated spelling.");
                if (substitute.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
                    value = "\\\\?\\" + substitute;
                else if (substitute.Length >= 3 && char.IsAsciiLetter(substitute[0]) &&
                         substitute[1] == ':' && substitute[2] == '\\')
                    value = substitute;
                else
                    throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                        "The Windows native alias target does not use a supported local drive or volume anchor.");
            }

            if (TryParseWindowsAbsolute(value, out var anchor, out var components))
                return new ParsedPath(anchor, components);

            if (value[0] is '\\' or '/' || value.Length >= 2 && value[1] == ':')
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "The Windows locator is not a strict local drive-rooted or supported volume path.");
            return new ParsedPath(null, SplitWindowsComponents(value));
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem, "The current platform has no qualified physical-store path syntax.");
        if (value[0] == '/')
            return new ParsedPath("/", SplitComponents(value, '/', appendTerminalDot: true));
        return new ParsedPath(null, SplitComponents(value, '/', appendTerminalDot: true));
    }

    private static bool TryParseWindowsAbsolute(string value, out string anchor, out string[] components)
    {
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/')
        {
            anchor = value[..2] + "\\";
            components = SplitWindowsComponents(value[3..]);
            return true;
        }

        const string VolumePrefix = "\\\\?\\Volume{";
        if (value.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase) && !value.Contains('/'))
        {
            var close = value.IndexOf('}', VolumePrefix.Length);
            if (close >= 0 && close + 1 < value.Length && value[close + 1] == '\\' &&
                Guid.TryParseExact(value.AsSpan(VolumePrefix.Length, close - VolumePrefix.Length), "D", out _))
            {
                anchor = value[..(close + 2)];
                components = SplitComponents(value[(close + 2)..], '\\', appendTerminalDot: true);
                return true;
            }
        }

        anchor = string.Empty;
        components = [];
        return false;
    }

    private static string[] SplitComponents(string value, char separator, bool appendTerminalDot)
    {
        var components = value.Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (appendTerminalDot && value.Length > 0 && value[^1] == separator)
            components.Add(".");
        return components.ToArray();
    }

    private static string[] SplitWindowsComponents(string value)
    {
        var components = value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (value.Length > 0 && value[^1] is '\\' or '/')
            components.Add(".");
        return components.ToArray();
    }

    private static bool HasMeaningfulRemainingComponents(IReadOnlyList<ComponentFrame> frames)
    {
        for (var frameIndex = frames.Count - 1; frameIndex >= 0; frameIndex--)
        {
            var frame = frames[frameIndex];
            for (var index = frame.Index; index < frame.Components.Length; index++)
            {
                if (frame.Components[index] != ".")
                    return true;
            }
        }
        return false;
    }

    private bool TryGetOrdinaryRemainingSuffix(IReadOnlyList<ComponentFrame> frames, out string[] suffix)
    {
        var remaining = new List<string>();
        foreach (var frame in frames)
        {
            for (var index = frame.Index; index < frame.Components.Length; index++)
            {
                var component = frame.Components[index];
                if (component is "." or "..")
                {
                    suffix = [];
                    return false;
                }

                try
                {
                    PhysicalStoreNames.ValidateSingleComponent(component);
                    ValidateComponentEncoding(component);
                }
                catch (ArgumentException)
                {
                    suffix = [];
                    return false;
                }

                remaining.Add(component);
            }
        }

        suffix = remaining.ToArray();
        return true;
    }

    private static bool AllowsConfiguredRootAlias(PhysicalStorePathTarget target)
        => target is PhysicalStorePathTarget.ConfiguredRootDirectory or
            PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix;

    private static bool HasAnyRemainingComponents(IReadOnlyList<ComponentFrame> frames)
        => frames.Any(frame => frame.Index < frame.Components.Length);

    private static void EnsureComponentLimit(IReadOnlyCollection<string> components)
    {
        if (components.Count > MaximumPathComponents)
            throw Unknown("A configured path exceeds the bounded component count.");
    }

    private void ValidateComponentEncoding(string component)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                _ = StrictUtf16.GetByteCount(component);
            else
                _ = StrictUtf8.GetByteCount(component);
        }
        catch (EncoderFallbackException exception)
        {
            throw Unknown("A configured component has invalid native path encoding.", null, exception);
        }
    }

    private static PackageStoreAdmissionException Unknown(string message, PhysicalRootIdentity? root = null, Exception? innerException = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root, innerException);

    private static PackageStoreAdmissionException Refusal(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity? root = null,
        Exception? innerException = null)
        => new(reason, message, root, innerException);

    private sealed record ParsedPath(string? Anchor, string[] Components);

    private sealed class ComponentFrame(
        string[] components,
        AliasEvidence? alias = null,
        bool requireBeneathOnCompletion = false,
        bool absoluteAliasScope = false)
    {
        internal string[] Components { get; } = components;
        internal AliasEvidence? Alias { get; } = alias;
        internal bool RequireBeneathOnCompletion { get; } = requireBeneathOnCompletion;
        internal bool AbsoluteAliasScope { get; } = absoluteAliasScope;
        internal int Index { get; set; }
    }

    private sealed record AliasEvidence(
        PhysicalStoreDirectoryHandle Parent,
        string Name,
        PhysicalFileIdentity Identity,
        string Target);

    private sealed class ResolutionState(
        PackageStoreAuthorityResolver resolver,
        PhysicalRootIdentity? requiredRoot,
        RootMembershipRegistry.MemberLocatorReplayScope? memberLocatorScope,
        PackageInstallIdentity? retainedInstall)
    {
        private readonly List<PhysicalStoreHandle> _handles = [];
        private readonly List<DirectoryEvidence> _directories = [];
        private readonly List<NamespaceEvidence> _anchors = [];
        private readonly List<ChildEdgeEvidence> _childEdges = [];
        private readonly List<ParentEdgeEvidence> _parentEdges = [];
        private readonly List<AliasEvidence> _aliases = [];
        private readonly List<ControlEvidence> _controls = [];
        private readonly List<MissingSuffixEvidence> _missingSuffixes = [];

        internal PhysicalRootIdentity? RequiredRoot { get; } = requiredRoot;
        internal RootMembershipRegistry.MemberLocatorReplayScope? MemberLocatorScope { get; } = memberLocatorScope;
        internal PackageInstallIdentity? RetainedInstall { get; } = retainedInstall;
        internal PhysicalStoreDirectoryHandle? AuthorityRoot { get; private set; }
        internal PhysicalRootIdentity? RootIdentity { get; private set; }
        internal PhysicalRootIdentity? AuthorityRootIdentity => RootIdentity;
        internal RootMembershipRecord? MembershipCandidate { get; private set; }
        internal PhysicalFileIdentity? MembershipLedgerIdentity { get; private set; }
        internal HashSet<PhysicalFileIdentity> ActiveAliases { get; } = [];
        internal int AliasExpansions { get; set; }

        internal void BindRetainedInstallRoot(
            PhysicalStoreDirectoryHandle installDirectory,
            PackageInstallIdentity expectedInstall)
        {
            if (RetainedInstall is null || !ReferenceEquals(RetainedInstall, expectedInstall))
                throw new InvalidOperationException("A retained install can only be bound by its own path replay.");

            var relativeComponents = expectedInstall.RootRelativeInstallPath.Split('/');
            PhysicalStoreDirectoryHandle cursor = installDirectory;
            for (var index = 0; index < relativeComponents.Length; index++)
            {
                var childInfo = resolver._files.InspectHandle(cursor);
                if (childInfo.Kind != PhysicalStoreEntryKind.Directory)
                    throw Unknown("A retained install ancestry component is not a directory.", expectedInstall.Root);
                var parent = resolver.OpenParent(this, cursor);
                var parentInfo = resolver._files.InspectHandle(parent);
                if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity == childInfo.Identity)
                    throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                        "The retained install path is shallower than its immutable root-relative identity.", expectedInstall.Root);
                RecordParentEdge(cursor, childInfo.Identity, parent, parentInfo.Identity);
                cursor = parent;
            }

            var rootInfo = resolver._files.InspectHandle(cursor);
            if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != expectedInstall.Root.HandleIdentity)
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The retained install path no longer resolves beneath its admitted physical root.", expectedInstall.Root);

            AuthorityRoot = cursor;
            RootIdentity = expectedInstall.Root;
        }

        internal void Track(PhysicalStoreHandle handle)
        {
            if (_handles.Count >= MaximumEvidenceItems)
            {
                handle.Dispose();
                throw Unknown("Configured path resolution exceeded the retained-handle bound.", RootIdentity);
            }
            _handles.Add(handle);
        }

        internal IReadOnlyList<PhysicalStoreHandle> DetachHandles()
        {
            var result = _handles.ToArray();
            _handles.Clear();
            return result;
        }

        internal IReadOnlyList<PhysicalStoreHandle> SnapshotHandles() => _handles.ToArray();

        internal void DisposeHandles()
        {
            for (var index = _handles.Count - 1; index >= 0; index--)
                _handles[index].Dispose();
            _handles.Clear();
        }

        internal void RecordAnchor(string exactAnchor, PhysicalStoreDirectoryHandle directory, PhysicalFileIdentity identity)
        {
            AddEvidence(_anchors, new NamespaceEvidence(exactAnchor, directory, identity));
        }

        internal void RecordChildEdge(
            PhysicalStoreDirectoryHandle parent,
            string name,
            PhysicalStoreDirectoryHandle child,
            PhysicalFileIdentity identity)
        {
            var parentInfo = resolver._files.InspectHandle(parent);
            if (parentInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A configured child edge has a non-directory parent.", RootIdentity);
            AddEvidence(_childEdges, new ChildEdgeEvidence(
                parent, parentInfo.Identity, name, child, identity, PhysicalStoreEntryKind.Directory));
        }

        internal void RecordParentEdge(
            PhysicalStoreDirectoryHandle child,
            PhysicalFileIdentity childIdentity,
            PhysicalStoreDirectoryHandle parent,
            PhysicalFileIdentity parentIdentity)
        {
            AddEvidence(_parentEdges, new ParentEdgeEvidence(child, childIdentity, parent, parentIdentity));
        }

        internal void RecordFileEdge(
            PhysicalStoreDirectoryHandle parent,
            string name,
            PhysicalStoreFileHandle file,
            PhysicalFileIdentity identity)
        {
            var parentInfo = resolver._files.InspectHandle(parent);
            if (parentInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A configured archive edge has a non-directory parent.", RootIdentity);
            AddEvidence(_childEdges, new ChildEdgeEvidence(
                parent, parentInfo.Identity, name, file, identity, PhysicalStoreEntryKind.RegularFile));
        }

        internal void ObserveDirectory(PhysicalStoreDirectoryHandle directory)
        {
            var before = resolver._files.InspectHandle(directory);
            if (before.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A configured path component is not a held directory.", RootIdentity);
            var semantics = resolver._names.ObserveDirectoryNameSemantics(directory);
            var after = resolver._files.InspectHandle(directory);
            if (after.Kind != PhysicalStoreEntryKind.Directory || after.Identity != before.Identity)
                throw Unknown("A held directory changed during native name-profile observation.", RootIdentity);
            AddEvidence(_directories, new DirectoryEvidence(directory, before.Identity, semantics));
        }

        internal void ObserveReservedAuthority(PhysicalStoreDirectoryHandle directory)
        {
            // A live graph-use lease already carries the immutable root/install identities. Re-reading
            // the membership ledger here would turn an unrelated atomic state publication into an
            // authorization failure for a graph that is still protected by its lease.
            if (RetainedInstall is not null)
                return;

            var parentInfo = resolver._files.InspectHandle(directory);
            if (parentInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("The configured path authority check requires a held directory.", RootIdentity);
            var entry = resolver._files.InspectChildNoFollow(directory, ControlDirectoryName);
            if (entry is null)
            {
                AddEvidence(_controls, new ControlEvidence(directory, parentInfo.Identity, null, null, null, null));
                return;
            }

            if (entry.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("A present reserved control name is linked or is not a directory.", RootIdentity);

            var control = resolver._files.OpenDirectoryChildNoFollow(directory, ControlDirectoryName);
            Track(control);
            var controlInfo = resolver._files.InspectHandle(control);
            if (controlInfo.Kind != PhysicalStoreEntryKind.Directory || controlInfo.Identity != entry.Identity)
                throw Unknown("The reserved control directory changed between no-follow inspection and held open.", RootIdentity);
            RecordChildEdge(directory, ControlDirectoryName, control, entry.Identity);

            var ledgerEntry = resolver._files.InspectChildNoFollow(control, LedgerName);
            if (ledgerEntry is null || ledgerEntry.Kind != PhysicalStoreEntryKind.RegularFile || ledgerEntry.LinkCount != 1)
                throw Unknown("A present reserved control directory has no unambiguous membership ledger.", RootIdentity);
            var candidate = resolver._registry.ReadCandidate(directory, ledgerEntry.Identity, out var ledgerIdentity);
            var ledgerAfter = resolver._files.InspectChildNoFollow(control, LedgerName);
            if (ledgerAfter is null || ledgerAfter.Kind != PhysicalStoreEntryKind.RegularFile || ledgerAfter.LinkCount != 1 ||
                ledgerAfter.Identity != ledgerIdentity || ledgerIdentity != ledgerEntry.Identity)
            {
                throw Unknown("The membership ledger changed while its structural candidate was read.", RootIdentity);
            }
            var observedRoot = new PhysicalRootIdentity(parentInfo.Identity);
            ValidateCandidate(observedRoot, candidate);

            if (RootIdentity is not null && (RootIdentity != candidate.RootIdentity ||
                !string.Equals(MembershipCandidate!.LedgerDigest, candidate.LedgerDigest, StringComparison.Ordinal) ||
                MembershipLedgerIdentity != ledgerIdentity))
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The configured path encountered changing or conflicting membership authority.", candidate.RootIdentity);
            }

            AuthorityRoot ??= directory;
            RootIdentity ??= candidate.RootIdentity;
            MembershipCandidate ??= candidate;
            MembershipLedgerIdentity ??= ledgerIdentity;
            AddEvidence(_controls, new ControlEvidence(
                directory, parentInfo.Identity, control, entry.Identity, ledgerIdentity, candidate));
        }

        internal AliasEvidence ReadAlias(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
        {
            var target = resolver._files.ReadLinkTargetNoFollow(parent, name, identity);
            var evidence = new AliasEvidence(parent, name, identity, target);
            AddEvidence(_aliases, evidence);
            return evidence;
        }

        internal void RecordMissingSuffix(
            PhysicalStoreDirectoryHandle parent,
            string firstMissingName,
            IReadOnlyList<string> remainingSuffix,
            PhysicalStorePathTarget target,
            bool allowStableMissingDirectoryRetry)
        {
            var admittedOperationTarget = target == PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix;
            var scopedDesiredSourceTarget = target == PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix &&
                MemberLocatorScope is not null && RequiredRoot == RootIdentity;
            if ((RootIdentity is not null && !admittedOperationTarget && !scopedDesiredSourceTarget) ||
                (RootIdentity is null && admittedOperationTarget) || ActiveAliases.Count != 0)
                throw Unknown("A missing target suffix cannot follow observed authority or an unresolved alias.", RootIdentity);

            var before = resolver._files.InspectHandle(parent);
            var semantics = resolver._names.ObserveDirectoryNameSemantics(parent);
            RequireSupportedMissingNameProfile(semantics);
            if (IsReservedControlName(firstMissingName, semantics) ||
                remainingSuffix.Any(IsPotentialReservedControlName))
            {
                throw Unknown("A prospective directory target cannot occupy the reserved control-directory name.", RootIdentity);
            }

            var missing = resolver._files.InspectChildNoFollow(parent, firstMissingName);
            var after = resolver._files.InspectHandle(parent);
            var semanticsAfter = resolver._names.ObserveDirectoryNameSemantics(parent);
            var missingAfter = resolver._files.InspectChildNoFollow(parent, firstMissingName);
            var missingFinal = missingAfter;
            var afterFinal = after;
            var semanticsFinal = semanticsAfter;
            if (allowStableMissingDirectoryRetry && missing is null &&
                missingAfter is { Kind: PhysicalStoreEntryKind.Directory })
            {
                missingFinal = resolver._files.InspectChildNoFollow(parent, firstMissingName);
                afterFinal = resolver._files.InspectHandle(parent);
                semanticsFinal = resolver._names.ObserveDirectoryNameSemantics(parent);
            }

            var parentStable = before.Kind == PhysicalStoreEntryKind.Directory && after.Kind == PhysicalStoreEntryKind.Directory &&
                before.Identity == after.Identity && semantics == semanticsAfter &&
                afterFinal.Kind == PhysicalStoreEntryKind.Directory && afterFinal.Identity == before.Identity &&
                semanticsFinal == semantics;
            var stillAbsent = missing is null && missingAfter is null && missingFinal is null;
            var stableDirectoryAppeared = allowStableMissingDirectoryRetry &&
                target == PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix && RootIdentity is null &&
                ((missing is { Kind: PhysicalStoreEntryKind.Directory } firstDirectory &&
                  missingAfter is { Kind: PhysicalStoreEntryKind.Directory } secondDirectory &&
                  firstDirectory.Identity == secondDirectory.Identity) ||
                 (missing is null && missingAfter is { Kind: PhysicalStoreEntryKind.Directory } secondDirectoryAfterAbsence &&
                  missingFinal is { Kind: PhysicalStoreEntryKind.Directory } thirdDirectoryAfterAbsence &&
                  secondDirectoryAfterAbsence.Identity == thirdDirectoryAfterAbsence.Identity));
            if (!parentStable || (!stillAbsent && !stableDirectoryAppeared))
            {
                throw Unknown("The prospective configured-root edge changed during native absence observation.", RootIdentity);
            }

            AddEvidence(_missingSuffixes, new MissingSuffixEvidence(parent, before.Identity, semantics,
                firstMissingName, stableDirectoryAppeared
                    ? (missingAfter ?? missingFinal)!.Identity
                    : null));
        }

        private static bool IsReservedControlName(string name, PhysicalStoreNameSemantics semantics)
        {
            // The profile reports lookup properties, not its native Unicode folding table. Keep Unicode names
            // on the strict sensitive path only; under broader profiles, refuse when equivalence is unknowable.
            if (semantics.CaseSensitive && !semantics.NormalizationInsensitive)
                return string.Equals(name, ControlDirectoryName, StringComparison.Ordinal);

            if (!IsAscii(name))
            {
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "The missing target name may alias the reserved control directory under native name semantics.");
            }

            // Both operands are ASCII here, so this checks only ASCII case variants.
            return semantics.CaseSensitive
                ? string.Equals(name, ControlDirectoryName, StringComparison.Ordinal)
                : string.Equals(name, ControlDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPotentialReservedControlName(string name)
        {
            // Parent directories below the first absent edge have no observable native profile yet.
            // ASCII is the only equivalence class we can compare exactly across every supported profile.
            if (!IsAscii(name))
            {
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "A later missing-target component has no observable native name profile for reserved-name comparison.");
            }

            // The explicit ASCII guard above keeps this comparison out of Unicode folding semantics.
            return string.Equals(name, ControlDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAscii(string value) => value.All(static character => character <= 0x7f);

        private static void RequireSupportedMissingNameProfile(PhysicalStoreNameSemantics semantics)
        {
            var expectedEncoding = OperatingSystem.IsWindows()
                ? PhysicalStoreNameEncoding.Utf16LittleEndian
                : PhysicalStoreNameEncoding.Utf8;
            var supported = semantics.ProfileId switch
            {
                "windows-ntfs-name-v1" => OperatingSystem.IsWindows() &&
                    semantics.Encoding == expectedEncoding && !semantics.NormalizationInsensitive,
                "darwin-apfs-v1" => OperatingSystem.IsMacOS() && semantics.Encoding == expectedEncoding,
                "linux-ext4-sensitive-v1" => OperatingSystem.IsLinux() && semantics.Encoding == expectedEncoding &&
                    semantics.CaseSensitive && !semantics.NormalizationInsensitive,
                "linux-ext4-casefold-v1" => OperatingSystem.IsLinux() && semantics.Encoding == expectedEncoding &&
                    !semantics.CaseSensitive && semantics.NormalizationInsensitive,
                _ => false
            };
            if (!supported)
            {
                throw Refusal(PackageStoreAdmissionReason.UnsupportedFilesystem,
                    "The filesystem returned an unsupported native name profile for a prospective missing target.");
            }
        }

        internal void Revalidate(
            PhysicalStoreHandle target,
            PhysicalStorePathTarget targetKind,
            PhysicalStoreDirectoryHandle? targetParent,
            bool allowStableMissingDirectoryRetry = false)
        {
            var missingDirectoryAppeared = false;
            if (_missingSuffixes.Count > 0 && targetKind is not (
                    PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix))
                throw Unknown("Missing-suffix evidence is valid only for configured-root or package-directory classification.", RootIdentity);

            foreach (var anchor in _anchors)
            {
                var held = resolver._files.InspectHandle(anchor.Directory);
                if (held.Kind != PhysicalStoreEntryKind.Directory || held.Identity != anchor.Identity)
                    throw Unknown("A retained namespace anchor changed identity.", RootIdentity);
                using var reopened = resolver._files.OpenNamespaceRoot(anchor.ExactAnchor);
                var current = resolver._files.InspectHandle(reopened);
                if (current.Kind != PhysicalStoreEntryKind.Directory || current.Identity != anchor.Identity)
                    throw Unknown("A configured namespace anchor no longer identifies the retained directory.", RootIdentity);
            }

            foreach (var directory in _directories)
            {
                var info = resolver._files.InspectHandle(directory.Directory);
                var semantics = resolver._names.ObserveDirectoryNameSemantics(directory.Directory);
                if (info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != directory.Identity || semantics != directory.Semantics)
                    throw Unknown("A held directory identity or native lookup profile changed after path resolution.", RootIdentity);
            }

            foreach (var edge in _childEdges)
            {
                var parentInfo = resolver._files.InspectHandle(edge.Parent);
                var childInfo = resolver._files.InspectHandle(edge.Child);
                var named = resolver._files.InspectChildNoFollow(edge.Parent, edge.Name);
                if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity != edge.ParentIdentity ||
                    childInfo.Kind != edge.Kind || childInfo.Identity != edge.ChildIdentity ||
                    named is null || named.Kind != edge.Kind || named.Identity != edge.ChildIdentity)
                {
                    throw Unknown("A configured parent/name/child identity edge changed after path resolution.", RootIdentity);
                }

                if (edge.Child is PhysicalStoreDirectoryHandle childDirectory)
                {
                    using var actualParent = resolver._files.OpenParentDirectory(childDirectory);
                    var actualParentInfo = resolver._files.InspectHandle(actualParent);
                    if (actualParentInfo.Kind != PhysicalStoreEntryKind.Directory || actualParentInfo.Identity != edge.ParentIdentity)
                        throw Unknown("A held directory moved away from its observed parent edge.", RootIdentity);
                }
            }

            foreach (var edge in _parentEdges)
            {
                var child = resolver._files.InspectHandle(edge.Child);
                var parent = resolver._files.InspectHandle(edge.Parent);
                using var actualParent = resolver._files.OpenParentDirectory(edge.Child);
                var reopenedParent = resolver._files.InspectHandle(actualParent);
                if (child.Kind != PhysicalStoreEntryKind.Directory || child.Identity != edge.ChildIdentity ||
                    parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != edge.ParentIdentity ||
                    reopenedParent.Identity != edge.ParentIdentity)
                {
                    throw Unknown("A configured parent transition changed physical ancestry.", RootIdentity);
                }
            }

            foreach (var alias in _aliases)
            {
                var parent = resolver._files.InspectHandle(alias.Parent);
                var current = resolver._files.InspectChildNoFollow(alias.Parent, alias.Name);
                if (parent.Kind != PhysicalStoreEntryKind.Directory || current is null ||
                    current.Kind != PhysicalStoreEntryKind.SymbolicLink || current.Identity != alias.Identity)
                    throw Unknown("A configured symbolic-link entry changed after target expansion.", RootIdentity);
                var targetText = resolver._files.ReadLinkTargetNoFollow(alias.Parent, alias.Name, alias.Identity);
                if (!string.Equals(targetText, alias.Target, StringComparison.Ordinal))
                    throw Unknown("A configured symbolic-link target changed after expansion.", RootIdentity);
            }

            foreach (var missing in _missingSuffixes)
            {
                var parent = resolver._files.InspectHandle(missing.Parent);
                var semantics = resolver._names.ObserveDirectoryNameSemantics(missing.Parent);
                var entry = resolver._files.InspectChildNoFollow(missing.Parent, missing.FirstMissingName);
                var parentAfter = resolver._files.InspectHandle(missing.Parent);
                var semanticsAfter = resolver._names.ObserveDirectoryNameSemantics(missing.Parent);
                var entryAfter = resolver._files.InspectChildNoFollow(missing.Parent, missing.FirstMissingName);
                var parentFinal = parentAfter;
                var semanticsFinal = semanticsAfter;
                var entryFinal = entryAfter;
                if (allowStableMissingDirectoryRetry && entry is null &&
                    entryAfter is { Kind: PhysicalStoreEntryKind.Directory })
                {
                    entryFinal = resolver._files.InspectChildNoFollow(missing.Parent, missing.FirstMissingName);
                    parentFinal = resolver._files.InspectHandle(missing.Parent);
                    semanticsFinal = resolver._names.ObserveDirectoryNameSemantics(missing.Parent);
                }

                var parentStable = parent.Kind == PhysicalStoreEntryKind.Directory &&
                    parent.Identity == missing.ParentIdentity && semantics == missing.Semantics &&
                    parentAfter.Kind == PhysicalStoreEntryKind.Directory &&
                    parentAfter.Identity == missing.ParentIdentity && semanticsAfter == missing.Semantics &&
                    parentFinal.Kind == PhysicalStoreEntryKind.Directory &&
                    parentFinal.Identity == missing.ParentIdentity && semanticsFinal == missing.Semantics;
                var stillAbsent = entry is null && entryAfter is null && entryFinal is null;
                var stableDirectoryAppeared = allowStableMissingDirectoryRetry &&
                    targetKind == PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix && RootIdentity is null &&
                    ((entry is { Kind: PhysicalStoreEntryKind.Directory } firstDirectory &&
                      entryAfter is { Kind: PhysicalStoreEntryKind.Directory } secondDirectory &&
                      firstDirectory.Identity == secondDirectory.Identity) ||
                     (entry is null && entryAfter is { Kind: PhysicalStoreEntryKind.Directory } secondDirectoryAfterAbsence &&
                      entryFinal is { Kind: PhysicalStoreEntryKind.Directory } thirdDirectoryAfterAbsence &&
                      secondDirectoryAfterAbsence.Identity == thirdDirectoryAfterAbsence.Identity));
                var sameAppearedIdentity = missing.DirectoryAppearanceIdentity is null ||
                    stillAbsent ||
                    stableDirectoryAppeared &&
                    (entryAfter ?? entryFinal)?.Identity == missing.DirectoryAppearanceIdentity;
                if (!ReferenceEquals(target, missing.Parent) || !parentStable ||
                    (!stillAbsent && !stableDirectoryAppeared) || !sameAppearedIdentity)
                {
                    throw Unknown("A prospective target suffix changed after native absence observation.", RootIdentity);
                }

                missingDirectoryAppeared |= allowStableMissingDirectoryRetry &&
                    (missing.DirectoryAppearanceIdentity is not null || stableDirectoryAppeared);
            }

            if (targetKind is (PhysicalStorePathTarget.ConfiguredRootDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.PackageDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.AdmittedPackageDirectoryAllowMissingSuffix or
                    PhysicalStorePathTarget.DesiredSourceDirectoryAllowMissingSuffix) &&
                _missingSuffixes.Count > 1)
            {
                throw Unknown("A missing-suffix resolution observed multiple missing edges.", RootIdentity);
            }

            foreach (var control in _controls)
            {
                var parent = resolver._files.InspectHandle(control.Parent);
                if (parent.Kind != PhysicalStoreEntryKind.Directory || parent.Identity != control.ParentIdentity)
                    throw Unknown("A held authority-check parent changed identity.", RootIdentity);
                var current = resolver._files.InspectChildNoFollow(control.Parent, ControlDirectoryName);
                if (control.ControlIdentity is null)
                {
                    if (current is not null)
                        throw Unknown("A reserved authority entry appeared after it was observed absent.", RootIdentity);
                    continue;
                }

                if (current is null || current.Kind != PhysicalStoreEntryKind.Directory || current.Identity != control.ControlIdentity)
                    throw Unknown("A reserved control-directory entry changed after authority observation.", RootIdentity);
                var heldControl = resolver._files.InspectHandle(control.ControlHandle!);
                if (heldControl.Kind != PhysicalStoreEntryKind.Directory || heldControl.Identity != control.ControlIdentity)
                    throw Unknown("A retained control-directory handle changed identity.", RootIdentity);
                var ledger = resolver._files.InspectChildNoFollow(control.ControlHandle!, LedgerName);
                if (ledger is null || ledger.Kind != PhysicalStoreEntryKind.RegularFile || ledger.LinkCount != 1 ||
                    ledger.Identity != control.LedgerIdentity)
                {
                    throw Unknown("The membership ledger changed after authority observation.", RootIdentity);
                }
                var candidate = resolver._registry.ReadCandidate(control.Parent, control.LedgerIdentity!, out var ledgerIdentity);
                if (candidate.RootIdentity != control.Candidate!.RootIdentity ||
                    !string.Equals(candidate.LedgerDigest, control.Candidate.LedgerDigest, StringComparison.Ordinal))
                {
                    throw Unknown("The membership candidate changed after metadata-only path resolution.", RootIdentity);
                }
                ValidateCandidate(control.Candidate.RootIdentity, candidate);
                var ledgerAfterRead = resolver._files.InspectChildNoFollow(control.ControlHandle!, LedgerName);
                if (ledgerAfterRead is null || ledgerAfterRead.Kind != PhysicalStoreEntryKind.RegularFile ||
                    ledgerAfterRead.LinkCount != 1 || ledgerAfterRead.Identity != control.LedgerIdentity ||
                    ledgerIdentity != control.LedgerIdentity)
                {
                    throw Unknown("The membership ledger identity changed during candidate revalidation.", RootIdentity);
                }
            }

            var targetInfo = resolver._files.InspectHandle(target);
            if (targetKind == PhysicalStorePathTarget.ArchiveFile)
            {
                var edge = _childEdges.SingleOrDefault(candidate => ReferenceEquals(candidate.Child, target));
                var named = edge is null || targetParent is null
                    ? null
                    : resolver._files.InspectChildNoFollow(targetParent, edge.Name);
                if (targetInfo.Kind != PhysicalStoreEntryKind.RegularFile || targetInfo.LinkCount != 1 ||
                    targetParent is null || edge is null || !ReferenceEquals(edge.Parent, targetParent) ||
                    named is null || named.Kind != PhysicalStoreEntryKind.RegularFile || named.Identity != targetInfo.Identity)
                {
                    throw Unknown("The archive target is no longer one stable regular single-link file.", RootIdentity);
                }
            }
            else if (targetInfo.Kind != PhysicalStoreEntryKind.Directory)
            {
                throw Unknown("The resolved configured directory target changed kind.", RootIdentity);
            }

            if (AuthorityRoot is not null && RootIdentity is not null)
            {
                var root = resolver._files.InspectHandle(AuthorityRoot);
                if (root.Kind != PhysicalStoreEntryKind.Directory || root.Identity != RootIdentity.HandleIdentity)
                {
                    throw Unknown("The retained authority-root candidate no longer binds its held directory.", RootIdentity);
                }
                if (RetainedInstall is null)
                {
                    if (MembershipCandidate is null || MembershipCandidate.RootIdentity != RootIdentity)
                        throw Unknown("The retained authority-root candidate no longer binds its held membership record.", RootIdentity);
                    ValidateCandidate(RootIdentity, MembershipCandidate);
                }
                else if (MembershipCandidate is not null || RootIdentity != RetainedInstall.Root)
                {
                    throw Unknown("The retained graph-use root no longer matches its immutable install identity.", RootIdentity);
                }
            }

            if (missingDirectoryAppeared)
                throw Unknown("A previously absent package-directory edge appeared during native classification.",
                    RootIdentity, new PackageStoreDirectoryAppearanceRetryException());
        }

        private void ValidateCandidate(PhysicalRootIdentity observedRoot, RootMembershipRecord candidate)
        {
            if (candidate.RootIdentity != observedRoot)
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The membership candidate does not bind the held physical directory.", observedRoot);
            if (RequiredRoot is not null && candidate.RootIdentity != RequiredRoot)
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The configured path encountered a different physical authority root.", RequiredRoot);
            if (MemberLocatorScope is { } scope)
            {
                scope.RequireCandidate(observedRoot, candidate);
                return;
            }
            if (candidate.Status != RootMembershipStatus.Complete || candidate.PendingStateCommit is not null)
                throw Refusal(PackageStoreAdmissionReason.IncompleteEnrollment,
                    "The reserved membership authority is incomplete or has a pending publication.", candidate.RootIdentity);
        }

        private void AddEvidence<T>(List<T> evidence, T item)
        {
            if (evidence.Count >= MaximumEvidenceItems)
                throw Unknown("Configured path resolution exceeded the bounded evidence limit.", RootIdentity);
            evidence.Add(item);
        }

        private sealed record DirectoryEvidence(
            PhysicalStoreDirectoryHandle Directory,
            PhysicalFileIdentity Identity,
            PhysicalStoreNameSemantics Semantics);

        private sealed record NamespaceEvidence(
            string ExactAnchor,
            PhysicalStoreDirectoryHandle Directory,
            PhysicalFileIdentity Identity);

        private sealed record ChildEdgeEvidence(
            PhysicalStoreDirectoryHandle Parent,
            PhysicalFileIdentity ParentIdentity,
            string Name,
            PhysicalStoreHandle Child,
            PhysicalFileIdentity ChildIdentity,
            PhysicalStoreEntryKind Kind);

        private sealed record ParentEdgeEvidence(
            PhysicalStoreDirectoryHandle Child,
            PhysicalFileIdentity ChildIdentity,
            PhysicalStoreDirectoryHandle Parent,
            PhysicalFileIdentity ParentIdentity);

        private sealed record ControlEvidence(
            PhysicalStoreDirectoryHandle Parent,
            PhysicalFileIdentity ParentIdentity,
            PhysicalStoreDirectoryHandle? ControlHandle,
            PhysicalFileIdentity? ControlIdentity,
            PhysicalFileIdentity? LedgerIdentity,
            RootMembershipRecord? Candidate);

        private sealed record MissingSuffixEvidence(
            PhysicalStoreDirectoryHandle Parent,
            PhysicalFileIdentity ParentIdentity,
            PhysicalStoreNameSemantics Semantics,
            string FirstMissingName,
            PhysicalFileIdentity? DirectoryAppearanceIdentity);
    }
}
