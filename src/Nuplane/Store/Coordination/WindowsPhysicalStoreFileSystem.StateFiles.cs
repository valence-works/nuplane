using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreStateFileSystem
{
    /// <inheritdoc />
    public Stream OpenStatePayloadReadStream(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo expectedEntry,
        long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(expectedEntry);
        RequireSupportedPlatform();
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        PhysicalStoreStateFileChecks.RequireRegular(expectedEntry);
        if (expectedEntry.Length > maximumBytes)
            throw Unknown("The state payload exceeds the requested read limit.");

        PhysicalStoreSafeHandleLease? parentLease = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        SafeFileHandle? opened = null;
        try
        {
            parentLease = parent.AcquireScopedSafeHandle(_providerToken);
            var parentRaw = parentLease.DangerousHandle;
            PhysicalStoreStateFileChecks.RequireEntry(ObserveStateEntryForIo(parentRaw, slot), expectedEntry);
            opened = WindowsNative.OpenRelative(parentRaw, slot.CanonicalBasename,
                WindowsNative.FileReadAttributes | WindowsNative.FileReadData | WindowsNative.Synchronize,
                WindowsNative.FileOpen, WindowsNative.FileNonDirectoryFile,
                shareAccess: WindowsNative.ShareRead | WindowsNative.ShareWrite | WindowsNative.ShareDelete);
            using var file = OwnFile(opened, createdExclusive: false, expectedEntry.Identity);
            opened = null;
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var fileRaw = fileLease.DangerousHandle;

            void Validate(long length)
            {
                InvokePackageNative("revalidate a held state payload", () =>
                {
                    PhysicalStoreStateFileChecks.RequireEntry(ObserveStateEntryForIo(parentRaw, slot), expectedEntry);
                    var actual = QueryEntry(fileRaw, "revalidate the held state file");
                    PhysicalStoreStateFileChecks.RequireEntry(actual, expectedEntry);
                    if (length != expectedEntry.Length)
                        throw Unknown("The state stream length differs from its retained observation.");
                    RequireSameVolume(slot.ParentIdentity, actual.Identity);
                    RequireStateIoParent(parentRaw, slot);
                });
            }

            Validate(expectedEntry.Length);
            var result = new BoundedPhysicalStoreFileStream(parentLease, fileLease, maximumBytes, expectedEntry.Length,
                writable: false, validateBinding: Validate,
                readAt: (position, buffer, offset, count) => InvokePackageNative("read a held state payload",
                    () => WindowsNative.ReadPackageFileAt(fileRaw, position, buffer, offset, count)),
                writeAt: static (_, _, _, _) => throw new NotSupportedException(), flush: static () => { });
            parentLease = null;
            fileLease = null;
            return result;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("open a selected state payload without following links", exception);
        }
        finally
        {
            opened?.Dispose();
            fileLease?.Dispose();
            parentLease?.Dispose();
        }
    }

    /// <inheritdoc />
    public PhysicalStoreEntryInfo PublishStateFileAt(
        PhysicalStoreDirectoryHandle parent,
        string stagedName,
        PhysicalFileIdentity expectedStagedIdentity,
        StateSlotIdentity destination,
        PhysicalStoreEntryInfo? expectedPriorEntry)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedStagedIdentity);
        ArgumentNullException.ThrowIfNull(destination);
        RequireSupportedPlatform();
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var staged = PhysicalStoreStateFileChecks.PreparePublication(
            this, this, parent, stagedName, expectedStagedIdentity, destination, expectedPriorEntry);
        var parentRaw = parentLease.DangerousHandle;
        try
        {
            using (var source = OpenPublicationFile(parentRaw, stagedName, requestDelete: true))
            {
                PhysicalStorePublicationChecks.RequireExpectedEntry(
                    QueryEntry(source.DangerousGetHandle(), "inspect the staged state publication handle"), expectedStagedIdentity);
                RecheckPublicationEntry(parentRaw, stagedName, expectedStagedIdentity);
                PhysicalStoreStateFileChecks.RequireEntry(ObserveStateEntryForIo(parentRaw, destination), expectedPriorEntry);
                using var prior = expectedPriorEntry is not null && WindowsNative.SupportsPosixRenameReplacement
                    ? OpenPublicationFile(parentRaw, destination.CanonicalBasename, requestDelete: true)
                    : null;
                if (prior is not null)
                    PhysicalStoreStateFileChecks.RequireEntry(
                        QueryEntry(prior.DangerousGetHandle(), "inspect the prior lexical state entry"), expectedPriorEntry);
                RequireStateIoParent(parentRaw, destination);
                WindowsNative.RenameControlFile(source.DangerousGetHandle(), parentRaw,
                    destination.CanonicalBasename, replace: expectedPriorEntry is not null);
                PhysicalStorePublicationChecks.RequireExpectedEntry(
                    QueryEntry(source.DangerousGetHandle(), "inspect the published state handle"), expectedStagedIdentity);
                RequireStateIoParent(parentRaw, destination);
            }

            var published = PhysicalStoreStateFileChecks.VerifyPublication(this, this, parent, stagedName, destination, staged);
            PhysicalStoreStateFileChecks.RequireEntry(ObserveStateEntryForIo(parentRaw, destination), published);
            return published;
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("publish the selected lexical state entry", exception);
        }
    }

    private PhysicalStoreEntryInfo? ObserveStateEntryForIo(IntPtr parentRaw, StateSlotIdentity slot)
    {
        var parentNative = WindowsNative.QueryEntry(parentRaw);
        var parent = ToEntryInfo(parentNative);
        RequireStateIoParent(parentRaw, slot);
        using var entry = OpenChildForInspection(parentRaw, slot.CanonicalBasename);
        if (entry is null)
        {
            RequireStateIoParent(parentRaw, slot);
            return null;
        }

        var record = QueryStateDestinationEntryOnFreshParent(parentRaw, parentNative, parent,
            slot.NameSemantics, slot.CanonicalBasename);
        RequireSelectedDirectoryRecord(record, slot.CanonicalBasename, slot.NameSemantics);
        if (!string.Equals(record.FileName, slot.CanonicalBasename, StringComparison.Ordinal))
            throw Unknown("Windows state I/O requires the selected entry's observed stored long name.");
        var nativeEntry = WindowsNative.QueryEntry(entry.DangerousGetHandle());
        RequireDirectoryRecordMatches(record, nativeEntry);
        var observed = ToEntryInfo(nativeEntry);
        RequireSameVolume(slot.ParentIdentity, observed.Identity);
        RequireStateIoParent(parentRaw, slot);
        return observed;
    }

    private void RequireStateIoParent(IntPtr parentRaw, StateSlotIdentity slot)
    {
        PhysicalStoreStateFileChecks.RequireParent(QueryEntry(parentRaw, "inspect the retained state parent"), slot);
        if (ObserveNameSemantics(parentRaw) != slot.NameSemantics)
            throw Unknown("The retained state parent lookup profile changed.");
    }
}
