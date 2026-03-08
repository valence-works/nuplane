# Data Model: Default State Path

## `NuplaneSetupOptions`

- **Purpose**: Declarative translation model for the `Nuplane:Setup` section.
- **Fields**:
  - `AutomaticReconciliation: bool`
  - `PollInterval: TimeSpan`
  - `StateFilePath: string?`
  - `UseInMemoryStore: bool`
  - `Feeds: List<NuplaneFeedSetupOptions>`
- **Validation rules**:
  - `StateFilePath`, when provided, cannot be blank/whitespace.
  - `UseInMemoryStore=true` cannot be combined with a non-empty `StateFilePath`.
  - Existing feed and poll-interval rules remain unchanged.
- **Relationships**:
  - Translated by `NuplaneServiceCollectionExtensions.ApplySetupConfiguration` into `StoreRegistryOptions` and builder calls.

## `StoreRegistryOptions`

- **Purpose**: Runtime options consumed by store services.
- **Fields**:
  - `StateFilePath: string?`
  - `UseInMemoryStore: bool`
- **Validation rules**:
  - `StateFilePath`, when provided, cannot be blank/whitespace.
  - `UseInMemoryStore=true` cannot be combined with `StateFilePath`.
- **Relationships**:
  - Input to effective-settings resolution in `Nuplane.Store.State`.

## `EffectiveStorePersistenceSettings`

- **Purpose**: Internal resolved model used by runtime services after validation.
- **Fields**:
  - `Mode: StorePersistenceMode` with values:
    - `DefaultPath`
    - `ConfiguredPath`
    - `InMemory`
  - `ResolvedStateFilePath: string?`
  - `ConfiguredStateFilePath: string?`
  - `UseInMemoryStore: bool`
- **Derivation rules**:
  - If `UseInMemoryStore=true`, `Mode=InMemory` and `ResolvedStateFilePath=null`.
  - Else if `StateFilePath` is non-empty, `Mode=ConfiguredPath` and `ResolvedStateFilePath=Path.GetFullPath(StateFilePath)`.
  - Else `Mode=DefaultPath` and `ResolvedStateFilePath=Path.Combine(AppContext.BaseDirectory, ".nuplane", "store-state.json")`.
- **Consumers**:
  - `StoreRegistry`
  - startup logging path/mode emission
  - tests asserting effective behavior

## `StoreStateRecord`

- **Purpose**: Persisted reconciliation snapshot already used by the store.
- **Fields**:
  - `ActiveVersionById`
  - `LastKnownGoodById`
  - `LastFailureById`
  - `LastSuccessfulSourceSnapshots`
  - `UpdatedAt`
- **State transitions affected by this feature**:
  - No schema change.
  - Persistence now occurs by default through the resolved default path instead of requiring explicit `StateFilePath` configuration.

## Builder Surface

- **Purpose**: Programmatic configuration path for hosts not using JSON configuration.
- **Members affected**:
  - Existing `WithStateFile(string path)` remains.
  - New explicit `UseInMemoryStore()` (or equivalent boolean setter) sets `StoreRegistryOptions.UseInMemoryStore=true` and clears/overrides path usage at the configuration layer.
- **Conflict rule**:
  - Hosts must not configure both persisted path and explicit in-memory mode through the same final resolved options set.