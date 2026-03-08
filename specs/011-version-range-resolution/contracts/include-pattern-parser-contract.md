# Contract: Include Pattern Parser

## Interface
```csharp
internal static class IncludePatternParser
{
    static ParsedIncludePattern Parse(string pattern);
    static bool TryParseVersionRange(string candidate, out string versionRange);
}
```

## Behavioral Contract
- The parser MUST split an `IncludePatterns` entry string into a package identity glob and an optional version range suffix.
- Splitting MUST be whitespace-delimited: the version range is the trailing segment that starts with `[`, `(`, or a digit.
- If no version range suffix is detected, `VersionRange` MUST be empty string (signaling "resolve to latest").
- The `PackageGlob` MUST preserve the original case and wildcard characters.
- Parsing MUST be deterministic and stateless.

## Parsing Rules
| Input | PackageGlob | VersionRange |
|-------|-------------|-------------|
| `"MyPackage"` | `MyPackage` | *(empty)* |
| `"MyPackage [1.0.0, 2.0.0)"` | `MyPackage` | `[1.0.0, 2.0.0)` |
| `"MyPackage [2.0.0]"` | `MyPackage` | `[2.0.0]` |
| `"MyPackage 1.0.0"` | `MyPackage` | `1.0.0` |
| `"MyPackage.* [1.0.0,)"` | `MyPackage.*` | `[1.0.0,)` |
| `"*"` | `*` | *(empty)* |
| `"* [1.0.0, 2.0.0)"` | `*` | `[1.0.0, 2.0.0)` |
| `"  MyPackage  "` | `MyPackage` | *(empty)* |

## Error Contract
- Empty or whitespace-only pattern: MUST return a result with empty `PackageGlob` (caught by downstream validation).
- The parser itself MUST NOT throw exceptions — validation of the version range syntax is a separate concern (`IValidateOptions<T>` validator).

## Test Contract
- Must verify all parsing rules in the table above.
- Must verify leading/trailing whitespace is trimmed.
- Must verify exact patterns (no wildcards) are correctly split.
- Must verify wildcard patterns with version ranges are correctly split.
- Must verify patterns without version ranges produce empty `VersionRange`.
- Must verify bare version numbers (digits after whitespace) are recognized as version ranges.
