# Package-store maintenance observability

Calling the optional `AddNuplanePackageStoreMaintenance` registration adds diagnostics for explicit inspection, pruning, enrollment, recovery, and migration calls. Registration does not schedule maintenance or perform pruning in the background.

The `Nuplane.Admin` meter exposes two counters:

| Instrument | Tags | Meaning |
| --- | --- | --- |
| `nuplane.package_store.maintenance.operations` | `operation`, `outcome` | Calls recorded as preview, completed, refused, failed, or cancelled. |
| `nuplane.package_store.maintenance.physical` | `outcome` | Observed physical outcomes such as deleted, retained, quarantined, already absent, and partially removed. |

Tags use bounded operation and outcome values. Correlation IDs, root labels, diagnostic messages, member identities, install identities, and paths are excluded from metric dimensions. Correlation IDs, root labels, outcome counts, protection reasons, and diagnostic details appear in structured logs. The physical `deleted` count comes only from a returned candidate whose observed `PhysicalOutcome` is `Deleted`; an `Eligible` plan classification or `AlreadyAbsent` observation never increments it.

Maintenance logs include the operation correlation ID, status, enrollment epoch when observed, protection knowledge, inventory completeness, retained and eligible candidate counts, graph-use count, physical deletion and partial-removal counts, protection reasons, and diagnostic codes/messages. A caller-supplied prune correlation ID is carried through; other operations use the current activity ID when present or a generated diagnostic ID.

The optional package-store maintenance contributor reports the last mutating operation's refusal, failure, or cancellation as a degraded operational-state reason. A successful complete mutation clears that reason. Read-only previews do not clear or set it. This state is held in memory for the current process only; it is neither persisted nor package-store authority, and absence of a reason does not assert that a fresh process has inspected the store.
