using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Reads one configured desired-source text child through a native held-parent boundary.</summary>
/// <remarks>The operation performs its own first relative file open and final native replay; a directory candidate sample is not byte-read authority.</remarks>
internal interface IPhysicalStoreDesiredSourceTextReadFileSystem
{
    ValueTask<PhysicalStoreDesiredSourceTextReadResult> ReadDesiredSourceTextAsync(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalStoreEntryInfo expectedParent,
        PhysicalStoreNameSemantics expectedNameSemantics,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterNativeReadAsync);
}

internal enum PhysicalStoreDesiredSourceTextReadKind
{
    Missing,
    Directory,
    Unreadable,
    Readable
}

internal sealed record PhysicalStoreDesiredSourceTextReadResult(
    PhysicalStoreDesiredSourceTextReadKind Kind,
    byte[]? Content = null)
{
    internal static PhysicalStoreDesiredSourceTextReadResult Missing { get; } = new(PhysicalStoreDesiredSourceTextReadKind.Missing);
    internal static PhysicalStoreDesiredSourceTextReadResult Directory { get; } = new(PhysicalStoreDesiredSourceTextReadKind.Directory);
    internal static PhysicalStoreDesiredSourceTextReadResult Unreadable { get; } = new(PhysicalStoreDesiredSourceTextReadKind.Unreadable);

    internal static PhysicalStoreDesiredSourceTextReadResult Readable(byte[] content)
        => new(PhysicalStoreDesiredSourceTextReadKind.Readable, content ?? throw new ArgumentNullException(nameof(content)));
}
