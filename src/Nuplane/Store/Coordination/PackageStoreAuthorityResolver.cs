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

    private ResolvedPackageStorePath ResolveParsed(
        ParsedPath path,
        PhysicalStorePathTarget target,
        PhysicalRootIdentity? requiredRoot,
        RootMembershipRegistry.MemberLocatorReplayScope? memberLocatorScope)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        memberLocatorScope?.EnsureActive();
        var state = new ResolutionState(this, requiredRoot, memberLocatorScope);
        try
        {
            var anchor = OpenAnchor(state, path.Anchor
                ?? throw Unknown("The configured path has no namespace anchor."));
            var current = anchor;
            var frames = new List<ComponentFrame> { new(path.Components) };
            PhysicalStoreFileHandle? finalFile = null;
            PhysicalStoreDirectoryHandle? finalFileParent = null;

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
                var entry = _files.InspectChildNoFollow(current, component)
                    ?? throw Unknown("The configured path contains a missing component.", state.RootIdentity);

                if (entry.Kind == PhysicalStoreEntryKind.SymbolicLink)
                {
                    var terminalLink = !HasMeaningfulRemainingComponents(frames);
                    if (terminalLink && target != PhysicalStorePathTarget.ConfiguredRootDirectory)
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

            if (requiredRoot is not null && state.RootIdentity != requiredRoot)
                throw Refusal(PackageStoreAdmissionReason.RootMismatch,
                    "The configured path did not resolve to the required physical authority root.", requiredRoot);

            var finalTarget = (PhysicalStoreHandle?)finalFile ?? current;
            if (state.AuthorityRootIdentity is not null)
                RequireTargetInsideAuthority(state, finalTarget, finalFileParent);

            state.Revalidate(finalTarget, target, finalFileParent);
            var transferredHandles = state.SnapshotHandles();
            var result = new ResolvedPackageStorePath(
                finalTarget,
                state.AuthorityRoot,
                state.RootIdentity,
                state.MembershipCandidate,
                transferredHandles,
                () => state.Revalidate(finalTarget, target, finalFileParent));
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
        RootMembershipRegistry.MemberLocatorReplayScope? memberLocatorScope)
    {
        private readonly List<PhysicalStoreHandle> _handles = [];
        private readonly List<DirectoryEvidence> _directories = [];
        private readonly List<NamespaceEvidence> _anchors = [];
        private readonly List<ChildEdgeEvidence> _childEdges = [];
        private readonly List<ParentEdgeEvidence> _parentEdges = [];
        private readonly List<AliasEvidence> _aliases = [];
        private readonly List<ControlEvidence> _controls = [];

        internal PhysicalRootIdentity? RequiredRoot { get; } = requiredRoot;
        internal RootMembershipRegistry.MemberLocatorReplayScope? MemberLocatorScope { get; } = memberLocatorScope;
        internal PhysicalStoreDirectoryHandle? AuthorityRoot { get; private set; }
        internal PhysicalRootIdentity? RootIdentity { get; private set; }
        internal PhysicalRootIdentity? AuthorityRootIdentity => RootIdentity;
        internal RootMembershipRecord? MembershipCandidate { get; private set; }
        internal HashSet<PhysicalFileIdentity> ActiveAliases { get; } = [];
        internal int AliasExpansions { get; set; }

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
            var parentInfo = resolver._files.InspectHandle(directory);
            if (parentInfo.Kind != PhysicalStoreEntryKind.Directory)
                throw Unknown("The configured path authority check requires a held directory.", RootIdentity);
            var entry = resolver._files.InspectChildNoFollow(directory, ControlDirectoryName);
            if (entry is null)
            {
                AddEvidence(_controls, new ControlEvidence(directory, parentInfo.Identity, null, null, null, null, null));
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
            var ledger = resolver._files.OpenFileChildNoFollow(control, LedgerName, FileAccess.Read);
            Track(ledger);
            var ledgerInfo = resolver._files.InspectHandle(ledger);
            if (ledgerInfo.Kind != PhysicalStoreEntryKind.RegularFile || ledgerInfo.LinkCount != 1 || ledgerInfo.Identity != ledgerEntry.Identity)
                throw Unknown("The membership ledger changed between no-follow inspection and held open.", RootIdentity);
            RecordFileEdge(control, LedgerName, ledger, ledgerInfo.Identity);

            var candidate = resolver._registry.ReadCandidate(directory);
            var ledgerAfter = resolver._files.InspectChildNoFollow(control, LedgerName);
            var ledgerHandleAfter = resolver._files.InspectHandle(ledger);
            if (ledgerAfter is null || ledgerAfter.Kind != PhysicalStoreEntryKind.RegularFile || ledgerAfter.LinkCount != 1 ||
                ledgerAfter.Identity != ledgerInfo.Identity || ledgerHandleAfter.Identity != ledgerInfo.Identity)
            {
                throw Unknown("The membership ledger changed while its structural candidate was read.", RootIdentity);
            }
            var observedRoot = new PhysicalRootIdentity(parentInfo.Identity);
            ValidateCandidate(observedRoot, candidate);

            if (RootIdentity is not null && (RootIdentity != candidate.RootIdentity ||
                !string.Equals(MembershipCandidate!.LedgerDigest, candidate.LedgerDigest, StringComparison.Ordinal)))
            {
                throw Refusal(PackageStoreAdmissionReason.UnknownAuthority,
                    "The configured path encountered changing or conflicting membership authority.", candidate.RootIdentity);
            }

            AuthorityRoot ??= directory;
            RootIdentity ??= candidate.RootIdentity;
            MembershipCandidate ??= candidate;
            AddEvidence(_controls, new ControlEvidence(
                directory, parentInfo.Identity, control, entry.Identity, ledger, ledgerInfo.Identity, candidate));
        }

        internal AliasEvidence ReadAlias(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
        {
            var target = resolver._files.ReadLinkTargetNoFollow(parent, name, identity);
            var evidence = new AliasEvidence(parent, name, identity, target);
            AddEvidence(_aliases, evidence);
            return evidence;
        }

        internal void Revalidate(
            PhysicalStoreHandle target,
            PhysicalStorePathTarget targetKind,
            PhysicalStoreDirectoryHandle? targetParent)
        {
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
                var heldLedger = resolver._files.InspectHandle(control.LedgerHandle!);
                if (ledger is null || ledger.Kind != PhysicalStoreEntryKind.RegularFile || ledger.LinkCount != 1 ||
                    ledger.Identity != control.LedgerIdentity || heldLedger.Kind != PhysicalStoreEntryKind.RegularFile ||
                    heldLedger.LinkCount != 1 || heldLedger.Identity != control.LedgerIdentity)
                {
                    throw Unknown("The held membership ledger changed after authority observation.", RootIdentity);
                }
                var candidate = resolver._registry.ReadCandidate(control.Parent);
                if (candidate.RootIdentity != control.Candidate!.RootIdentity ||
                    !string.Equals(candidate.LedgerDigest, control.Candidate.LedgerDigest, StringComparison.Ordinal))
                {
                    throw Unknown("The membership candidate changed after metadata-only path resolution.", RootIdentity);
                }
                ValidateCandidate(control.Candidate.RootIdentity, candidate);
                var ledgerAfterRead = resolver._files.InspectChildNoFollow(control.ControlHandle!, LedgerName);
                var heldLedgerAfterRead = resolver._files.InspectHandle(control.LedgerHandle!);
                if (ledgerAfterRead is null || ledgerAfterRead.Kind != PhysicalStoreEntryKind.RegularFile ||
                    ledgerAfterRead.LinkCount != 1 || ledgerAfterRead.Identity != control.LedgerIdentity ||
                    heldLedgerAfterRead.Identity != control.LedgerIdentity)
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
                if (root.Kind != PhysicalStoreEntryKind.Directory || root.Identity != RootIdentity.HandleIdentity ||
                    MembershipCandidate is null || MembershipCandidate.RootIdentity != RootIdentity)
                {
                    throw Unknown("The retained authority-root candidate no longer binds its held directory.", RootIdentity);
                }
                ValidateCandidate(RootIdentity, MembershipCandidate);
            }
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
            PhysicalStoreFileHandle? LedgerHandle,
            PhysicalFileIdentity? LedgerIdentity,
            RootMembershipRecord? Candidate);
    }
}
