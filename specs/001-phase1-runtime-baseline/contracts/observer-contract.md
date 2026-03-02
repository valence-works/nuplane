# Contract: Runtime Observer Events

## Interface
```csharp
public interface INuplaneObserver
{
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct);
    Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct);
    Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct);
}
```

## Behavioral Contract
- `OnPackagesChangingAsync` MUST fire before apply begins for a non-empty change set.
- `OnPackagesChangedAsync` MUST fire after all package transactions complete for the cycle.
- `OnPackageFailedAsync` MUST fire per failed package transaction.
- All callbacks for one cycle MUST carry the same `correlationId` in `PackageChangeSet`/diagnostic context.
- Observer callback failures MUST be isolated and logged; they MUST NOT abort reconciliation.

## Delivery Semantics
- Ordering per cycle: `Changing` -> zero or more `Failed` -> `Changed`.
- Exactly-once delivery is not guaranteed across process crashes; at-least-once per in-process cycle is acceptable for Phase 1.

## Test Contract
- Must verify callback ordering and shared correlation ID.
- Must verify observer exceptions do not terminate reconciliation loop.
