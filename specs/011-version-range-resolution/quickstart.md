# Quickstart: Version Range Resolution

**Feature**: 011-version-range-resolution

## Configuration Examples

### Resolve to latest version (default)

```json
{
  "Nuplane": {
    "Setup": {
      "Feeds": [
        {
          "Name": "nuget.org",
          "ServiceIndex": "https://api.nuget.org/v3/index.json",
          "IncludePatterns": [
            "Elsa.Workflows.Core"
          ]
        }
      ]
    }
  }
}
```

When no version is specified, Nuplane queries the feed for all available versions of `Elsa.Workflows.Core` and resolves to the highest stable release.

### Pin to an exact version

```json
"IncludePatterns": [
  "Elsa.Workflows.Core [3.2.1]"
]
```

Resolves to exactly version `3.2.1`. Reconciliation will not update to newer versions.

### Constrain to a version range

```json
"IncludePatterns": [
  "Elsa.Workflows.Core [3.0.0, 4.0.0)"
]
```

Resolves to the highest stable version within `>= 3.0.0` and `< 4.0.0`. When a new `3.x` version is published to the feed, the next reconciliation cycle picks it up automatically. A `4.0.0` release is ignored.

### Bare version shorthand

```json
"IncludePatterns": [
  "Elsa.Workflows.Core 3.2.1"
]
```

Equivalent to `[3.2.1]` — resolves to exactly `3.2.1`.

### Wildcard pattern with version range

```json
"IncludePatterns": [
  "Elsa.* [3.0.0, 4.0.0)"
]
```

All packages matching `Elsa.*` from the feed catalog are pinned to the `[3.0.0, 4.0.0)` range.

### Include pre-release versions

```json
"IncludePatterns": [
  "Elsa.Workflows.Core [3.0.0-preview.1, 4.0.0)"
]
```

When either bound references a pre-release tag, pre-release versions are included in resolution.

### Configure version cache TTL

The version enumeration cache TTL is configured under feed resolution options. Default is 5 minutes.

```json
{
  "Nuplane": {
    "FeedResolution": {
      "VersionCacheTtl": "00:10:00"
    }
  }
}
```

Set to `"00:00:00"` to disable caching (enumerate fresh on every reconciliation cycle).

## Verification

After reconciliation runs, check the structured logs for version resolution entries:

```
[INF] Version resolved: PackageId=Elsa.Workflows.Core, Range=[3.0.0, 4.0.0), Selected=3.3.0, Feed=nuget.org, Candidates=47, CacheHit=true, Duration=0ms
```

Failed resolutions appear as warnings:

```
[WRN] Version resolution failed: PackageId=Elsa.Workflows.Core, Range=[5.0.0, 6.0.0), Reason=No versions satisfy range, Feed=nuget.org, Candidates=47
```

Invalid version range syntax at startup produces a startup failure:

```
[FTL] Options validation failed: IncludePatterns entry 'Elsa.Workflows.Core [abc, def)' contains invalid version range syntax.
```
