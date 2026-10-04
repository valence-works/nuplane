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

        var operationId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(destinationPath);
        var temporaryPath = Path.Combine(directory, $"{fileName}.{operationId}.tmp");
        var backupPath = Path.Combine(directory, $"{fileName}.{operationId}.bak");
        var replacementAttempted = false;

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

            replacementAttempted = true;
            await _fileReplacer.ReplaceAsync(temporaryPath, destinationPath, backupPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryRestoreBackup(destinationPath, backupPath);
            throw;
        }
        finally
        {
            // ReplaceFileW can fail after removing the destination. If restoring the backup also
            // fails, retain both recovery artifacts instead of deleting the only valid records.
            if (!replacementAttempted || File.Exists(destinationPath))
            {
                TryDeleteRecoveryFile(temporaryPath);
                TryDeleteRecoveryFile(backupPath);
            }
        }
    }

    private static void TryRestoreBackup(string destinationPath, string backupPath)
    {
        if (File.Exists(destinationPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Move(backupPath, destinationPath);
        }
        catch (IOException)
        {
            // Preserve the backup under its unique recovery name when restoration is blocked.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the backup under its unique recovery name when restoration is blocked.
        }
    }

    private static void TryDeleteRecoveryFile(string path)
    {
        try
        {
            File.Delete(path);
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
