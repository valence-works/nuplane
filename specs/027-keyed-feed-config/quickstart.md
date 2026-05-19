# Quickstart: Key-Based Feed Setup Configuration

**Feature**: 027-keyed-feed-config
**Date**: 2026-05-18

## Recommended Configuration Shape

Use keyed feed setup under `Nuplane:Setup:Feeds`. The feed key is the canonical feed name.

```json
{
  "Nuplane": {
    "Setup": {
      "AutomaticReconciliation": true,
      "PollInterval": "00:01:00",
      "Feeds": {
        "local-packages": {
          "DirectoryPath": "packages",
          "IncludePatterns": ["*"],
          "Directory": {
            "Watch": true,
            "DebounceWindow": "00:00:01"
          }
        },
        "nuget.org": {
          "ServiceIndex": "https://api.nuget.org/v3/index.json",
          "IncludePatterns": ["MyCompany.Plugins.*"]
        }
      }
    }
  }
}
```

## Migration Example

Before:

```json
{
  "Feeds": [
    {
      "Name": "feedz.io",
      "ServiceIndex": "https://old.example/nuget/index.json"
    }
  ]
}
```

After:

```json
{
  "Feeds": {
    "feedz.io": {
      "ServiceIndex": "https://new.example/nuget/index.json"
    }
  }
}
```

## Layered Override Scenario

Base configuration:

```json
{
  "Nuplane": {
    "Setup": {
      "Feeds": {
        "feedz.io": {
          "ServiceIndex": "https://old.example/nuget/index.json",
          "IncludePatterns": ["Elsa.*"]
        }
      }
    }
  }
}
```

Later provider:

```json
{
  "Nuplane": {
    "Setup": {
      "Feeds": {
        "feedz.io": {
          "ServiceIndex": "https://new.example/nuget/index.json",
          "IncludePatterns": ["Elsa.Persistence.*"]
        }
      }
    }
  }
}
```

Expected result:

- One feed named `feedz.io` is registered.
- The effective `ServiceIndex` is `https://new.example/nuget/index.json`.
- Include pattern behavior follows normal .NET configuration binding for the same keyed path.
- No duplicate `feedz.io` feed is registered.

## Mixed Legacy And Keyed Configuration

If effective configuration contains both:

```json
{
  "Feeds": [
    {
      "Name": "feedz.io",
      "ServiceIndex": "https://old.example/nuget/index.json"
    }
  ]
}
```

and:

```json
{
  "Feeds": {
    "feedz.io": {
      "ServiceIndex": "https://new.example/nuget/index.json"
    }
  }
}
```

Expected result:

- The keyed declaration wins.
- The array declaration is ignored for `feedz.io`.
- Nuplane emits a warning diagnostic identifying the ignored array declaration.

## Validation Examples

Invalid key/name mismatch:

```json
{
  "Feeds": {
    "feedz.io": {
      "Name": "other-feed",
      "ServiceIndex": "https://example.test/v3/index.json"
    }
  }
}
```

Expected result: startup validation fails with a message identifying both `feedz.io` and `other-feed`.

Invalid source type conflict:

```json
{
  "Feeds": {
    "local-packages": {
      "ServiceIndex": "https://example.test/v3/index.json",
      "DirectoryPath": "packages"
    }
  }
}
```

Expected result: startup validation fails because a feed must specify exactly one source type.

## Verification Commands

Run focused tests for configuration-driven registration and directory setup:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~Configuration"
dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj
```

Run the full solution when practical:

```bash
dotnet test nuplane.sln
```
