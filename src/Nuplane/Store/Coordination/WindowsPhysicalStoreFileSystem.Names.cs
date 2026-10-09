using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem
{
    private const string NameProfileId = "windows-ntfs-name-v1";

    /// <inheritdoc />
    public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedFileIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedFileIdentity);
        ValidateName(singleName);
        RequireSupportedPlatform();

        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var parentState = GetState(parent);
        var parentBefore = QueryEntry(parentLease.DangerousHandle, "inspect the held parent before observing a canonical name");
        RequireKind(parentBefore, PhysicalStoreEntryKind.Directory, "The canonical-name parent is not a directory.");
        if (parentBefore.Identity != parentState.Identity)
            throw Unknown("The canonical-name parent identity changed before observation.");

        var semanticsBefore = ObserveNameSemantics(parentLease.DangerousHandle);
        using var original = OpenFileChildNoFollow(parent, singleName, FileAccess.Read);
        var fileBefore = InspectHandle(original);
        RequireControlFile(fileBefore, "Canonical-name observation requires a regular single-link file.");
        if (fileBefore.Identity != expectedFileIdentity)
            throw Unknown("The supplied component does not resolve to the expected file identity.");

        string normalizedPath;
        using (var originalLease = original.AcquireScopedSafeHandle(_providerToken))
        {
            try
            {
                normalizedPath = WindowsNative.GetNormalizedVolumePath(originalLease.DangerousHandle);
            }
            catch (WindowsNativeCallException exception)
            {
                throw NativeFailure("observe the canonical file spelling", exception);
            }
        }

        var separator = normalizedPath.LastIndexOf('\\');
        if (separator < 0 || separator == normalizedPath.Length - 1)
            throw Unknown("The normalized file path did not provide a leaf-name candidate.");

        var basename = normalizedPath[(separator + 1)..];
        try
        {
            PhysicalStoreCanonicalName.ValidateBasename(basename, semanticsBefore);
        }
        catch (ArgumentException exception)
        {
            throw Unknown("The normalized file path returned an invalid canonical basename.", exception);
        }

        using var canonical = OpenFileChildNoFollow(parent, basename, FileAccess.Read);
        var canonicalInfo = InspectHandle(canonical);
        using var suppliedAfter = OpenFileChildNoFollow(parent, singleName, FileAccess.Read);
        var suppliedInfo = InspectHandle(suppliedAfter);
        var originalAfter = InspectHandle(original);
        var parentAfter = QueryEntry(parentLease.DangerousHandle, "recheck the held parent after observing a canonical name");
        var semanticsAfter = ObserveNameSemantics(parentLease.DangerousHandle);
        RequireControlFile(canonicalInfo, "The canonical basename did not reopen as a regular single-link file.");
        RequireControlFile(suppliedInfo, "The supplied name no longer resolves to a regular single-link file.");
        RequireControlFile(originalAfter, "The held file changed while its canonical basename was observed.");

        if (canonicalInfo.Identity != expectedFileIdentity || suppliedInfo.Identity != expectedFileIdentity ||
            originalAfter.Identity != expectedFileIdentity ||
            parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentBefore.Identity ||
            semanticsAfter != semanticsBefore)
        {
            throw Unknown("The file, parent, or native name profile changed during canonical-name observation.");
        }

        return new PhysicalStoreCanonicalName(parentBefore.Identity, expectedFileIdentity, basename, semanticsBefore);
    }

    private static PhysicalStoreNameSemantics ObserveNameSemantics(IntPtr directoryHandle)
    {
        try
        {
            return new PhysicalStoreNameSemantics(
                NameProfileId,
                PhysicalStoreNameEncoding.Utf16LittleEndian,
                WindowsNative.QueryDirectoryCaseSensitive(directoryHandle),
                normalizationInsensitive: false);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("query the held directory's native name profile", exception);
        }
    }
}
