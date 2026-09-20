using Nuplane.Abstractions;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>
/// Offline, dependency-injection-free entry point for reading the currently active package set
/// from a persisted <c>store-state.json</c> file. Tools that resolve module or provider
/// assemblies without a running Nuplane host — and without any dependency-injection container or
/// network access — read through here instead of composing the reconciliation/hosted-service
/// stack.
/// </summary>
public static class NuplaneStore
{
    /// <summary>
    /// Reads every currently active package's id, version, and install path from the
    /// <c>store-state.json</c> file at <paramref name="stateFilePath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is strictly read-only. It opens <paramref name="stateFilePath"/> for reading
    /// only, with <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/> sharing,
    /// and never creates, rewrites, migrates, or otherwise modifies the file or its containing
    /// directory. A running host writing the same file is never blocked or made to fail by a
    /// concurrent call to this method.
    /// </para>
    /// <para>
    /// The packages returned are exactly the set a running host's <c>IActivePackageCatalog</c>
    /// would report: active package descriptors whose version matches the state file's recorded
    /// active version for that package id. A descriptor that diverges from the recorded active
    /// version, or that has no recorded active version at all, is omitted rather than reported.
    /// </para>
    /// <para>
    /// Behavior for edge cases, matching how <see cref="StoreStateSerializer"/> already treats
    /// them elsewhere in the store:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Missing state file: an empty collection is returned, the same "nothing persisted yet"
    /// outcome a running host observes on first start.
    /// </description></item>
    /// <item><description>
    /// State file present but empty or not valid JSON: the underlying deserialization exception
    /// propagates. A file that exists but cannot be read is a real inconsistency the caller
    /// should see, not one this reader silently swallows or heals.
    /// </description></item>
    /// <item><description>
    /// State file with no active packages recorded: an empty collection is returned.
    /// </description></item>
    /// <item><description>
    /// State file read while a host is mid-write: because the host does not write via a
    /// temp-file-and-move, a concurrent read can observe a sharing violation (an
    /// <see cref="IOException"/>) or torn, partial JSON content (a
    /// <see cref="System.Text.Json.JsonException"/>). Both outcomes are transient; a caller that
    /// needs a consistent read across a concurrent write should retry, since this method does
    /// not retry on the caller's behalf.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="stateFilePath">The path to a <c>store-state.json</c> file.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// Every active package as an <see cref="ActivePackage"/>, carrying at least its id, version,
    /// and install path, ordered deterministically by package id and then version.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stateFilePath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="IOException">Thrown when the state file exists but cannot be opened for reading, for example while a host holds an exclusive lock on it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the state file exists but its content is empty, torn, or otherwise not valid JSON for <see cref="StoreStateRecord"/>.</exception>
    public static async Task<IReadOnlyList<ActivePackage>> ReadActivePackagesAsync(
        string stateFilePath,
        CancellationToken cancellationToken = default) =>
        GetActivePackages(await ReadStateAsync(stateFilePath, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Reads the whole persisted store state at <paramref name="stateFilePath"/>, for callers that need
    /// more than the active package set — for example the graph activation records that decide which
    /// packages were activated together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the read <see cref="ReadActivePackagesAsync(string, CancellationToken)"/> itself performs,
    /// with the same strictly read-only file handling and the same missing-file, corrupt-file, and
    /// concurrent-write behavior described there — with one difference: a missing state file yields
    /// <see cref="StoreStateRecord.Empty"/>, the same "nothing persisted yet" record a running host
    /// starts from, rather than an empty package collection.
    /// </para>
    /// <para>
    /// Prefer reading once through this method and projecting with
    /// <see cref="GetActivePackages(StoreStateRecord)"/> over calling both readers, so every part of the
    /// answer comes from one snapshot of the file.
    /// </para>
    /// </remarks>
    /// <param name="stateFilePath">The path to a <c>store-state.json</c> file.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The persisted store state, or <see cref="StoreStateRecord.Empty"/> when no state file exists.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stateFilePath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="IOException">Thrown when the state file exists but cannot be opened for reading, for example while a host holds an exclusive lock on it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the state file exists but its content is empty, torn, or otherwise not valid JSON for <see cref="StoreStateRecord"/>.</exception>
    public static async Task<StoreStateRecord> ReadStateAsync(
        string stateFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return StoreStateRecord.Empty();
        }

        await using var _ = stream;

        return await StoreStateSerializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the active packages of an already-read <paramref name="state"/>: the same set
    /// <see cref="ReadActivePackagesAsync(string, CancellationToken)"/> returns, through the same single
    /// definition a running host's <c>IActivePackageCatalog</c> uses.
    /// </summary>
    /// <param name="state">The store state to project, typically from <see cref="ReadStateAsync(string, CancellationToken)"/>.</param>
    /// <returns>
    /// Every active package as an <see cref="ActivePackage"/>, ordered deterministically by package id
    /// and then version.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ActivePackage> GetActivePackages(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return ActivePackageCatalogMapper.MapActivePackages(state);
    }

    /// <summary>
    /// Reads every currently active package from the state file resolved from
    /// <paramref name="options"/> the same way a running host resolves it, via
    /// <see cref="EffectiveStorePersistenceSettings.Resolve(StoreRegistryOptions)"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="ReadActivePackagesAsync(string, CancellationToken)"/> for the missing-file,
    /// corrupt-file, concurrent-write, and no-active-packages behavior once a path is resolved.
    /// When <paramref name="options"/> resolves to <see cref="StorePersistenceMode.InMemory"/>,
    /// there is no state file to read; this method throws <see cref="InvalidOperationException"/>
    /// rather than silently returning an empty result, so a caller does not mistake "options that
    /// do not match how the host was actually configured" for "no active packages".
    /// </remarks>
    /// <param name="options">The store registry options to resolve the effective state file path from.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// Every active package as an <see cref="ActivePackage"/>, carrying at least its id, version,
    /// and install path, ordered deterministically by package id and then version.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="options"/> resolves to <see cref="StorePersistenceMode.InMemory"/>.</exception>
    /// <exception cref="IOException">Thrown when the resolved state file exists but cannot be opened for reading, for example while a host holds an exclusive lock on it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the resolved state file exists but its content is empty, torn, or otherwise not valid JSON for <see cref="StoreStateRecord"/>.</exception>
    public static Task<IReadOnlyList<ActivePackage>> ReadActivePackagesAsync(
        StoreRegistryOptions options,
        CancellationToken cancellationToken = default) =>
        ReadActivePackagesAsync(ResolveStateFilePath(options), cancellationToken);

    /// <summary>
    /// Reads the whole persisted store state from the state file resolved from
    /// <paramref name="options"/> the same way a running host resolves it, via
    /// <see cref="EffectiveStorePersistenceSettings.Resolve(StoreRegistryOptions)"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="ReadStateAsync(string, CancellationToken)"/> for the missing-file, corrupt-file,
    /// and concurrent-write behavior once a path is resolved. When <paramref name="options"/> resolves
    /// to <see cref="StorePersistenceMode.InMemory"/>, there is no state file to read; this method
    /// throws <see cref="InvalidOperationException"/> rather than silently returning an empty record,
    /// so a caller does not mistake "options that do not match how the host was actually configured"
    /// for "nothing persisted".
    /// </remarks>
    /// <param name="options">The store registry options to resolve the effective state file path from.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The persisted store state, or <see cref="StoreStateRecord.Empty"/> when no state file exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="options"/> resolves to <see cref="StorePersistenceMode.InMemory"/>.</exception>
    /// <exception cref="IOException">Thrown when the resolved state file exists but cannot be opened for reading, for example while a host holds an exclusive lock on it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the resolved state file exists but its content is empty, torn, or otherwise not valid JSON for <see cref="StoreStateRecord"/>.</exception>
    public static Task<StoreStateRecord> ReadStateAsync(
        StoreRegistryOptions options,
        CancellationToken cancellationToken = default) =>
        ReadStateAsync(ResolveStateFilePath(options), cancellationToken);

    /// <summary>
    /// The single definition of "which state file do these options mean", shared by both options
    /// overloads so they refuse in-memory persistence identically and synchronously.
    /// </summary>
    private static string ResolveStateFilePath(StoreRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return EffectiveStorePersistenceSettings.Resolve(options).ResolvedStateFilePath
            ?? throw new InvalidOperationException(
                "Cannot read Nuplane store state: the resolved persistence settings specify in-memory mode, which persists no state file.");
    }
}
