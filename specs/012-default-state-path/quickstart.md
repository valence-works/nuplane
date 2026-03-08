# Quickstart: Default State Path

**Feature**: `012-default-state-path`

## Default persisted state path

No explicit path is required. When neither `StateFilePath` nor `UseInMemoryStore` is configured, Nuplane persists reconciliation state to `.nuplane/store-state.json` under `AppContext.BaseDirectory`.

### JSON configuration

```json
{
  "Nuplane": {
    "Setup": {
      "AutomaticReconciliation": true,
      "Feeds": [
        {
          "Name": "local-packages",
          "DirectoryPath": "./packages",
          "IncludePatterns": ["*"]
        }
      ]
    }
  }
}
```

### Expected outcome

- On first successful reconciliation, `.nuplane/store-state.json` is created under the host base directory.
- On restart, active-version and last-known-good state are reloaded from that file.
- Startup logs disclose that the default persisted state path is active.

## Custom persisted path

```json
{
  "Nuplane": {
    "Setup": {
      "StateFilePath": "./data/custom-state.json"
    }
  }
}
```

### Expected outcome

- Nuplane resolves the path to a full path under the host environment.
- The configured path overrides the default `.nuplane/store-state.json` location.
- Startup logs show the configured effective path.

## Explicit in-memory mode

```json
{
  "Nuplane": {
    "Setup": {
      "UseInMemoryStore": true
    }
  }
}
```

### Expected outcome

- No state file is created.
- Restarting the host begins with empty store state.
- Startup logs clearly state that state persistence is disabled by configuration.

## Builder API examples

### Default persisted path

```csharp
services.AddNuplane(nuplane =>
{
    nuplane.AddFeed("local-packages", feed =>
    {
        feed.FromDirectory("./packages");
        feed.IncludeAll();
    });
});
```

### Custom persisted path

```csharp
services.AddNuplane(nuplane =>
{
    nuplane.WithStateFile("./data/custom-state.json");
});
```

### Explicit in-memory mode

```csharp
services.AddNuplane(nuplane =>
{
    nuplane.UseInMemoryStore();
});
```

## Invalid configurations

The following startup configuration is rejected:

```json
{
  "Nuplane": {
    "Setup": {
      "StateFilePath": "./data/custom-state.json",
      "UseInMemoryStore": true
    }
  }
}
```

### Expected outcome

- Startup fails through options validation.
- Error message states that `UseInMemoryStore` cannot be combined with `StateFilePath`.

## Validation checklist

1. Start a host with no `StateFilePath` configured and verify `.nuplane/store-state.json` is created after the first successful reconciliation.
2. Restart the host and verify previously active packages are reloaded from the default file.
3. Configure `UseInMemoryStore=true` and verify no state file is created across restart.
4. Configure a custom relative path and verify the resolved full path is logged and used.
5. Configure both `StateFilePath` and `UseInMemoryStore=true` and verify startup fails.
6. Simulate a write failure on the effective persisted path and verify the reconciliation/apply operation fails instead of silently continuing.