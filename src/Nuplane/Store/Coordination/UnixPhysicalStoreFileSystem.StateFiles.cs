using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreStateFileSystem
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
        var platform = RequireSupportedPlatform();
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        PhysicalStoreStateFileChecks.RequireRegular(expectedEntry);
        if (expectedEntry.Length > maximumBytes)
            throw Unknown("The state payload exceeds the requested read limit.");

        PhysicalStoreSafeHandleLease? parentLease = null;
        PhysicalStoreSafeHandleLease? fileLease = null;
        try
        {
            parentLease = parent.AcquireScopedSafeHandle(_providerToken);
            var parentFd = GetFileDescriptor(parentLease);
            RequirePublicationNameProfile(platform, parentFd, slot.NameSemantics);
            PhysicalStoreStateFileChecks.RequireParent(InspectHandle(parent), slot);
            PhysicalStoreStateFileChecks.RequireEntry(InspectChildNoFollow(parent, slot.CanonicalBasename), expectedEntry);
            using var file = OwnFile(InvokeNative("open a selected state payload without following links",
                () => UnixNative.OpenFileAt(platform, parentFd, slot.CanonicalBasename, FileAccess.Read)), createdExclusive: false);
            fileLease = file.AcquireScopedSafeHandle(_providerToken);
            var fileFd = GetFileDescriptor(fileLease);

            void Validate(long length)
            {
                RequirePublicationNameProfile(platform, parentFd, slot.NameSemantics);
                PhysicalStoreStateFileChecks.RequireParent(ToEntryInfo(InvokeNative("revalidate the state parent",
                    () => UnixNative.StatHandle(platform, parentFd))), slot);
                var opened = ToEntryInfo(InvokeNative("revalidate the held state file", () => UnixNative.StatHandle(platform, fileFd)));
                PhysicalStoreStateFileChecks.RequireEntry(opened, expectedEntry);
                var named = InvokeNative("revalidate the selected state entry", () => UnixNative.StatAt(platform, parentFd, slot.CanonicalBasename));
                if (named.Status == UnixStatResultStatus.Failure)
                    throw NativeFailure("revalidate the selected state entry", named.Error);
                PhysicalStoreStateFileChecks.RequireEntry(named.Status == UnixStatResultStatus.Absent ? null : ToEntryInfo(named.Metadata), expectedEntry);
                if (length != expectedEntry.Length || opened.Identity.VolumeOrDeviceId != slot.ParentIdentity.VolumeOrDeviceId)
                    throw Unknown("The state payload length or volume differs from its retained evidence.");
                RequirePublicationNameProfile(platform, parentFd, slot.NameSemantics);
            }

            Validate(expectedEntry.Length);
            var result = new BoundedPhysicalStoreFileStream(parentLease, fileLease, maximumBytes, expectedEntry.Length,
                writable: false, validateBinding: Validate,
                readAt: (position, buffer, offset, count) => InvokeNative("read a held state payload",
                    () => UnixNative.ReadPackageFileAt(platform, fileFd, buffer, offset, count, position)),
                writeAt: static (_, _, _, _) => throw new NotSupportedException(), flush: static () => { });
            parentLease = null;
            fileLease = null;
            return result;
        }
        finally
        {
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
        var platform = RequireSupportedPlatform();
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentLease);
        var staged = PhysicalStoreStateFileChecks.PreparePublication(
            this, this, parent, stagedName, expectedStagedIdentity, destination, expectedPriorEntry);
        RequirePublicationNameProfile(platform, parentFd, destination.NameSemantics);
        PhysicalStoreStateFileChecks.RequireParent(InspectHandle(parent), destination);
        PhysicalStorePublicationChecks.RequireExpectedEntry(InspectChildNoFollow(parent, stagedName), expectedStagedIdentity);
        PhysicalStoreStateFileChecks.RequireEntry(InspectChildNoFollow(parent, destination.CanonicalBasename), expectedPriorEntry);
        InvokeNative("publish the selected lexical state entry", () => expectedPriorEntry is null
            ? UnixNative.PublishNoReplaceAt(platform, parentFd, stagedName, destination.CanonicalBasename)
            : UnixNative.PublishReplaceAt(platform, parentFd, stagedName, destination.CanonicalBasename));
        RequirePublicationNameProfile(platform, parentFd, destination.NameSemantics);
        return PhysicalStoreStateFileChecks.VerifyPublication(this, this, parent, stagedName, destination, staged);
    }
}
