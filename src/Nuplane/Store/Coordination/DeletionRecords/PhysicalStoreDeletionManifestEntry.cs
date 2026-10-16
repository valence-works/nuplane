using System.Collections.Immutable;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination.DeletionRecords;

/// <summary>Describes one exact native entry beneath a candidate install directory.</summary>
/// <remarks>This value is detached evidence and never authorizes filesystem access or removal.</remarks>
internal sealed class PhysicalStoreDeletionManifestEntry
{
    private const int MaximumComponentBytes = 255;
    private const int MaximumComponentCodeUnits = 255;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    internal PhysicalStoreDeletionManifestEntry(
        IEnumerable<string> relativeComponents,
        PhysicalStoreEntryKind kind,
        PhysicalFileIdentity identity,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics parentSemantics,
        PhysicalStoreNameSemantics? directorySemantics)
    {
        ArgumentNullException.ThrowIfNull(relativeComponents);
        if (!Enum.IsDefined(kind) || kind is not (PhysicalStoreEntryKind.Directory or PhysicalStoreEntryKind.RegularFile))
            throw new ArgumentOutOfRangeException(nameof(kind), "A deletion manifest supports only directories and regular files.");
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(parentIdentity);
        ArgumentNullException.ThrowIfNull(parentSemantics);
        if (kind == PhysicalStoreEntryKind.Directory && directorySemantics is null)
            throw new ArgumentNullException(nameof(directorySemantics), "A directory row requires its own native name profile.");
        if (kind == PhysicalStoreEntryKind.RegularFile && directorySemantics is not null)
            throw new ArgumentException("A regular-file row cannot carry directory name semantics.", nameof(directorySemantics));

        var components = CopyComponents(relativeComponents);
        if (components.IsDefaultOrEmpty)
            throw new ArgumentException("A manifest entry requires at least one relative component.", nameof(relativeComponents));
        if (components.Length > PhysicalStoreDeletionManifest.MaximumDepth)
            throw new ArgumentOutOfRangeException(nameof(relativeComponents), "A manifest entry exceeds the supported relative depth.");

        foreach (var component in components)
        {
            PhysicalStoreNames.ValidateSingleComponent(component);
            if (component.Length > MaximumComponentCodeUnits)
                throw new ArgumentException("A manifest component exceeds the UTF-16 code-unit limit.", nameof(relativeComponents));
        }
        ValidateComponent(components[^1], parentSemantics);

        RelativeComponents = components;
        Kind = kind;
        Identity = CopyIdentity(identity);
        ParentIdentity = CopyIdentity(parentIdentity);
        ParentSemantics = CopySemantics(parentSemantics);
        DirectorySemantics = directorySemantics is null ? null : CopySemantics(directorySemantics);
    }

    internal ImmutableArray<string> RelativeComponents { get; }
    internal PhysicalStoreEntryKind Kind { get; }
    internal PhysicalFileIdentity Identity { get; }
    internal PhysicalFileIdentity ParentIdentity { get; }
    internal PhysicalStoreNameSemantics ParentSemantics { get; }
    internal PhysicalStoreNameSemantics? DirectorySemantics { get; }

    /// <summary>Validates one exact component under the supplied native name encoding.</summary>
    internal static void ValidateComponent(string component, PhysicalStoreNameSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        PhysicalStoreNames.ValidateSingleComponent(component);
        if (component.Length > MaximumComponentCodeUnits)
            throw new ArgumentException("A manifest component exceeds the UTF-16 code-unit limit.", nameof(component));

        int encodedBytes;
        try
        {
            encodedBytes = semantics.Encoding switch
            {
                PhysicalStoreNameEncoding.Utf8 => StrictUtf8.GetByteCount(component),
                PhysicalStoreNameEncoding.Utf16LittleEndian => StrictUtf16.GetByteCount(component),
                _ => throw new ArgumentOutOfRangeException(nameof(semantics), "The native name encoding is unsupported.")
            };
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A manifest component is invalid under its native name encoding.", nameof(component), exception);
        }

        if (encodedBytes > MaximumComponentBytes)
            throw new ArgumentException("A manifest component exceeds the native encoded-byte limit.", nameof(component));
    }

    private static ImmutableArray<string> CopyComponents(IEnumerable<string> components)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var component in components)
        {
            if (builder.Count == PhysicalStoreDeletionManifest.MaximumDepth)
                throw new ArgumentOutOfRangeException(nameof(components), "A manifest entry exceeds the supported relative depth.");
            if (component is null)
                throw new ArgumentException("A manifest component cannot be null.", nameof(components));
            builder.Add(component);
        }

        return builder.ToImmutable();
    }

    private static PhysicalFileIdentity CopyIdentity(PhysicalFileIdentity identity)
        => new(identity.Provider, identity.VolumeOrDeviceId, identity.FileId);

    private static PhysicalStoreNameSemantics CopySemantics(PhysicalStoreNameSemantics semantics)
        => new(semantics.ProfileId, semantics.Encoding, semantics.CaseSensitive, semantics.NormalizationInsensitive);
}
