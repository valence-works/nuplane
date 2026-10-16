using System.Reflection;
using System.Threading;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

internal enum PackageAssemblyReadCheckpointStage
{
    BeforeLoadModeSelection,
    BeforeAssemblyMetadataRead
}

internal sealed record PackageAssemblyReadCheckpointEvent(
    PackageAssemblyReadCheckpointStage Stage,
    PackageGraphUseSnapshot? GraphSnapshot,
    string? AssemblyPath);

/// <summary>
/// Provides an inert-by-default, async-flow-local checkpoint for owned-process crash tests.
/// The package read still uses the same BCL call after the checkpoint returns.
/// </summary>
internal static class PackageAssemblyReadCheckpoint
{
    private static readonly AsyncLocal<Action<PackageAssemblyReadCheckpointEvent>?> Checkpoint = new();
    private static readonly AsyncLocal<PackageGraphUseSnapshot?> CurrentGraph = new();

    internal static IDisposable InstallForTesting(Action<PackageAssemblyReadCheckpointEvent> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var prior = Checkpoint.Value;
        Checkpoint.Value = checkpoint;
        return new RestoreScope(() => Checkpoint.Value = prior);
    }

    internal static IDisposable EnterGraph(PackageGraphUseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Checkpoint.Value is null)
            return EmptyScope.Instance;

        var prior = CurrentGraph.Value;
        CurrentGraph.Value = snapshot;
        return new RestoreScope(() => CurrentGraph.Value = prior);
    }

    internal static void BeforeLoadModeSelection()
        => Checkpoint.Value?.Invoke(new(PackageAssemblyReadCheckpointStage.BeforeLoadModeSelection,
            CurrentGraph.Value, AssemblyPath: null));

    internal static AssemblyName GetAssemblyName(string assemblyPath)
    {
        Checkpoint.Value?.Invoke(new(PackageAssemblyReadCheckpointStage.BeforeAssemblyMetadataRead,
            CurrentGraph.Value, assemblyPath));
        return AssemblyName.GetAssemblyName(assemblyPath);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
