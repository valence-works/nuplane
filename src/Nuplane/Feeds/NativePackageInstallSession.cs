using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Feeds;

/// <summary>Stages, validates, extracts, and publishes one archive through an admitted native root.</summary>
/// <remarks>
/// This increment deliberately leaves identity-bound staging evidence on every failure. Native package
/// cleanup and crash recovery require a separately reviewed deletion authority and are not available here.
/// </remarks>
internal static class NativePackageInstallSession
{
    private const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private const int MaximumArchiveEntries = 10_000;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const int MaximumPathLength = 1_024;
    private const int MaximumPathDepth = 32;
    private const int MaximumComponentBytes = 255;
    private const string ArchiveFileName = ".nuplane-source.nupkg";
    private const string AttemptRecordName = "attempt.json";
    private const string ArchiveCreatedRecordName = "archive-created.json";
    private const string ArchiveRecordName = "archive-complete.json";
    private const string PreparedRecordName = "prepared.json";
    private const string PublishedRecordName = "published.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    /// <summary>Acquires one native install directory while the supplied complete-root borrow remains live.</summary>
    /// <param name="installRoot">The configured package root.</param>
    /// <param name="installDirectory">The deterministic destination below that root.</param>
    /// <param name="borrow">The original complete-root operation borrow.</param>
    /// <param name="writeArchive">Writes the already-authorized archive to the supplied native stream.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The exact completed install path.</returns>
    internal static async Task<string> AcquireAsync(
        string installRoot,
        string installDirectory,
        PackageStoreOperationBorrow borrow,
        Func<Stream, CancellationToken, Task> writeArchive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(writeArchive);
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(installRoot, installDirectory, borrow.Root);

        // This scoped probe is intentionally before the callback. It rejects expired/wrong-root borrows
        // before acquisition can request archive bytes, and permits a completed install to be reused.
        if (PackageInstallStore.IsInstalled(target.InstallPath, borrow))
            return target.InstallPath;

        var destinationExists = PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
            borrow,
            target.InstallPath,
            static (_, directory) => directory is not null);
        if (destinationExists)
            throw Refuse("The destination exists without valid native completion evidence.", borrow.Root);

