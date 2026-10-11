using System.Collections.ObjectModel;

namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Owns an all-or-nothing admission for a complete set of package install paths.</summary>
/// <remarks>Enrolled paths are accessed with BorrowFor; partial authorization is not exposed.</remarks>
public sealed class PackageStorePathAdmission : IAsyncDisposable
{
    private readonly IPackageStorePathAdmissionControl _control;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    internal PackageStorePathAdmission(
        IEnumerable<PackageStorePathAdmissionEntry> entries,
        IPackageStorePathAdmissionControl control)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _control = control ?? throw new ArgumentNullException(nameof(control));
        var copy = entries.ToArray();
        if (copy.Any(static entry => entry is null))
            throw new ArgumentException("Admission entries cannot contain null.", nameof(entries));
        Entries = new ReadOnlyCollection<PackageStorePathAdmissionEntry>(copy);
    }

    /// <summary>Gets a copied immutable classification for every requested path.</summary>
    public IReadOnlyList<PackageStorePathAdmissionEntry> Entries { get; }

    /// <summary>Creates a counted borrow for an included enrolled path.</summary>
    /// <param name="installPath">A path included in this admission.</param>
    /// <returns>A caller-owned counted borrow for the path's enrolled root.</returns>
    /// <exception cref="PackageStoreAdmissionException">The path is absent, unenrolled, or its scope is closed.</exception>
    public PackageStoreOperationBorrow BorrowFor(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        return _control.BorrowFor(this, installPath);
    }

    /// <summary>Releases all root owners in reverse acquisition order after outstanding borrows drain.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= _control.DisposeAsync(this).AsTask();
            return new ValueTask(_disposeTask);
        }
    }
}
