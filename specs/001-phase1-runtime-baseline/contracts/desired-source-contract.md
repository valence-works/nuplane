# Contract: Desired Package Source

## Interface
```csharp
public interface IDesiredPackageSource
{
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct);
}
```

## Behavioral Contract
- Source output MUST be deterministic for the same underlying source snapshot.
- Source output MUST include source identity for each package request.
- Non-allowlisted package IDs MUST be rejected before entering resolution.
- If a source read fails during a cycle:
  - runtime MUST reuse the last successful snapshot for that source (if available),
  - runtime MUST continue cycle processing,
  - cycle health MUST be degraded.
- Fresh source read success updates that source snapshot reference for future fallback.

## Error Contract
- Source access failures return typed exceptions or failure results mapped to stage `source-read` diagnostics.
- Source implementations MUST NOT terminate host process execution on failure.

## Test Contract
- Must verify deterministic output ordering.
- Must verify strict allowlist rejection.
- Must verify snapshot fallback behavior when source is unavailable.
