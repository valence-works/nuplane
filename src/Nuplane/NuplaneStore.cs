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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

        if (!File.Exists(stateFilePath))
        {
            return [];
        }

        await using var stream = new FileStream(
            stateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var state = await StoreStateSerializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var effectiveSettings = EffectiveStorePersistenceSettings.Resolve(options);
        if (effectiveSettings.ResolvedStateFilePath is null)
        {
            throw new InvalidOperationException(
                "Cannot read active packages: the resolved persistence settings specify in-memory mode, which persists no state file.");
        }

        return ReadActivePackagesAsync(effectiveSettings.ResolvedStateFilePath, cancellationToken);
    }
}
