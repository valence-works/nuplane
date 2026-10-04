using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuplane.Store.State;

/// <summary>
/// Serializes and deserializes <see cref="StoreStateRecord"/> to/from JSON files.
/// </summary>
public sealed class StoreStateSerializer : IStoreStateSerializer
{
    private readonly IAtomicFileReplacer _fileReplacer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Initializes a serializer that writes state through atomic file replacement.</summary>
    public StoreStateSerializer()
        : this(new AtomicFileReplacer())
    {
    }

    internal StoreStateSerializer(IAtomicFileReplacer fileReplacer)
    {
        _fileReplacer = fileReplacer ?? throw new ArgumentNullException(nameof(fileReplacer));
    }

    /// <inheritdoc />
    public async Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(stateFilePath))
        {
            return StoreStateRecord.Empty();
        }

        await using var stream = new FileStream(
            stateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes and normalizes a <see cref="StoreStateRecord"/> from an already-open
    /// <paramref name="stream"/>. Shared by <see cref="LoadAsync"/> and by callers, such as
    /// <see cref="NuplaneStore"/>, that need to open the state file with sharing semantics of
    /// their own instead of going through <see cref="LoadAsync"/>.
    /// </summary>
    internal static async Task<StoreStateRecord> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var state = await JsonSerializer.DeserializeAsync<StoreStateRecord>(stream, JsonOptions, cancellationToken);
        return Normalize(state ?? StoreStateRecord.Empty());
    }

    /// <inheritdoc />
    public async Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var destinationPath = Path.GetFullPath(stateFilePath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(state), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await _fileReplacer.ReplaceAsync(temporaryPath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // Cleanup must not hide the original serialization or replacement failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not hide the original serialization or replacement failure.
        }
    }

    private static StoreStateRecord Normalize(StoreStateRecord state) =>
        state with
        {
            ActivePackageDescriptorsById = new(state.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(state.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase)
        };

}
