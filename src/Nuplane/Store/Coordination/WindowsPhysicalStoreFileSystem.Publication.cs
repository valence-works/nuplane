using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStorePublicationFileSystem
{
    /// <inheritdoc />
    public PhysicalStoreEntryInfo PublishControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        string destinationName,
        PhysicalFileIdentity? expectedDestinationIdentity)
    {
        RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(stagedName);
        ValidateName(destinationName);
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var prepared = PhysicalStorePublicationChecks.PreparePublish(
            this, this, parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        var parentRaw = parentLease.DangerousHandle;
        try
        {
            // The DELETE-capable source handle must be closed before ordinary no-delete-sharing
            // canonical reopens. Publication-local inspection opens explicitly share DELETE.
            using (var staged = OpenPublicationFile(parentRaw, stagedName, requestDelete: true))
            {
                PhysicalStorePublicationChecks.RequireExpectedEntry(
                    QueryEntry(staged.DangerousGetHandle(), "inspect the staged publication handle"), expectedStagedIdentity);
                RecheckPublicationEntry(parentRaw, stagedName, expectedStagedIdentity);

                // POSIX replacement allows the old file to remain open, so retain a DELETE-capable
                // destination handle through the rename. Its share-mode check refuses existing
                // handles that did not allow delete sharing and prevents a new incompatible open
                // from racing the transition. Release it before the ordinary canonical reopen.
                using var priorDestination = expectedDestinationIdentity is not null && WindowsNative.SupportsPosixRenameReplacement
                    ? OpenPublicationFile(parentRaw, destinationName, requestDelete: true)
                    : null;
                if (priorDestination is not null)
                {
                    PhysicalStorePublicationChecks.RequireExpectedEntry(
                        QueryEntry(priorDestination.DangerousGetHandle(), "inspect the prior publication handle"),
                        expectedDestinationIdentity);
                }
                else
                    RecheckPublicationEntry(parentRaw, destinationName, expectedDestinationIdentity);

                RequirePublicationParent(parent, parentRaw, prepared);
                WindowsNative.RenameControlFile(
                    staged.DangerousGetHandle(), parentRaw, destinationName, replace: expectedDestinationIdentity is not null);
                PhysicalStorePublicationChecks.RequireExpectedEntry(
                    QueryEntry(staged.DangerousGetHandle(), "recheck the published file handle"), expectedStagedIdentity);
                RequirePublicationParent(parent, parentRaw, prepared);
            }
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("publish a staged control file relative to its held parent", exception);
        }

        return PhysicalStorePublicationChecks.VerifyPublished(this, this, parent, stagedName, destinationName, prepared);
    }

    /// <inheritdoc />
    public void RemoveControlFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateName(singleName);
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var prepared = PhysicalStorePublicationChecks.PrepareRemoval(this, this, parent, singleName, expectedIdentity);
        var parentRaw = parentLease.DangerousHandle;
        try
        {
            using (var file = OpenPublicationFile(parentRaw, singleName, requestDelete: true))
            {
                PhysicalStorePublicationChecks.RequireExpectedEntry(
                    QueryEntry(file.DangerousGetHandle(), "inspect the transaction-control removal handle"), expectedIdentity);
                RecheckPublicationEntry(parentRaw, singleName, expectedIdentity);
                RequirePublicationParent(parent, parentRaw, prepared);
                WindowsNative.RemoveControlFile(file.DangerousGetHandle());
            }

            // A successful disposition is not reported as removal until close and positive absence.
            RequirePublicationParent(parent, parentRaw, prepared);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("remove a transaction-control file relative to its held parent", exception);
        }

        PhysicalStorePublicationChecks.VerifyRemoved(this, parent, singleName, prepared);
    }

    private static SafeFileHandle OpenPublicationFile(IntPtr parent, string name, bool requestDelete)
        => WindowsNative.OpenRelative(
            parent,
            name,
            WindowsNative.FileReadAttributes | WindowsNative.Synchronize | (requestDelete ? WindowsNative.DeleteAccess : 0),
            WindowsNative.FileOpen,
            WindowsNative.FileNonDirectoryFile,
            shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);

    private static void RecheckPublicationEntry(IntPtr parent, string name, PhysicalFileIdentity? expectedIdentity)
    {
        SafeFileHandle? opened = null;
        try
        {
            opened = OpenPublicationFile(parent, name, requestDelete: false);
            PhysicalStorePublicationChecks.RequireExpectedEntry(
                QueryEntry(opened.DangerousGetHandle(), "recheck a publication entry"), expectedIdentity);
        }
        catch (WindowsNativeCallException exception) when (exception.NtStatus is { } status && WindowsNative.IsPositiveAbsence(status))
        {
            PhysicalStorePublicationChecks.RequireExpectedEntry(null, expectedIdentity);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private void RequirePublicationParent(
        PhysicalStoreDirectoryHandle parent,
        IntPtr parentRaw,
        PhysicalStoreCanonicalName prepared)
    {
        PhysicalStorePublicationChecks.RequireParent(this, parent, prepared.ParentIdentity);
        if (ObserveNameSemantics(parentRaw) != prepared.Semantics)
            throw Unknown("The native publication parent name profile changed.");
    }
}