        return await PackageStoreOperationAccess.WithValidatedRootAsync(
            borrow,
            (files, root, token) => InstallUnderRootAsync(
                files, root, target, borrow, writeArchive, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> InstallUnderRootAsync(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        InstallTarget target,
        PackageStoreOperationBorrow borrow,
        Func<Stream, CancellationToken, Task> writeArchive,
        CancellationToken cancellationToken)
    {
        var streams = files as IPhysicalStorePackageStreamFileSystem
            ?? throw Unsupported("The filesystem does not support bounded native package streams.", borrow.Root);
        var names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("The filesystem cannot verify native package names.", borrow.Root);
        var directories = files as IPhysicalStoreDirectoryPublicationFileSystem
            ?? throw Unsupported("The filesystem does not support same-parent no-replace directory publication.", borrow.Root);

        var rootInfo = RequireDirectory(files.InspectHandle(root), borrow.Root, "The admitted root handle is not a directory.");
        if (rootInfo.Identity != borrow.Root.HandleIdentity)
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The supplied root handle does not match the operation borrow.",
                borrow.Root);

        var rootSemantics = ObserveSemantics(names, root, borrow.Root);
        using var parentPath = OpenInstallParent(files, names, directories, root, rootInfo, rootSemantics,
            target.ParentComponents, borrow.Root);
        ValidateComponent(target.DestinationName, parentPath.Last.Semantics, borrow.Root);
        if (IsReservedRootName(target.DestinationName, parentPath.Last.Semantics))
            throw Refuse("The install destination cannot use a reserved package-store authority directory name.", borrow.Root);
        RequireDirectory(files.InspectHandle(parentPath.Last.Handle), borrow.Root,
            "The held destination parent changed before staging.", parentPath.Last.Identity);
        RequireSemantics(names, parentPath.Last.Handle, borrow.Root, parentPath.Last.Semantics);

        var finalBefore = files.InspectChildNoFollow(parentPath.Last.Handle, target.DestinationName);
        if (finalBefore is not null)
            throw Refuse("The destination appeared before native archive staging.", borrow.Root);

        var operationId = Guid.NewGuid().ToString("N");
        var stagedName = $".nuplane-stage-{operationId}";
        var preparedName = $".nuplane-prepared-{operationId}";
        using var stagingDirectory = CreateNewDirectory(files, names, directories,
            parentPath.Last.Handle, parentPath.Last, stagedName, borrow.Root);

        WriteEvidence(files, names, stagingDirectory.Handle, stagingDirectory, AttemptRecordName,
            new
            {
                SchemaVersion = 1,
                Phase = "archive-writing",
                OperationId = operationId,
                RootIdentity = borrow.Root.HandleIdentity,
                ParentIdentity = parentPath.Last.Identity,
                ParentNameProfile = parentPath.Last.Semantics,
                StagingName = stagedName,
                StagingIdentity = stagingDirectory.Identity,
                PreparedName = preparedName,
                DestinationName = target.DestinationName,
                RootRelativeDestination = target.RootRelativePath,
                ArchiveFileName
            }, borrow.Root);

        PhysicalStoreEntryInfo archiveInfo;
        using (var archiveFile = CreateNewFile(files, names, stagingDirectory, ArchiveFileName, borrow.Root))
        {
            var archiveCreated = RequireRegularFile(files.InspectHandle(archiveFile.Handle), borrow.Root,
                "The exclusive staged archive is not one empty regular file.", archiveFile.ParentIdentity, expectedLength: 0);
            RequireCanonicalFileName(names, stagingDirectory, ArchiveFileName, archiveCreated, borrow.Root);
            WriteEvidence(files, names, stagingDirectory.Handle, stagingDirectory, ArchiveCreatedRecordName,
                new
                {
                    SchemaVersion = 1,
                    Phase = "archive-created-before-write",
                    OperationId = operationId,
                    RootIdentity = borrow.Root.HandleIdentity,
                    ParentIdentity = parentPath.Last.Identity,
                    StagingIdentity = stagingDirectory.Identity,
                    ArchiveName = ArchiveFileName,
                    ArchiveIdentity = archiveCreated.Identity,
                    ArchiveLength = archiveCreated.Length
                }, borrow.Root);

            await using (var output = streams.CreatePackageFileWriteStream(
                             stagingDirectory.Handle, ArchiveFileName, archiveFile.Handle,
                             files.InspectHandle(stagingDirectory.Handle), MaximumArchiveBytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writeArchive(output, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var writerPathReplay = ReplayInstallParent(files, names, directories,
                       root, rootInfo.Identity, rootSemantics, target.ParentComponents,
                       parentPath, borrow.Root))
            {
                if (writerPathReplay.Last.Identity != parentPath.Last.Identity)
                    throw Refuse("The install parent changed while the package archive was being acquired.", borrow.Root);
            }

            archiveInfo = RequireRegularFile(files.InspectHandle(archiveFile.Handle), borrow.Root,
                "The staged archive is not one regular file.", archiveFile.ParentIdentity,
                expectedLength: null);
            if (archiveInfo.Identity != archiveCreated.Identity)
                throw Refuse("The staged archive file identity changed while it was being written.", borrow.Root);
            RequireCanonicalFileName(names, stagingDirectory, ArchiveFileName, archiveInfo, borrow.Root);
            RequireSameEntry(archiveInfo,
                RequireRegularFile(files.InspectChildNoFollow(stagingDirectory.Handle, ArchiveFileName), borrow.Root,
                    "The staged archive name changed after writing.", archiveFile.ParentIdentity, archiveInfo.Length),
                borrow.Root, "The staged archive changed after writing.");
            if (archiveInfo.Length <= 0 || archiveInfo.Length > MaximumArchiveBytes)
                throw Refuse("The staged archive is empty or exceeds its byte limit.", borrow.Root);
        }

        using var archiveReadFile = OpenExactFile(files, names, stagingDirectory, ArchiveFileName, archiveInfo, borrow.Root);
        await using var archiveStream = streams.OpenPackageArchiveReadStream(
            stagingDirectory.Handle, ArchiveFileName, archiveReadFile.Handle,
            files.InspectHandle(stagingDirectory.Handle), archiveInfo, MaximumArchiveBytes);
        var archiveDigest = await SHA512.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false);
        var contentHash = "sha512:" + Convert.ToBase64String(archiveDigest);
        cancellationToken.ThrowIfCancellationRequested();
        archiveStream.Position = 0;

        using var archive = OpenZipArchive(archiveStream, borrow.Root);
        var preparedDirectory = CreateNewDirectory(files, names, directories,
            parentPath.Last.Handle, parentPath.Last, preparedName, borrow.Root);
        var outputDirectories = new List<HeldDirectory> { preparedDirectory };
        try
        {
            WriteEvidence(files, names, stagingDirectory.Handle, stagingDirectory, ArchiveRecordName,
                new
                {
                    SchemaVersion = 1,
                    Phase = "archive-staged",
                    OperationId = operationId,
                    RootIdentity = borrow.Root.HandleIdentity,
                    ParentIdentity = parentPath.Last.Identity,
                    StagingName = stagedName,
                    StagingIdentity = stagingDirectory.Identity,
                    ArchiveName = ArchiveFileName,
                    ArchiveIdentity = archiveInfo.Identity,
                    ArchiveLength = archiveInfo.Length,
                    ContentHash = contentHash,
                    PreparedName = preparedName,
                    PreparedIdentity = preparedDirectory.Identity,
                    DestinationName = target.DestinationName,
                    RootRelativeDestination = target.RootRelativePath
                }, borrow.Root);

            WriteEvidence(files, names, stagingDirectory.Handle, stagingDirectory, PreparedRecordName,
                new
                {
                    SchemaVersion = 1,
                    Phase = "prepared-directory-created",
                    OperationId = operationId,
                    ParentIdentity = parentPath.Last.Identity,
                    PreparedName = preparedName,
                    PreparedIdentity = preparedDirectory.Identity,
                    DestinationName = target.DestinationName,
                    ContentHash = contentHash
                }, borrow.Root);

            var preparedSemantics = ObserveSemantics(names, preparedDirectory.Handle, borrow.Root);
            var plan = ValidateArchiveEntries(archive, preparedSemantics, borrow.Root);
            var archiveCurrent = RequireRegularFile(files.InspectHandle(archiveReadFile.Handle), borrow.Root,
                "The held staged archive changed during ZIP validation.", stagingDirectory.Identity, archiveInfo.Length);
            RequireSameEntry(archiveInfo, archiveCurrent, borrow.Root, "The staged archive changed during ZIP validation.");
            RequireSameEntry(archiveInfo,
                RequireRegularFile(files.InspectChildNoFollow(stagingDirectory.Handle, ArchiveFileName), borrow.Root,
                    "The staged archive name changed during ZIP validation.", stagingDirectory.Identity, archiveInfo.Length),
                borrow.Root, "The staged archive changed during ZIP validation.");
            RequireSemantics(names, stagingDirectory.Handle, borrow.Root, stagingDirectory.Semantics);

            var outputTree = new Dictionary<string, HeldDirectory>(PathComparer(preparedSemantics))
            {
                [string.Empty] = preparedDirectory
            };
            long extractedBytes = 0;
            foreach (var entry in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    _ = EnsureOutputDirectory(files, names, directories, preparedDirectory, outputTree,
                        outputDirectories, entry.Components, entry.Components.Length, borrow.Root);
                    continue;
                }

                var parent = EnsureOutputDirectory(files, names, directories, preparedDirectory, outputTree,
                    outputDirectories, entry.Components, entry.Components.Length - 1, borrow.Root);
                using var file = CreateNewFile(files, names, parent, entry.Components[^1], borrow.Root);
                await using var output = streams.CreatePackageFileWriteStream(
                    parent.Handle, entry.Components[^1], file.Handle,
                    files.InspectHandle(parent.Handle), MaximumEntryBytes);
                long extracted;
                try
                {
                    using var input = entry.ArchiveEntry!.Open();
                    extracted = await CopyEntryBoundedAsync(input, output, entry.ArchiveEntry.Length,
                        entry.ArchiveEntry.Crc32, extractedBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    throw Refuse("A package ZIP entry could not be decoded and verified.", borrow.Root, exception);
                }
                extractedBytes = checked(extractedBytes + extracted);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var written = RequireRegularFile(files.InspectHandle(file.Handle), borrow.Root,
                    "An extracted package entry is not a single-link regular file.", parent.Identity,
                    entry.ArchiveEntry.Length);
                RequireCanonicalFileName(names, parent, entry.Components[^1], written, borrow.Root);
                RequireSameEntry(written,
                    RequireRegularFile(files.InspectChildNoFollow(parent.Handle, entry.Components[^1]), borrow.Root,
                        "An extracted package entry changed after writing.", parent.Identity,
                        entry.ArchiveEntry.Length), borrow.Root,
                    "An extracted package entry changed after writing.");
            }

            WriteNewPackageMetadata(files, names, preparedDirectory, PackageInstallStore.ContentHashFileName,
                StrictUtf8.GetBytes(contentHash), borrow.Root);
            WriteNewPackageMetadata(files, names, preparedDirectory, PackageInstallStore.CompletionMarkerFileName,
                ReadOnlyMemory<byte>.Empty, borrow.Root);

            // Close every prepared descendant, including the prepared root, before the same-parent move.
            DisposeDirectoriesReverse(outputDirectories);
            outputDirectories.Clear();

            using (var preparedForVerification = OpenExactDirectory(files, names, directories,
                       parentPath.Last.Handle, parentPath.Last, preparedName, preparedDirectory.Identity, borrow.Root))
            {
                if (!PackageInstallStore.IsInstalledNative(files, preparedForVerification.Handle, borrow.Root) ||
                    !string.Equals(PackageInstallStore.ReadContentHashNative(
                            files, preparedForVerification.Handle, borrow.Root, cancellationToken),
                        contentHash, StringComparison.Ordinal))
                {
                    throw Refuse("The prepared package did not retain its exact completion and archive-hash metadata.", borrow.Root);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var publicationParentPath = ReplayInstallParent(files, names, directories,
                root, rootInfo.Identity, rootSemantics, target.ParentComponents, parentPath, borrow.Root);
            var publicationParent = publicationParentPath.Last;
            EnsureDestinationAbsent(files, names, publicationParent, target.DestinationName, borrow.Root);
            using (var preparedForPublication = OpenExactDirectory(files, names, directories,
                       publicationParent.Handle, publicationParent, preparedName, preparedDirectory.Identity, borrow.Root))
            {
                RequireDirectory(files.InspectHandle(preparedForPublication.Handle), borrow.Root,
                    "The prepared directory changed before publication.", preparedDirectory.Identity);
                RequireSemantics(names, preparedForPublication.Handle, borrow.Root, preparedSemantics);
            }
            EnsureDestinationAbsent(files, names, publicationParent, target.DestinationName, borrow.Root);

            PhysicalStoreEntryInfo publishResult;
            try
            {
                publishResult = directories.PublishDirectoryNoReplaceAt(
                    publicationParent.Handle, preparedName, preparedDirectory.Identity, target.DestinationName);
                if (publishResult.Kind != PhysicalStoreEntryKind.Directory ||
                    publishResult.Identity != preparedDirectory.Identity)
                    throw Refuse("Native publication returned an unexpected prepared-directory identity.", borrow.Root);
            }
            catch (Exception publishError)
            {
                if (TryResolvePublished(files, names, directories, publicationParent, target.DestinationName,
                        contentHash, borrow.Root, cancellationToken, out _))
                {
                    using var publishedPathReplay = ReplayInstallParent(files, names, directories,
                        root, rootInfo.Identity, rootSemantics, target.ParentComponents, parentPath, borrow.Root);
                    if (publishedPathReplay.Last.Identity != publicationParent.Identity)
                        throw Refuse("The published package is no longer reachable through its exact configured parent path.", borrow.Root);
                    WritePublishedEvidenceBestEffort(files, names, stagingDirectory, parentPath.Last,
                        operationId, stagedName, preparedName, target.DestinationName,
                        contentHash, borrow.Root);
                    return target.InstallPath;
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publishError).Throw();
                throw;
            }

            using var finalParentPath = ReplayInstallParent(files, names, directories,
                root, rootInfo.Identity, rootSemantics, target.ParentComponents, parentPath, borrow.Root);
            if (finalParentPath.Last.Identity != publicationParent.Identity)
                throw Refuse("The published package is no longer reachable through its exact configured parent path.", borrow.Root);
            var final = ResolvePublished(files, names, directories, finalParentPath.Last,
                target.DestinationName, contentHash, borrow.Root, cancellationToken);
            if (final.Identity != publishResult.Identity || final.Identity != preparedDirectory.Identity)
                throw Refuse("The published install does not identify the exact prepared directory.", borrow.Root);

            WritePublishedEvidenceBestEffort(files, names, stagingDirectory, parentPath.Last,
                operationId, stagedName, preparedName, target.DestinationName,
                contentHash, borrow.Root);
            return target.InstallPath;
        }
        finally
        {
            DisposeDirectoriesReverse(outputDirectories);
        }
    }

    private static InstallTarget ResolveTarget(string installRoot, string installDirectory, PhysicalRootIdentity root)
    {
        string fullRoot;
        string fullInstall;
        try
        {
            fullRoot = Path.GetFullPath(installRoot);
            fullInstall = Path.GetFullPath(installDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw Refuse("The install root or destination is not a supported absolute path.", root, exception);
        }

        var relative = Path.GetRelativePath(fullRoot, fullInstall);
        if (relative is "." or ".." ||
            Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw Refuse("The destination is not a strict descendant of the configured install root.", root);

        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (components.Length < 2 || components.Any(static component => component.Length == 0))
            throw Refuse("The destination must contain a parent path and a final install name.", root);
        foreach (var component in components)
            ValidateComponent(component, null, root);

        return new InstallTarget(fullInstall, components[..^1], components[^1], string.Join('/', components));
    }

    private static OwnedDirectoryPath OpenInstallParent(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle root,
        PhysicalStoreEntryInfo rootInfo,
        PhysicalStoreNameSemantics rootSemantics,
        IReadOnlyList<string> components,
        PhysicalRootIdentity expectedRoot)
    {
        var owned = new List<HeldDirectory>();
        try
        {
            var current = new HeldDirectory(root, rootInfo.Identity, rootSemantics, ownsHandle: false);
            foreach (var component in components)
            {
                if (IsReservedRootName(component, current.Semantics))
                    throw Refuse("A package install path cannot use a reserved package-store authority directory.", expectedRoot);
                var next = OpenOrCreateDirectory(files, names, directories, current, component, expectedRoot);
                owned.Add(next);
                current = next;
            }
            return new OwnedDirectoryPath(owned);
        }
        catch
        {
            DisposeDirectoriesReverse(owned);
            throw;
        }
    }

    private static OwnedDirectoryPath ReplayInstallParent(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle root,
        PhysicalFileIdentity expectedRootIdentity,
        PhysicalStoreNameSemantics rootSemantics,
        IReadOnlyList<string> components,
        OwnedDirectoryPath expectedPath,
        PhysicalRootIdentity expectedRoot)
    {
        if (components.Count == 0 || expectedPath.Directories.Count != components.Count)
            throw Refuse("The exact install parent path could not be replayed.", expectedRoot);

        RequireDirectory(files.InspectHandle(root), expectedRoot,
            "The admitted root changed while replaying the install parent path.", expectedRootIdentity);
        RequireSemantics(names, root, expectedRoot, rootSemantics);
        var current = new HeldDirectory(root, expectedRootIdentity, rootSemantics, ownsHandle: false);
        var replayed = new List<HeldDirectory>(components.Count);
        try
        {
            for (var index = 0; index < components.Count; index++)
            {
                var expected = expectedPath.Directories[index];
                if (IsReservedRootName(components[index], current.Semantics))
                    throw Refuse("The exact install parent path contains a reserved package-store authority directory.", expectedRoot);
                var child = OpenExactDirectory(files, names, directories,
                    current.Handle, current, components[index], expected.Identity, expectedRoot);
                if (child.Semantics != expected.Semantics)
                {
                    child.Dispose();
                    throw Unsupported("An install parent name profile changed during path replay.", expectedRoot);
                }
                replayed.Add(child);
                current = child;
            }

            return new OwnedDirectoryPath(replayed);
        }
        catch
        {
            DisposeDirectoriesReverse(replayed);
            throw;
        }
    }

    private static HeldDirectory OpenOrCreateDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        HeldDirectory parent,
        string name,
        PhysicalRootIdentity expectedRoot)
    {
        ValidateComponent(name, parent.Semantics, expectedRoot);
        RequireDirectory(files.InspectHandle(parent.Handle), expectedRoot,
            "A held package-path parent changed.", parent.Identity);
        RequireSemantics(names, parent.Handle, expectedRoot, parent.Semantics);

        var before = files.InspectChildNoFollow(parent.Handle, name);
        PhysicalStoreDirectoryHandle child;
        if (before is null)
        {
            child = files.CreateDirectoryExclusiveAt(parent.Handle, name);
        }
        else
        {
            RequireDirectory(before, expectedRoot, "An install-path component is not a no-follow directory.");
            child = files.OpenDirectoryChildNoFollow(parent.Handle, name);
        }

        try
        {
            var childInfo = RequireDirectory(files.InspectHandle(child), expectedRoot,
                "An install-path component did not open as a directory.", before?.Identity);
            RequireSameVolume(childInfo.Identity, expectedRoot.HandleIdentity,
                "An install-path component left the admitted volume.", expectedRoot);
            var childSemantics = ObserveSemantics(names, child, expectedRoot);
            var canonical = directories.ObserveCanonicalDirectoryNameNoFollow(parent.Handle, name, childInfo.Identity);
            RequireCanonicalDirectory(canonical, parent, name, childInfo.Identity, expectedRoot);
            RequireDirectory(files.InspectHandle(parent.Handle), expectedRoot,
                "An install-path parent changed during canonical-name observation.", parent.Identity);
            RequireSemantics(names, parent.Handle, expectedRoot, parent.Semantics);
            var namedAfter = RequireDirectory(files.InspectChildNoFollow(parent.Handle, name), expectedRoot,
                "An install-path directory changed during native observation.", childInfo.Identity);
            RequireDirectory(files.InspectHandle(child), expectedRoot,
                "A held install-path directory changed during native observation.", childInfo.Identity);
            if (namedAfter.Identity != childInfo.Identity)
                throw Refuse("An install-path directory changed identity during native observation.", expectedRoot);
            return new HeldDirectory(child, childInfo.Identity, childSemantics, ownsHandle: true);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private static HeldDirectory CreateNewDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle parentHandle,
        HeldDirectory parent,
        string name,
        PhysicalRootIdentity expectedRoot)
    {
        ValidateComponent(name, parent.Semantics, expectedRoot);
        RequireDirectory(files.InspectHandle(parentHandle), expectedRoot,
            "The staging parent changed before exclusive directory creation.", parent.Identity);
        RequireSemantics(names, parentHandle, expectedRoot, parent.Semantics);
        if (files.InspectChildNoFollow(parentHandle, name) is not null)
            throw Refuse("A unique native staging directory name already exists.", expectedRoot);

        var handle = files.CreateDirectoryExclusiveAt(parentHandle, name);
        try
        {
            var info = RequireDirectory(files.InspectHandle(handle), expectedRoot,
                "The exclusively created staging entry is not a directory.");
            RequireSameVolume(info.Identity, expectedRoot.HandleIdentity,
                "A staging directory left the admitted volume.", expectedRoot);
            var semantics = ObserveSemantics(names, handle, expectedRoot);
            var canonical = directories.ObserveCanonicalDirectoryNameNoFollow(parentHandle, name, info.Identity);
            RequireCanonicalDirectory(canonical, parent, name, info.Identity, expectedRoot);
            RequireDirectory(files.InspectHandle(parentHandle), expectedRoot,
                "The staging parent changed during canonical-name observation.", parent.Identity);
            RequireSemantics(names, parentHandle, expectedRoot, parent.Semantics);
            RequireDirectory(files.InspectChildNoFollow(parentHandle, name), expectedRoot,
                "The staging directory changed during native observation.", info.Identity);
            return new HeldDirectory(handle, info.Identity, semantics, ownsHandle: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static HeldDirectory OpenExactDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        PhysicalStoreDirectoryHandle parentHandle,
        HeldDirectory parent,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalRootIdentity expectedRoot)
    {
        var named = RequireDirectory(files.InspectChildNoFollow(parentHandle, name), expectedRoot,
            "The prepared directory is absent or changed kind.", expectedIdentity);
        var handle = files.OpenDirectoryChildNoFollow(parentHandle, name);
        try
        {
            RequireDirectory(files.InspectHandle(handle), expectedRoot,
                "The prepared directory changed while being reopened.", expectedIdentity);
            var semantics = ObserveSemantics(names, handle, expectedRoot);
            RequireCanonicalDirectory(directories.ObserveCanonicalDirectoryNameNoFollow(parentHandle, name, expectedIdentity),
                parent, name, expectedIdentity, expectedRoot);
            RequireDirectory(files.InspectChildNoFollow(parentHandle, name), expectedRoot,
                "The prepared directory name changed while being reopened.", named.Identity);
            RequireSemantics(names, parentHandle, expectedRoot, parent.Semantics);
            return new HeldDirectory(handle, expectedIdentity, semantics, ownsHandle: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static HeldDirectory EnsureOutputDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        HeldDirectory prepared,
        Dictionary<string, HeldDirectory> outputTree,
        List<HeldDirectory> ownedDirectories,
        IReadOnlyList<string> components,
        int componentCount,
        PhysicalRootIdentity expectedRoot)
    {
        var current = prepared;
        var path = new StringBuilder();
        for (var index = 0; index < componentCount; index++)
        {
            if (path.Length > 0)
                path.Append('/');
            path.Append(components[index]);
            var key = path.ToString();
            if (outputTree.TryGetValue(key, out var existing))
            {
                current = existing;
                continue;
            }

            var child = OpenOrCreateDirectory(files, names, directories, current,
                components[index], expectedRoot);
            if (child.Semantics != prepared.Semantics)
            {
                child.Handle.Dispose();
                throw Unsupported("A package directory has a different native name profile from its prepared root.", expectedRoot);
            }
            outputTree.Add(key, child);
            ownedDirectories.Add(child);
            current = child;
        }

        return current;
    }

    private static NativeFile CreateNewFile(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory parent,
        string name,
        PhysicalRootIdentity expectedRoot)
    {
        ValidateComponent(name, parent.Semantics, expectedRoot);
        RequireDirectory(files.InspectHandle(parent.Handle), expectedRoot,
            "A package-file parent changed before exclusive file creation.", parent.Identity);
        RequireSemantics(names, parent.Handle, expectedRoot, parent.Semantics);
        if (files.InspectChildNoFollow(parent.Handle, name) is not null)
            throw Refuse("A package-file destination already exists; overwrite is forbidden.", expectedRoot);

        var handle = files.CreateFileExclusiveAt(parent.Handle, name);
        try
        {
            var info = RequireRegularFile(files.InspectHandle(handle), expectedRoot,
                "The exclusively created package entry is not a regular single-link file.", parent.Identity, 0);
            RequireCanonicalFileName(names, parent, name, info, expectedRoot);
            return new NativeFile(handle, parent.Identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static NativeFile OpenExactFile(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory parent,
        string name,
        PhysicalStoreEntryInfo expectedFile,
        PhysicalRootIdentity expectedRoot)
    {
        RequireSameEntry(expectedFile,
            RequireRegularFile(files.InspectChildNoFollow(parent.Handle, name), expectedRoot,
                "The staged archive changed before it was reopened.", parent.Identity, expectedFile.Length),
            expectedRoot, "The staged archive changed before it was reopened.");
        var handle = files.OpenFileChildNoFollow(parent.Handle, name, FileAccess.Read);
        try
        {
            RequireSameEntry(expectedFile,
                RequireRegularFile(files.InspectHandle(handle), expectedRoot,
                    "The staged archive did not reopen as the exact single-link file.", parent.Identity,
                    expectedFile.Length), expectedRoot, "The staged archive changed while reopening.");
            RequireCanonicalFileName(names, parent, name, expectedFile, expectedRoot);
            return new NativeFile(handle, parent.Identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void WriteEvidence<T>(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle stagingDirectory,
        HeldDirectory staging,
        string name,
        T record,
        PhysicalRootIdentity expectedRoot)
    {
        var parentInfo = RequireDirectory(files.InspectHandle(stagingDirectory), expectedRoot,
            "The staging evidence parent changed.", staging.Identity);
        var semantics = ObserveSemantics(names, stagingDirectory, expectedRoot);
        if (semantics != staging.Semantics)
            throw Refuse("The staging evidence directory profile changed.", expectedRoot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > 16 * 1024)
            throw Refuse("The identity-bound staging evidence exceeded its size limit.", expectedRoot);
        WriteNewControlFile(files, names, staging, name, bytes, expectedRoot);
        RequireDirectory(files.InspectHandle(stagingDirectory), expectedRoot,
            "The staging evidence parent changed while writing.", parentInfo.Identity);
        RequireSemantics(names, stagingDirectory, expectedRoot, staging.Semantics);
    }

    private static void WritePublishedEvidenceBestEffort(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory staging,
        HeldDirectory parent,
        string operationId,
        string stagedName,
        string preparedName,
        string destinationName,
        string contentHash,
        PhysicalRootIdentity expectedRoot)
    {
        try
        {
            var published = RequireDirectory(files.InspectChildNoFollow(parent.Handle, destinationName), expectedRoot,
                "The published install directory could not be observed.");
            WriteEvidence(files, names, staging.Handle, staging, PublishedRecordName,
                new
                {
                    SchemaVersion = 1,
                    Phase = "published-and-verified",
                    OperationId = operationId,
                    ParentIdentity = parent.Identity,
                    StagingName = stagedName,
                    StagingIdentity = staging.Identity,
                    PreparedName = preparedName,
                    DestinationName = destinationName,
                    PublishedIdentity = published.Identity,
                    ContentHash = contentHash
                }, expectedRoot);
        }
        catch
        {
            // The earlier attempt/archive/prepared records remain the durable fail-closed evidence.
        }
    }

    private static void WriteNewControlFile(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory parent,
        string name,
        ReadOnlyMemory<byte> contents,
        PhysicalRootIdentity expectedRoot)
    {
        if (files.InspectChildNoFollow(parent.Handle, name) is not null)
            throw Refuse("A staging evidence record would replace an existing entry.", expectedRoot);
        using var handle = files.CreateFileExclusiveAt(parent.Handle, name);
        files.WriteNewControlFile(handle, contents);
        var info = RequireRegularFile(files.InspectHandle(handle), expectedRoot,
            "A staging evidence record is not a single-link regular file.", parent.Identity, contents.Length);
        RequireCanonicalFileName(names, parent, name, info, expectedRoot);
        RequireSameEntry(info,
            RequireRegularFile(files.InspectChildNoFollow(parent.Handle, name), expectedRoot,
                "A staging evidence record changed after writing.", parent.Identity, contents.Length),
            expectedRoot, "A staging evidence record changed after writing.");
    }

    private static void WriteNewPackageMetadata(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory packageDirectory,
        string name,
        ReadOnlyMemory<byte> contents,
        PhysicalRootIdentity expectedRoot)
    {
        WriteNewControlFile(files, names, packageDirectory, name, contents, expectedRoot);
    }

    private static ZipArchive OpenZipArchive(Stream stream, PhysicalRootIdentity expectedRoot)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw Refuse("The staged package is not a valid ZIP archive.", expectedRoot, exception);
        }
        catch (EndOfStreamException exception)
        {
            throw Refuse("The staged package ZIP is truncated.", expectedRoot, exception);
        }
    }

    private static IReadOnlyList<PlannedEntry> ValidateArchiveEntries(
        ZipArchive archive,
        PhysicalStoreNameSemantics semantics,
        PhysicalRootIdentity expectedRoot)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
            throw Refuse("The package ZIP entry count is empty or exceeds its limit.", expectedRoot);

        var comparer = PathComparer(semantics);
        var nodes = new Dictionary<string, PathNode>(comparer);
        var plan = new List<PlannedEntry>(archive.Entries.Count);
        long totalExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.Length > MaximumPathLength ||
                entry.FullName.IndexOf('\\') >= 0 ||
                entry.FullName.IndexOf('\0') >= 0)
                throw Refuse("A package ZIP entry has an empty, overlong, or unsupported path.", expectedRoot);

            var raw = entry.FullName;
            var trailingSeparator = raw.EndsWith("/", StringComparison.Ordinal);
            var attributes = unchecked((uint)entry.ExternalAttributes);
            var dosAttributes = (FileAttributes)(attributes & 0xFFFF);
            var unixType = (attributes >> 16) & 0xF000;
            if ((dosAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                (unixType != 0 && unixType is not (0x8000 or 0x4000)))
                throw Refuse("A package ZIP entry uses a link, device, or unfamiliar filesystem type.", expectedRoot);

            var typeDirectory = (dosAttributes & FileAttributes.Directory) != 0 || unixType == 0x4000;
            var typeFile = unixType == 0x8000;
            if (trailingSeparator != typeDirectory && (typeDirectory || typeFile))
                throw Refuse("A package ZIP entry's directory spelling disagrees with its declared type.", expectedRoot);
            var isDirectory = trailingSeparator;
            if (entry.Length < 0 || entry.CompressedLength < 0 ||
                (isDirectory && (entry.Length != 0 || entry.Crc32 != 0)))
                throw Refuse("A package ZIP entry has invalid declared lengths or directory data.", expectedRoot);
            if (!isDirectory)
            {
                if (entry.Length > MaximumEntryBytes)
                    throw Refuse("A package ZIP file entry exceeds its expanded byte limit.", expectedRoot);
                try { totalExpanded = checked(totalExpanded + entry.Length); }
                catch (OverflowException exception)
                {
                    throw Refuse("The package ZIP expanded byte count overflowed.", expectedRoot, exception);
                }
                if (totalExpanded > MaximumExpandedBytes)
                    throw Refuse("The package ZIP exceeds its total expanded byte limit.", expectedRoot);
            }

            var path = isDirectory ? raw[..^1] : raw;
            if (path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(path))
                throw Refuse("A package ZIP entry path is rooted or empty.", expectedRoot);
            var components = path.Split('/', StringSplitOptions.None);
            if (components.Length > MaximumPathDepth || components.Any(static component => component.Length == 0))
                throw Refuse("A package ZIP entry has excessive depth or an empty path component.", expectedRoot);
            if (IsReservedPackageMetadataName(components[0], semantics))
                throw Refuse("A package ZIP entry uses a reserved completion-metadata path.", expectedRoot);
            foreach (var component in components)
            {
                ValidateComponent(component, semantics, expectedRoot);
                if (IsReservedRootName(component, semantics))
                    throw Refuse("A package ZIP entry uses a reserved package-store authority directory.", expectedRoot);
            }
            AddPathToPlan(nodes, plan, components, isDirectory, entry, semantics, expectedRoot);
        }

        return plan;
    }

    private static void AddPathToPlan(
        Dictionary<string, PathNode> nodes,
        List<PlannedEntry> plan,
        string[] components,
        bool isDirectory,
        ZipArchiveEntry entry,
        PhysicalStoreNameSemantics semantics,
        PhysicalRootIdentity expectedRoot)
    {
        var path = new StringBuilder();
        for (var index = 0; index < components.Length - 1; index++)
        {
            if (path.Length > 0)
                path.Append('/');
            path.Append(components[index]);
            var key = path.ToString();
            if (!nodes.TryGetValue(key, out var parentNode))
            {
                nodes.Add(key, new PathNode(components[index], isDirectory: true));
            }
            else if (!string.Equals(parentNode.ExactName, components[index], StringComparison.Ordinal))
            {
                throw Refuse("The package ZIP contains a native name alias for a shared directory path.", expectedRoot);
            }
            else if (!parentNode.IsDirectory)
            {
                throw Refuse("A package ZIP file is also used as a parent directory.", expectedRoot);
            }
        }

        if (path.Length > 0)
            path.Append('/');
        path.Append(components[^1]);
        var finalKey = path.ToString();
        var reservedMetadataAlias = IsReservedPackageMetadataName(components[0], semantics);
        if (reservedMetadataAlias)
            throw Refuse("A package ZIP entry collides with reserved completion metadata.", expectedRoot);

        if (isDirectory)
        {
            if (nodes.TryGetValue(finalKey, out var existing))
            {
                if (!string.Equals(existing.ExactName, components[^1], StringComparison.Ordinal))
                    throw Refuse("The package ZIP contains a native name alias for one path.", expectedRoot);
                if (!existing.IsDirectory || existing.HasExplicitDirectory)
                    throw Refuse("The package ZIP contains a duplicate or conflicting directory path.", expectedRoot);
                existing.HasExplicitDirectory = true;
            }
            else
            {
                nodes.Add(finalKey, new PathNode(components[^1], isDirectory: true) { HasExplicitDirectory = true });
            }

            plan.Add(new PlannedEntry(components, true, entry));
            return;
        }

        if (nodes.TryGetValue(finalKey, out var finalNode))
        {
            if (!string.Equals(finalNode.ExactName, components[^1], StringComparison.Ordinal))
                throw Refuse("The package ZIP contains a native name alias for one path.", expectedRoot);
            throw Refuse("The package ZIP contains a duplicate file or file/directory path collision.", expectedRoot);
        }
        nodes.Add(finalKey, new PathNode(components[^1], isDirectory: false));
        plan.Add(new PlannedEntry(components, false, entry));
    }

    private static async Task<long> CopyEntryBoundedAsync(
        Stream input,
        Stream output,
        long declaredLength,
        uint expectedCrc32,
        long alreadyExpanded,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        var crc = uint.MaxValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (read > MaximumEntryBytes - written || read > MaximumExpandedBytes - alreadyExpanded - written)
                throw new InvalidDataException("The decompressed package entry exceeded its bounded size.");
            written += read;
            for (var index = 0; index < read; index++)
                crc = Crc32Table[(crc ^ buffer[index]) & 0xFF] ^ (crc >> 8);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (written != declaredLength || ~crc != expectedCrc32)
            throw new InvalidDataException("The decompressed package entry did not match its declared length or CRC-32.");
        return written;
    }

    private static bool IsCompleteMatchingWinner(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parentHandle,
        string destinationName,
        PhysicalFileIdentity destinationIdentity,
        string expectedHash,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken,
        out PhysicalStoreEntryInfo final)
    {
        try
        {
            var parentInfo = RequireDirectory(files.InspectHandle(parentHandle), expectedRoot,
                "The destination parent is no longer a directory.");
            var parentSemantics = ObserveSemantics(names, parentHandle, expectedRoot);
            var parent = new HeldDirectory(parentHandle, parentInfo.Identity, parentSemantics, ownsHandle: false);
            if (files.InspectChildNoFollow(parentHandle, destinationName) is not { } before ||
                before.Kind != PhysicalStoreEntryKind.Directory || before.Identity != destinationIdentity)
            {
                final = null!;
                return false;
            }

            if (files is not IPhysicalStoreDirectoryPublicationFileSystem directoryPublication)
            {
                final = null!;
                return false;
            }
            RequireCanonicalDirectory(directoryPublication.ObserveCanonicalDirectoryNameNoFollow(
                parentHandle, destinationName, destinationIdentity), parent, destinationName,
                destinationIdentity, expectedRoot);
            using var directory = files.OpenDirectoryChildNoFollow(parentHandle, destinationName);
            var actualDirectory = RequireDirectory(files.InspectHandle(directory), expectedRoot,
                "The completed destination could not be reopened.", destinationIdentity);
            if (!PackageInstallStore.IsInstalledNative(files, directory, expectedRoot))
            {
                final = null!;
                return false;
            }

            var actualHash = PackageInstallStore.ReadContentHashNative(files, directory, expectedRoot, cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                final = null!;
                return false;
            }

            final = actualDirectory;
            return true;
        }
        catch (PackageStoreAdmissionException)
        {
            final = null!;
            return false;
        }
    }

    private static PhysicalStoreEntryInfo ResolvePublished(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        HeldDirectory parent,
        string destinationName,
        string expectedHash,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        var entry = files.InspectChildNoFollow(parent.Handle, destinationName);
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory)
            throw Refuse("The no-replace install publication did not produce a destination directory.", expectedRoot);
        if (!IsCompleteMatchingWinner(files, names, parent.Handle, destinationName,
                entry.Identity, expectedHash, expectedRoot, cancellationToken, out var final))
            throw Refuse("The destination winner is incomplete or its canonical archive hash differs.", expectedRoot);
        var canonical = directories.ObserveCanonicalDirectoryNameNoFollow(parent.Handle, destinationName, final.Identity);
        RequireCanonicalDirectory(canonical, parent, destinationName, final.Identity, expectedRoot);
        return final;
    }

    private static bool TryResolvePublished(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        IPhysicalStoreDirectoryPublicationFileSystem directories,
        HeldDirectory parent,
        string destinationName,
        string expectedHash,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken,
        out PhysicalStoreEntryInfo final)
    {
        try
        {
            final = ResolvePublished(files, names, directories, parent, destinationName,
                expectedHash, expectedRoot, cancellationToken);
            return true;
        }
        catch
        {
            final = null!;
            return false;
        }
    }

    private static void EnsureDestinationAbsent(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        HeldDirectory parent,
        string destinationName,
        PhysicalRootIdentity expectedRoot)
    {
        RequireDirectory(files.InspectHandle(parent.Handle), expectedRoot,
            "The destination parent changed before publication.", parent.Identity);
        RequireSemantics(names, parent.Handle, expectedRoot, parent.Semantics);
        if (files.InspectChildNoFollow(parent.Handle, destinationName) is not null)
            throw Refuse("The deterministic destination appeared before no-replace publication.", expectedRoot);
        RequireDirectory(files.InspectHandle(parent.Handle), expectedRoot,
            "The destination parent changed during the absence check.", parent.Identity);
        RequireSemantics(names, parent.Handle, expectedRoot, parent.Semantics);
    }

    private static void RequireCanonicalFileName(
        IPhysicalStoreNameFileSystem names,
        HeldDirectory parent,
        string name,
        PhysicalStoreEntryInfo file,
        PhysicalRootIdentity expectedRoot)
    {
        var canonical = names.ObserveCanonicalFileNameNoFollow(parent.Handle, name, file.Identity);
        if (!string.Equals(canonical.Basename, name, StringComparison.Ordinal) ||
            canonical.ParentIdentity != parent.Identity || canonical.FileIdentity != file.Identity ||
            canonical.Semantics != parent.Semantics)
            throw Refuse("A package file is not its exact native canonical name/profile.", expectedRoot);
    }

    private static void RequireCanonicalDirectory(
        PhysicalStoreCanonicalName canonical,
        HeldDirectory parent,
        string name,
        PhysicalFileIdentity identity,
        PhysicalRootIdentity expectedRoot)
    {
        if (!string.Equals(canonical.Basename, name, StringComparison.Ordinal) ||
            canonical.ParentIdentity != parent.Identity || canonical.FileIdentity != identity ||
            canonical.Semantics != parent.Semantics)
            throw Refuse("A package directory is not its exact native canonical name/profile.", expectedRoot);
    }

    private static PhysicalStoreNameSemantics ObserveSemantics(
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity expectedRoot)
        => PackageInstallStore.RequireSupportedNameProfile(names.ObserveDirectoryNameSemantics(directory), expectedRoot);

    private static void RequireSemantics(
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreNameSemantics expected)
        => _ = PackageInstallStore.RequireSupportedNameProfile(
            names.ObserveDirectoryNameSemantics(directory), expectedRoot, expected);

    private static StringComparer PathComparer(PhysicalStoreNameSemantics semantics)
        => semantics.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static void ValidateComponent(
        string component,
        PhysicalStoreNameSemantics? semantics,
        PhysicalRootIdentity expectedRoot)
    {
        try
        {
            PhysicalStoreNames.ValidateSingleComponent(component);
        }
        catch (ArgumentException exception)
        {
            throw Refuse("A package path component is not one safe native name.", expectedRoot, exception);
        }

        int encodedLength;
        try
        {
            encodedLength = StrictUtf8.GetByteCount(component);
        }
        catch (EncoderFallbackException exception)
        {
            throw Refuse("A package path component is not valid strict UTF-8.", expectedRoot, exception);
        }

        if (component.Length > MaximumComponentBytes ||
            encodedLength > MaximumComponentBytes ||
            component.EndsWith(' ') || component.EndsWith('.') ||
            component.Any(char.IsControl) ||
            component.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            IsReservedWindowsDeviceName(component))
            throw Refuse("A package path component is not portable or exceeds its native name limit.", expectedRoot);

        // The native API reports whether names fold but does not expose the provider's full Unicode
        // equivalence function. ASCII-only names on folding/normalizing parents make alias checks exact.
        if (semantics is { } profile && (!profile.CaseSensitive || profile.NormalizationInsensitive) &&
            component.Any(static character => character > 0x7F))
            throw Unsupported("Non-ASCII package names cannot be compared safely under this native name profile.", expectedRoot);
    }

    private static bool IsReservedPackageMetadataName(string name, PhysicalStoreNameSemantics semantics)
    {
        var comparer = PathComparer(semantics);
        return comparer.Equals(name, PackageInstallStore.CompletionMarkerFileName) ||
               comparer.Equals(name, PackageInstallStore.ContentHashFileName);
    }

    private static bool IsReservedRootName(string name, PhysicalStoreNameSemantics semantics)
    {
        var comparer = PathComparer(semantics);
        return comparer.Equals(name, RootMembershipRegistry.ControlDirectoryName) || comparer.Equals(name, ".tmp");
    }

    private static bool IsReservedWindowsDeviceName(string component)
    {
        var stem = component.Split('.', 2)[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static PhysicalStoreEntryInfo RequireRegularFile(
        PhysicalStoreEntryInfo? entry,
        PhysicalRootIdentity expectedRoot,
        string message,
        PhysicalFileIdentity? expectedParent,
        long? expectedLength)
    {
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1 ||
            (expectedLength is not null && entry.Length != expectedLength.Value))
            throw Refuse(message, expectedRoot);
        if (expectedParent is not null)
            RequireSameVolume(entry.Identity, expectedParent, message, expectedRoot);
        RequireSameVolume(entry.Identity, expectedRoot.HandleIdentity, message, expectedRoot);
        return entry;
    }

    private static PhysicalStoreEntryInfo RequireDirectory(
        PhysicalStoreEntryInfo? entry,
        PhysicalRootIdentity expectedRoot,
        string message,
        PhysicalFileIdentity? expectedIdentity = null)
    {
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory ||
            (expectedIdentity is not null && entry.Identity != expectedIdentity))
            throw Refuse(message, expectedRoot);
        RequireSameVolume(entry.Identity, expectedRoot.HandleIdentity, message, expectedRoot);
        return entry;
    }

    private static void RequireSameEntry(
        PhysicalStoreEntryInfo expected,
        PhysicalStoreEntryInfo actual,
        PhysicalRootIdentity expectedRoot,
        string message)
    {
        if (expected.Kind != actual.Kind || expected.Identity != actual.Identity ||
            expected.LinkCount != actual.LinkCount || expected.Length != actual.Length)
            throw Refuse(message, expectedRoot);
    }

    private static void RequireSameVolume(
        PhysicalFileIdentity identity,
        PhysicalFileIdentity expected,
        string message,
        PhysicalRootIdentity expectedRoot)
    {
        if (!string.Equals(identity.Provider, expected.Provider, StringComparison.Ordinal) ||
            !string.Equals(identity.VolumeOrDeviceId, expected.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse(message, expectedRoot);
    }

    private static void DisposeDirectoriesReverse(List<HeldDirectory> directories)
    {
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            if (directories[index].OwnsHandle)
                directories[index].Handle.Dispose();
        }
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : 0xEDB88320U ^ (value >> 1);
            table[index] = value;
        }
        return table;
    }

    private static PackageStoreAdmissionException Refuse(
        string message,
        PhysicalRootIdentity? root,
        Exception? inner = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root, inner);

    private static PackageStoreAdmissionException Unsupported(string message, PhysicalRootIdentity? root)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message, root);

    private sealed record InstallTarget(
        string InstallPath,
        string[] ParentComponents,
        string DestinationName,
        string RootRelativePath);

    private sealed class HeldDirectory(
        PhysicalStoreDirectoryHandle handle,
        PhysicalFileIdentity identity,
        PhysicalStoreNameSemantics semantics,
        bool ownsHandle) : IDisposable
    {
        internal PhysicalStoreDirectoryHandle Handle { get; } = handle;
        internal PhysicalFileIdentity Identity { get; } = identity;
        internal PhysicalStoreNameSemantics Semantics { get; } = semantics;
        internal bool OwnsHandle { get; } = ownsHandle;
        public void Dispose()
        {
            if (OwnsHandle)
                Handle.Dispose();
        }
    }

    private sealed class NativeFile(
        PhysicalStoreFileHandle handle,
        PhysicalFileIdentity parentIdentity) : IDisposable
    {
        internal PhysicalStoreFileHandle Handle { get; } = handle;
        internal PhysicalFileIdentity ParentIdentity { get; } = parentIdentity;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class OwnedDirectoryPath(List<HeldDirectory> directories) : IDisposable
    {
        internal IReadOnlyList<HeldDirectory> Directories => directories;
        internal HeldDirectory Last => directories[^1];
        public void Dispose() => DisposeDirectoriesReverse(directories);
    }

    private sealed record PlannedEntry(string[] Components, bool IsDirectory, ZipArchiveEntry? ArchiveEntry);

    private sealed class PathNode(string exactName, bool isDirectory)
    {
        internal string ExactName { get; } = exactName;
        internal bool IsDirectory { get; } = isDirectory;
        internal bool HasExplicitDirectory { get; set; }
    }
}
