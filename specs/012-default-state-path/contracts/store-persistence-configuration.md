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
- Programmatic builder configuration overrides both configuration layers, because builder calls run after configuration binds.
- The more specific `Nuplane:StoreRegistry` section overrides the `Nuplane:Setup` translation in both directions when the key is explicitly present; the shorthand only applies when the matching `StoreRegistry` key is absent. Superseded the original rule, under which the `Setup` translation always won.
- Because the two persistence settings are mutually exclusive, an explicit `StoreRegistry` persistence choice also suppresses the opposing `Setup` shorthand rather than combining into a configuration the validator rejects.
- The final resolved `StoreRegistryOptions` object is the single source of truth for runtime behavior.

## Trust & Security Boundary

- Store persistence operates exclusively on the local filesystem under the host application's base directory.
- No external network locations, remote storage, or cloud endpoints are supported or introduced.
- No credentials, secrets, or authentication tokens are stored in or required by the state file.
- The state file contains reconciliation metadata only (active versions, LKG versions, failure records, source snapshots).
- The persistence path is intended to resolve to a local filesystem path within the host's trust boundary; URL schemes or UNC-style remote paths are not supported by this contract and may not be validated or restricted by the runtime.

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