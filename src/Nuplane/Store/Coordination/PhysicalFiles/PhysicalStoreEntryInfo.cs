using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Immutable metadata observed for one filesystem object without following its final link.</summary>
/// <remarks>
/// This record describes an observation only. It is not authority to reopen or mutate the named object;
/// operations must use validated held handles and obtain current metadata again where required.
/// </remarks>
internal sealed record PhysicalStoreEntryInfo
{
    /// <summary>Initializes immutable metadata for a physical store object.</summary>
    /// <param name="kind">The observed no-follow entry kind.</param>
    /// <param name="identity">The native identity observed for the object.</param>
    /// <param name="linkCount">The observed native link count.</param>
    /// <param name="length">The observed length in bytes where the entry kind defines one.</param>
    /// <param name="reparseTag">The native reparse tag, when available.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is invalid or length is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is null.</exception>
    internal PhysicalStoreEntryInfo(
        PhysicalStoreEntryKind kind,
        PhysicalFileIdentity identity,
        ulong linkCount,
        long length,
        uint? reparseTag = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(identity);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Kind = kind;
        Identity = identity;
        LinkCount = linkCount;
        Length = length;
        ReparseTag = reparseTag;
    }

    /// <summary>Gets the observed entry kind.</summary>
    internal PhysicalStoreEntryKind Kind { get; }

    /// <summary>Gets the native identity observed for the object.</summary>
    internal PhysicalFileIdentity Identity { get; }

    /// <summary>Gets the observed native link count.</summary>
    internal ulong LinkCount { get; }

    /// <summary>Gets the observed length in bytes.</summary>
    internal long Length { get; }

    /// <summary>Gets the native reparse tag, when available.</summary>
    internal uint? ReparseTag { get; }
}
