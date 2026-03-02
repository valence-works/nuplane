# nuplane
Nuplane is a NuGet runtime reconciliation engine for .NET applications — deterministic, transactional, and host-neutral.

## Prerequisites
- .NET 8 SDK
- Git
- macOS/Linux shell (or equivalent PowerShell commands on Windows)

## Build and test
```bash
dotnet build nuplane.sln
dotnet test nuplane.sln
```

## Runtime onboarding (Phase 1 baseline)
1. Register Nuplane services via `AddNuplaneRuntime(...)` from `Nuplane.Hosting`.
2. Configure package allowlist through `SourceTrustOptions.AllowedPackageIds`.
3. Configure reconciliation policy through `ReconciliationOptions` (single-flight, retries, backoff).
4. Register one or more `IDesiredPackageSource` implementations.
5. Trigger reconciliation manually via `ReconciliationService.TriggerManualAsync(...)`.

## Operational guidance
- Reconciliation is deterministic and idempotent for equivalent source snapshots.
- Overlapping triggers are single-flight guarded when `EnableSingleFlight=true`.
- Per-package failures do not crash the host; unaffected packages continue.
- Source read outages reuse last successful snapshots and mark the cycle degraded.
- Health recovers from degraded only after a fully successful, fresh-read cycle.

## Security and credentials
- Do not commit feed credentials or raw secrets.
- Keep secrets in runtime configuration/secret providers.
- Run secret validation before push:
```bash
./build/validate-secrets.sh
```

## Feature specs
- Phase 1 feature docs and validation steps are under `specs/001-phase1-runtime-baseline/`.
