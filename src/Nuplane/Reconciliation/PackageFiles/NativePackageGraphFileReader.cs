using System.Xml;
using System.Xml.Linq;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Reconciliation.PackageFiles;

/// <summary>Copies graph inputs through no-follow native handles; no filesystem authority escapes the read.</summary>
internal sealed class NativePackageGraphFileReader : IPackageGraphFileReader
{
    internal const int MaximumEntries = 16384;
    internal const int MaximumDepth = 64;
    internal const int MaximumNuspecBytes = 4 * 1024 * 1024;

    private readonly PackageStoreOperationBorrow? _borrow;
    private readonly IPhysicalStoreFileSystem? _boundFiles;
    private readonly PhysicalRootIdentity? _boundRoot;
    private readonly IReadOnlyDictionary<string, PackageInstallIdentityReader.PackageInstallIdentityObservation>? _boundInstalls;
    private readonly List<Action> _boundReadReplays = [];

    internal NativePackageGraphFileReader(PackageStoreOperationBorrow? borrow = null) => _borrow = borrow;

    /// <summary>Reads only exact installs retained by the internal bound-Incomplete reconstruction owner.</summary>
    internal NativePackageGraphFileReader(
        IPhysicalStoreFileSystem files,
        PhysicalRootIdentity expectedRoot,
        IReadOnlyDictionary<string, PackageInstallIdentityReader.PackageInstallIdentityObservation> installs)
    {
        _boundFiles = files ?? throw new ArgumentNullException(nameof(files));
        _boundRoot = expectedRoot ?? throw new ArgumentNullException(nameof(expectedRoot));
        _boundInstalls = installs ?? throw new ArgumentNullException(nameof(installs));
        if (installs.Count == 0 || installs.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("A held-root graph reader requires exact retained install observations.", nameof(installs));
    }

    public InstalledPackageGraphFiles ReadInstallFiles(ResolvedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.InstallPath))
            return InstalledPackageGraphFiles.Empty;

        if (_boundInstalls is not null)
        {
            if (!_boundInstalls.TryGetValue(package.InstallPath, out var install))
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "Offline graph reconstruction requested an install outside its exact active candidate set.", _boundRoot);
            install.Revalidate();
            var files = _boundFiles!;
            var info = files.InspectHandle(install.InstallDirectory);
            if (info.Kind != PhysicalStoreEntryKind.Directory ||
                !string.Equals(info.Identity.Provider, _boundRoot!.HandleIdentity.Provider, StringComparison.Ordinal) ||
                !string.Equals(info.Identity.VolumeOrDeviceId, _boundRoot.HandleIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
            {
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "The retained graph-input directory is not a same-volume child of the bound Incomplete root.", _boundRoot);
            }
            var graphFiles = ReadDirectory(files, install.InstallDirectory, _boundRoot, _boundReadReplays);
            install.Revalidate();
            return graphFiles;
        }

        if (_borrow is not null)
            return PackageStoreOperationAccess.WithValidatedPackageDirectory(_borrow, package.InstallPath,
                (files, directory) => ReadDirectory(files, directory, _borrow.Root));

