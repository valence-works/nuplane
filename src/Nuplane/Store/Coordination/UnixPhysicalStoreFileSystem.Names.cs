using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem
{
    private const int MaximumCanonicalNameBytes = 4096;
    private static readonly UTF8Encoding StrictNameUtf8 = new(false, true);

    /// <inheritdoc />
    public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedFileIdentity)
    {
        var platform = RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedFileIdentity);
        ValidateName(singleName);

        var parentBefore = InspectHandle(parent);
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("Canonical-name observation requires a held directory.");

        using var original = OpenFileChildNoFollow(parent, singleName, FileAccess.Read);
        var fileBefore = InspectHandle(original);
        RequireControlFile(fileBefore, "The requested entry is not a single-link regular file.");
        if (fileBefore.Identity != expectedFileIdentity)
            throw Unknown("The supplied component does not identify the expected held file.");

        UnixNameProfile profileBefore;
        byte[] nameBytes;
        using (var parentScope = parent.AcquireScopedSafeHandle(_providerToken))
        using (var fileScope = original.AcquireScopedSafeHandle(_providerToken))
        {
            var parentFd = GetFileDescriptor(parentScope);
            var fileFd = GetFileDescriptor(fileScope);
            profileBefore = InvokeNative("inspect directory name semantics", () => UnixNative.GetNameProfile(platform, parentFd));
            nameBytes = InvokeNative(
                "observe canonical directory-entry name",
                () => UnixNative.FindEntryName(platform, parentFd, fileFd, MaximumCanonicalNameBytes));
        }

        string canonicalBasename;
        try
        {
            canonicalBasename = StrictNameUtf8.GetString(nameBytes);
            PhysicalStoreNames.ValidateSingleComponent(canonicalBasename);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw Unknown("The native directory entry did not provide one valid UTF-8 basename.", exception);
        }

        var named = InspectChildNoFollow(parent, canonicalBasename);
        RequireControlFile(named, "The enumerated canonical entry is absent, linked, or not a regular file.");
        if (named!.Identity != fileBefore.Identity)
            throw Unknown("The canonical basename now resolves to a different file.");

        using var canonical = OpenFileChildNoFollow(parent, canonicalBasename, FileAccess.Read);
        var canonicalInfo = InspectHandle(canonical);
        var fileAfter = InspectHandle(original);
        var parentAfter = InspectHandle(parent);
        var suppliedAfter = InspectChildNoFollow(parent, singleName);
        var profileAfter = InvokeNative(
            "recheck directory name semantics",
            () =>
            {
                using var parentScope = parent.AcquireScopedSafeHandle(_providerToken);
                return UnixNative.GetNameProfile(platform, GetFileDescriptor(parentScope));
            });
        RequireControlFile(canonicalInfo, "The reopened canonical entry is not a single-link regular file.");
        RequireControlFile(fileAfter, "The originally observed file changed during name observation.");
        RequireControlFile(suppliedAfter, "The originally supplied entry changed during name observation.");
        if (canonicalInfo.Identity != fileBefore.Identity ||
            fileAfter.Identity != fileBefore.Identity ||
            suppliedAfter!.Identity != fileBefore.Identity ||
            parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
            parentAfter.Identity != parentBefore.Identity ||
            profileAfter != profileBefore)
        {
            throw Unknown("The parent, canonical entry, or lookup profile changed during name observation.");
        }

        return new PhysicalStoreCanonicalName(
            parentBefore.Identity,
            fileBefore.Identity,
            canonicalBasename,
            new PhysicalStoreNameSemantics(
                profileBefore.ProfileId,
                PhysicalStoreNameEncoding.Utf8,
                profileBefore.CaseSensitive,
                profileBefore.NormalizationInsensitive));
    }
}
