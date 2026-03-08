# Contract: Version Range Evaluator

## Interface

```csharp
internal interface IVersionRangeEvaluator
{
    VersionResolutionResult SelectBestMatch(
        string versionRange,
        IReadOnlyList<string> availableVersions);

    bool IsValidRange(string versionRange);
}
```

**Location**: Interface in `Nuplane.Runtime/Feeds/Versioning/`  
**Implementation**: `NuGetVersionRangeEvaluator` in `Nuplane.NuGet/`

## Implementation Notes

`NuGetVersionRangeEvaluator` uses:
- `NuGet.Versioning.VersionRange.TryParse()` / `VersionRange.Parse()` for range parsing
- `NuGet.Versioning.NuGetVersion.TryParse()` for version string validation
- `VersionRange.FindBestMatch(IEnumerable<NuGetVersion>)` for best-match selection

## Behavioral Contract

- Given a version range string and a list of available version strings, the evaluator MUST return the best matching version.
- For empty range strings (latest): the evaluator MUST return the highest stable version. Pre-release versions MUST be excluded.
- For exact ranges (`[x.y.z]` or bare `x.y.z`): the evaluator MUST return the exact version if present, or a failure result.
- For bounded ranges: the evaluator MUST use `VersionRange.FindBestMatch()` to select the best match per NuGet semantics.
- Pre-release versions MUST be excluded unless the range explicitly references a pre-release tag (per NuGet.Versioning behavior).
- Selection MUST be deterministic: the same inputs MUST always produce the same output.
- `VersionResolutionResult.CandidateCount` MUST reflect the total number of version strings received (before filtering).
- Version strings in `availableVersions` that cannot be parsed by `NuGetVersion.TryParse()` MUST be silently skipped (not cause failure).

## Validation Contract

- `IsValidRange(string)` MUST return `true` for all NuGet version range notations accepted by `VersionRange.TryParse()`.
- `IsValidRange(string)` MUST return `true` for empty/null strings (meaning "latest").
- `IsValidRange(string)` MUST return `false` for syntactically invalid range strings.
- Used by the `IValidateOptions<T>` validator at startup to reject invalid `IncludePatterns` entries.

## Error Contract

- No matching version: return `VersionResolutionResult` with `Success = false` and `FailureReason` describing the gap (e.g., "no versions satisfy range [5.0.0, 6.0.0)", "no stable versions available").
- Empty version list: return `VersionResolutionResult` with `Success = false` and `FailureReason = "no versions available"`.
- The evaluator MUST NOT throw exceptions — all outcomes are expressed in the result type.

## Test Contract

- Must verify exact version match when present.
- Must verify exact version failure when absent.
- Must verify bounded range selects best match per NuGet semantics.
- Must verify open upper bound selects highest available.
- Must verify exclusive bounds exclude boundary versions.
- Must verify inclusive bounds include boundary versions.
- Must verify latest (empty range) selects highest stable.
- Must verify pre-release exclusion by default.
- Must verify pre-release inclusion when range references pre-release.
- Must verify empty version list returns failure.
- Must verify unparseable version strings are skipped.
- Must verify deterministic output for same inputs.
- Must verify `IsValidRange` accepts valid NuGet range notations.
- Must verify `IsValidRange` rejects malformed strings.