        return PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(package.InstallPath,
            (files, status, directory) => status == UnenrolledPackageDirectoryStatus.Missing
                ? InstalledPackageGraphFiles.Empty
                : ReadDirectory(files, directory!, root: null));
    }

    /// <summary>Replays every exact install path and graph-input tree retained during offline reconstruction.</summary>
    internal void RevalidateBoundReads()
    {
        if (_boundInstalls is null)
            throw new InvalidOperationException("Only a held-root graph reader has retained offline observations.");
        foreach (var install in _boundInstalls.Values)
            install.Revalidate();
        foreach (var replay in _boundReadReplays)
            replay();
        foreach (var install in _boundInstalls.Values)
            install.Revalidate();
    }

    private static InstalledPackageGraphFiles ReadDirectory(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity? root,
        ICollection<Action>? retainedReplays = null)
    {
        var enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot enumerate installed graph inputs through held native directories.", root);
        var names = files as IPhysicalStoreNameFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot retain installed graph input name semantics.", root);
        var volume = root?.HandleIdentity ?? files.InspectHandle(directory).Identity;
        var assets = new List<string>();
        XDocument? nuspec = null;
        var remaining = MaximumEntries;
        var snapshots = new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal);
        Walk(directory, relative: "", depth: 0);
        Replay(directory, relative: "");
        retainedReplays?.Add(() => Replay(directory, relative: ""));
        return new InstalledPackageGraphFiles(nuspec,
            Array.AsReadOnly(assets.OrderBy(static asset => asset, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static asset => asset, StringComparer.Ordinal).ToArray()));

        void Walk(PhysicalStoreDirectoryHandle parent, string relative, int depth)
        {
            if (depth > MaximumDepth)
                throw Refuse("Installed graph inputs exceed the native directory depth bound.", root);
            var before = files.InspectHandle(parent);
            RequireEntry(before, PhysicalStoreEntryKind.Directory, volume, root);
            var profile = names.ObserveDirectoryNameSemantics(parent);
            RequireNoNestedAuthority(parent);
            var entries = enumeration.EnumerateChildNamesNoFollow(parent, MaximumEntries);
            var observed = new Dictionary<string, PhysicalStoreEntryInfo>(StringComparer.Ordinal);
            remaining -= entries.Count;
            if (remaining < 0)
                throw Refuse("Installed graph inputs exceed the complete native entry bound.", root);

            // Preserve the legacy deterministic first-nuspec selection, with an ordinal tie-breaker.
            var nuspecName = relative.Length == 0
                ? entries.Where(static name => name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static name => name, StringComparer.Ordinal).FirstOrDefault()
                : null;

            foreach (var name in entries)
            {
                var entry = files.InspectChildNoFollow(parent, name)
                    ?? throw Refuse("An installed graph input disappeared during native enumeration.", root);
                observed.Add(name, entry);
                var childRelative = relative.Length == 0 ? name : Path.Combine(relative, name);
                if (entry.Kind == PhysicalStoreEntryKind.Directory)
                {
                    RequireEntry(entry, PhysicalStoreEntryKind.Directory, volume, root);
                    using var child = files.OpenDirectoryChildNoFollow(parent, name);
                    RequireUnchanged(entry, files.InspectHandle(child), root);
                    Walk(child, childRelative, depth + 1);
                    RequireUnchanged(entry, files.InspectChildNoFollow(parent, name), root);
                }
                else
                {
                    RequireEntry(entry, PhysicalStoreEntryKind.RegularFile, volume, root);
                    RequireCanonical(parent, name, entry, profile);
                    if (string.Equals(name, nuspecName, StringComparison.Ordinal))
                    {
                        using var file = files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
                        RequireUnchanged(entry, files.InspectHandle(file), root);
                        if (entry.Length > MaximumNuspecBytes)
                            throw Refuse("Installed package nuspec exceeds the bounded metadata read limit.", root);
                        var bytes = files.ReadControlFile(file, MaximumNuspecBytes);
                        RequireUnchanged(entry, files.InspectHandle(file), root);
                        if (bytes.LongLength != entry.Length)
                            throw Refuse("Installed package nuspec changed length while being read.", root);
                        using var stream = new MemoryStream(bytes, writable: false);
                        using var xml = XmlReader.Create(stream, new XmlReaderSettings
                        {
                            DtdProcessing = DtdProcessing.Prohibit,
                            XmlResolver = null,
                            MaxCharactersInDocument = MaximumNuspecBytes
                        });
                        nuspec = XDocument.Load(xml);
                    }
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        assets.Add(childRelative);
                    RequireUnchanged(entry, files.InspectChildNoFollow(parent, name), root);
                    RequireCanonical(parent, name, entry, profile);
                }
            }

            RequireUnchanged(before, files.InspectHandle(parent), root);
            RequireNoNestedAuthority(parent);
            if (names.ObserveDirectoryNameSemantics(parent) != profile ||
                !entries.SequenceEqual(enumeration.EnumerateChildNamesNoFollow(parent, MaximumEntries), StringComparer.Ordinal))
                throw Refuse("Installed graph input names or directory semantics changed during the native read.", root);
            snapshots.Add(relative, new DirectorySnapshot(before, profile, entries, observed));
        }

        // Replay earlier siblings too: matching final names alone would miss a same-name replacement
        // after one subtree was read but before a later nuspec or subtree completed.
        void Replay(PhysicalStoreDirectoryHandle parent, string relative)
        {
            var snapshot = snapshots[relative];
            RequireUnchanged(snapshot.Directory, files.InspectHandle(parent), root);
            RequireNoNestedAuthority(parent);
            if (names.ObserveDirectoryNameSemantics(parent) != snapshot.Profile ||
                !snapshot.Names.SequenceEqual(enumeration.EnumerateChildNamesNoFollow(parent, MaximumEntries), StringComparer.Ordinal))
                throw Refuse("Installed graph input names changed before the detached graph inputs could be returned.", root);
            foreach (var name in snapshot.Names)
            {
                var entry = snapshot.Entries[name];
                RequireUnchanged(entry, files.InspectChildNoFollow(parent, name), root);
                if (entry.Kind == PhysicalStoreEntryKind.Directory)
                {
                    using var child = files.OpenDirectoryChildNoFollow(parent, name);
                    Replay(child, relative.Length == 0 ? name : Path.Combine(relative, name));
                }
                else
                    RequireCanonical(parent, name, entry, snapshot.Profile);
                RequireUnchanged(entry, files.InspectChildNoFollow(parent, name), root);
            }
            RequireUnchanged(snapshot.Directory, files.InspectHandle(parent), root);
            RequireNoNestedAuthority(parent);
            if (names.ObserveDirectoryNameSemantics(parent) != snapshot.Profile)
                throw Refuse("Installed graph input directory semantics changed during replay.", root);
        }

        void RequireNoNestedAuthority(PhysicalStoreDirectoryHandle parent)
        {
            // Native lookup applies this directory's own name semantics, including aliases of
            // the reserved name. A descendant authority is never covered by the outer borrow.
            if (files.InspectChildNoFollow(parent, RootMembershipRegistry.ControlDirectoryName) is not null)
                throw Refuse("Installed graph inputs encounter a nested package-store authority.", root);
        }

        void RequireCanonical(PhysicalStoreDirectoryHandle parent, string name,
            PhysicalStoreEntryInfo entry, PhysicalStoreNameSemantics profile)
        {
            var canonical = names.ObserveCanonicalFileNameNoFollow(parent, name, entry.Identity);
            if (canonical.ParentIdentity != files.InspectHandle(parent).Identity ||
                canonical.FileIdentity != entry.Identity || canonical.Semantics != profile ||
                !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
                throw Refuse("Installed graph input does not retain its exact native name and profile.", root);
        }
    }

    private static void RequireEntry(PhysicalStoreEntryInfo entry, PhysicalStoreEntryKind kind,
        PhysicalFileIdentity volume, PhysicalRootIdentity? root)
    {
        if (entry.Kind != kind || kind == PhysicalStoreEntryKind.RegularFile && entry.LinkCount != 1 ||
            !string.Equals(entry.Identity.Provider, volume.Provider, StringComparison.Ordinal) ||
            !string.Equals(entry.Identity.VolumeOrDeviceId, volume.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse("Installed graph inputs must retain no-follow, same-volume directories and single-link files.", root);
    }

    private static void RequireUnchanged(PhysicalStoreEntryInfo before, PhysicalStoreEntryInfo? after,
        PhysicalRootIdentity? root)
    {
        if (after is null || after.Identity != before.Identity || after.Kind != before.Kind ||
            after.LinkCount != before.LinkCount || after.Kind == PhysicalStoreEntryKind.RegularFile && after.Length != before.Length)
            throw Refuse("An installed graph input changed its native identity, kind or file metadata during the read.", root);
    }

    private static PackageStoreAdmissionException Refuse(string message, PhysicalRootIdentity? root)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private sealed record DirectorySnapshot(PhysicalStoreEntryInfo Directory, PhysicalStoreNameSemantics Profile,
        IReadOnlyList<string> Names, IReadOnlyDictionary<string, PhysicalStoreEntryInfo> Entries);
}
