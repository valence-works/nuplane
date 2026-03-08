# Contract: Store Persistence Configuration

## Purpose

Defines the external configuration and builder contract for selecting store persistence behavior.

## Configuration Contract

### Setup section

```json
{
  "Nuplane": {
    "Setup": {
      "StateFilePath": "/absolute/or/relative/path/store-state.json",
      "UseInMemoryStore": false
    }
  }
}
```

### Direct store section

```json
{
  "Nuplane": {
    "StoreRegistry": {
      "StateFilePath": "/absolute/or/relative/path/store-state.json",
      "UseInMemoryStore": false
    }
  }
}
```

## Effective Behavior

| Input | Effective mode | Effective path |
|------|----------------|----------------|
| `UseInMemoryStore=true` | In-memory | `null` |
| `StateFilePath` set, `UseInMemoryStore=false` | Configured persisted path | `Path.GetFullPath(StateFilePath)` |
| Neither set | Default persisted path | `Path.Combine(AppContext.BaseDirectory, ".nuplane", "store-state.json")` |

## Validation Rules

- Blank or whitespace `StateFilePath` is invalid.
- `UseInMemoryStore=true` together with any non-empty `StateFilePath` is invalid.
- Validation failures occur during startup through the .NET options pipeline and prevent runtime startup.

## Precedence Rules

- `Nuplane:Setup` remains the high-level translation surface.
- Programmatic builder configuration and `Nuplane:Setup` translation continue to override directly bound `Nuplane:StoreRegistry` values, matching existing `StateFilePath` behavior.
- The final resolved `StoreRegistryOptions` object is the single source of truth for runtime behavior.

## Trust & Security Boundary

- Store persistence operates exclusively on the local filesystem under the host application's base directory.
- No external network locations, remote storage, or cloud endpoints are supported or introduced.
- No credentials, secrets, or authentication tokens are stored in or required by the state file.
- The state file contains reconciliation metadata only (active versions, LKG versions, failure records, source snapshots).
- The persistence path MUST NOT be configurable to point outside the host's trust boundary (e.g., no URL schemes, no UNC paths in cross-platform contexts).

## Builder Contract

### Persisted path

```csharp
services.AddNuplane(nuplane =>
{
    nuplane.WithStateFile("./custom-store-state.json");
});
```

### Explicit in-memory mode

```csharp
services.AddNuplane(nuplane =>
{
    nuplane.UseInMemoryStore();
});
```

## Logging Contract

When persistence is enabled, startup logs include:

- `Mode`: `DefaultPath` or `ConfiguredPath`
- `StateFilePath`: the fully resolved effective path

When in-memory mode is enabled, startup logs include:

- `Mode`: `InMemory`
- An explicit message that persisted store state is disabled by configuration

## Failure Contract

- If persistence is enabled and a write to the effective state path fails, the reconciliation/apply operation fails.
- The system does not silently downgrade to in-memory persistence.