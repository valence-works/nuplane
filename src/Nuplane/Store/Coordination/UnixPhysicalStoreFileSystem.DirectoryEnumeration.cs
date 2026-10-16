using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Coordination;

internal sealed partial class UnixPhysicalStoreFileSystem : IPhysicalStoreDirectoryEnumerationFileSystem
{
    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateChildNamesNoFollow(
        PhysicalStoreDirectoryHandle parent,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries), "The child-name entry bound must be positive.");

        var platform = RequireSupportedPlatform();
        using var parentHandle = parent.AcquireScopedSafeHandle(_providerToken);
        var parentFd = GetFileDescriptor(parentHandle);

        var parentBefore = ToEntryInfo(InvokeNative(
            "inspect held directory before enumerating child names",
            () => UnixNative.StatHandle(platform, parentFd)));
        if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("Child-name enumeration requires a held directory.");

        var profileBefore = InvokeNative(
            "inspect held directory name semantics before enumerating child names",
            () => UnixNative.GetNameProfile(platform, parentFd));

        byte[][] nativeNames;
        try
        {
            nativeNames = InvokeNative(
                "enumerate held-directory child names",
                () => UnixNative.EnumerateChildNameBytes(platform, parentFd, maximumEntries));
        }
        finally
        {
            var profileAfter = InvokeNative(
                "recheck held directory name semantics after enumerating child names",
                () => UnixNative.GetNameProfile(platform, parentFd));
            var parentAfter = ToEntryInfo(InvokeNative(
                "recheck held directory after enumerating child names",
                () => UnixNative.StatHandle(platform, parentFd)));

            if (parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
                parentAfter.Identity != parentBefore.Identity ||
                profileAfter != profileBefore)
            {
                throw Unknown("The held directory identity, kind, or native name profile changed during child-name enumeration.");
            }
        }

        if (nativeNames.Length > maximumEntries)
            throw Unknown("The Unix provider returned more child names than the requested bound.");

        var names = new List<string>(nativeNames.Length);
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bytes in nativeNames)
        {
            if ((bytes.Length == 1 && bytes[0] == (byte)'.') ||
                (bytes.Length == 2 && bytes[0] == (byte)'.' && bytes[1] == (byte)'.'))
            {
                continue;
            }

            if (bytes.Length == 0 || bytes.Length >= MaximumCanonicalNameBytes)
                throw Unknown("The native provider returned an empty or overlong child name.");

            string name;
            try
            {
                name = StrictNameUtf8.GetString(bytes);
                if (!StrictNameUtf8.GetBytes(name).AsSpan().SequenceEqual(bytes))
                    throw new DecoderFallbackException("The native child name did not round-trip as exact UTF-8.");
                PhysicalStoreNames.ValidateSingleComponent(name);
            }
            catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
            {
                throw Unknown("The native provider returned a child name that is not one exact portable UTF-8 component.", exception);
            }

            if (!uniqueNames.Add(name))
                throw Unknown("The native provider returned duplicate child names.");

            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(names.ToArray());
    }
}
