using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreDirectoryPublicationFileSystem
{
    /// <inheritdoc />
    public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName)
    {
        RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedStagedIdentity);
        ValidateName(stagedName);
        ValidateName(destinationName);

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var prepared = PhysicalStoreDirectoryPublicationChecks.PreparePublish(
            this, this, parent, stagedName, expectedStagedIdentity, destinationName);
        var parentRaw = parentLease.DangerousHandle;
        try
        {
            // Canonical and source observation handles are closed before this DELETE-capable open.
            // The caller also closes every staged descendant; this method never reopens a path.
            using (var staged = OpenPublicationDirectory(parentRaw, stagedName, requestDelete: true))
            {
                var stagedInfo = QueryEntry(staged.DangerousGetHandle(), "inspect the staged directory publication handle");
                PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(stagedInfo, expectedStagedIdentity);
                RequireSameVolume(prepared.ParentIdentity, stagedInfo.Identity);
                RecheckPublicationDirectory(parentRaw, stagedName, expectedStagedIdentity, prepared.ParentIdentity);
                RecheckPublicationDestinationAbsent(parentRaw, destinationName);
                RequirePublicationParent(parent, parentRaw, prepared);

                WindowsNative.RenameDirectoryNoReplace(
                    staged.DangerousGetHandle(), parentRaw, destinationName);

                var publishedInfo = QueryEntry(staged.DangerousGetHandle(), "recheck the published directory handle");
                PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(publishedInfo, expectedStagedIdentity);
                RequireSameVolume(prepared.ParentIdentity, publishedInfo.Identity);
                RequirePublicationParent(parent, parentRaw, prepared);
            }
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("publish a staged directory relative to its held parent without replacement", exception);
        }

        return PhysicalStoreDirectoryPublicationChecks.VerifyPublished(
            this, this, parent, stagedName, destinationName, prepared);
    }

    private static SafeFileHandle OpenPublicationDirectory(IntPtr parent, string name, bool requestDelete)
        => WindowsNative.OpenRelative(
            parent,
            name,
            WindowsNative.FileReadAttributes | WindowsNative.FileListDirectory | WindowsNative.Synchronize |
            (requestDelete ? WindowsNative.DeleteAccess : 0),
            WindowsNative.FileOpen,
            WindowsNative.FileDirectoryFile,
            shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);

    private static void RecheckPublicationDirectory(
        IntPtr parent,
        string name,
        PhysicalFileIdentity expectedIdentity,
        PhysicalFileIdentity expectedParentIdentity)
    {
        using var opened = OpenPublicationDirectory(parent, name, requestDelete: false);
        var entry = QueryEntry(opened.DangerousGetHandle(), "recheck a directory publication entry");
        PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(entry, expectedIdentity);
        RequireSameVolume(expectedParentIdentity, entry.Identity);
    }

    private static void RecheckPublicationDestinationAbsent(IntPtr parent, string name)
    {
        SafeFileHandle? opened = null;
        try
        {
            opened = WindowsNative.OpenRelative(
                parent,
                name,
                WindowsNative.FileReadAttributes | WindowsNative.Synchronize,
                WindowsNative.FileOpen,
                createOptions: 0,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);
            _ = QueryEntry(opened.DangerousGetHandle(), "recheck an absent directory publication destination");
            throw Unknown("Directory publication requires a positively absent destination entry.");
        }
        catch (WindowsNativeCallException exception) when
            (exception.NtStatus is { } status && WindowsNative.IsPositiveAbsence(status))
        {
            // A same-parent native no-replace rename remains the final collision guard.
        }
        finally
        {
            opened?.Dispose();
        }
    }
}
