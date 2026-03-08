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