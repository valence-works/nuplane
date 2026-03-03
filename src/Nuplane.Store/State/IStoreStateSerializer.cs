namespace Nuplane.Store.State;

/// <summary>
/// Defines the contract for serializing and deserializing store state to/from a file.
/// </summary>
public interface IStoreStateSerializer
{
    /// <summary>Loads the store state from the specified file path.</summary>
    Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken);
    /// <summary>Saves the store state to the specified file path.</summary>
    Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken);
}
