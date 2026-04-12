# Usage Guide

## Primary purpose

This page helps integrators choose the right Nuplane adoption path and understand where baseline runtime usage ends and optional module behavior begins.

## Start with the decision table

| Need | Applicability | Recommended path |
|------|---------------|------------------|
| Keep a runtime package set reconciled and queryable | `Core` | Start with configuration-driven setup and query-first catalog reads |
| React to package changes in host code | `Core` | Add host-owned observers or other host reactions after core setup |
| Load package assemblies into the current process | `Optional Module` | Add the loading module and its read surfaces intentionally |
| Explore roadmap-stage governance or future operational patterns | `Phase-Based` / `Evolving` | Read [`docs/roadmap.md`](../roadmap.md) and accepted specs rather than relying on the wiki alone |

## Core-runtime path

- **Applicability:** `Core`

Choose this path when your host needs Nuplane to reconcile packages, preserve deterministic store state, and expose authoritative package inventory or operational state.

Typical shape:

1. Configure feeds and reconciliation under the `Nuplane` section.
2. Let Nuplane reconcile desired vs actual package state.
3. Read authoritative package state from the active catalog or admin read surfaces.
4. Keep host decisions — cache invalidation, feature toggles, discovery, reload, and activation — in host code.

## Query-first integration guidance

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

Current repository behavior is explicit about query-first reads:

- Use active package catalog reads when you need the authoritative active package set.
- Use operational or admin state reads when you need health or cycle outcome context.
- Treat observers as invalidation and logging signals, not as the system of record.

That split keeps hosts from rebuilding state from event history.

## Configuration-driven adoption

- **Applicability:** `Core`

Choose configuration-first setup when you want the host to declare:

- feeds and include patterns;
- automatic reconciliation and poll interval;
- state-file persistence;
- optional loading settings when the loading module is installed.

The sample `appsettings.json` is the best concrete repository anchor for this path.

## Code-driven adoption

- **Applicability:** `Core`

Choose code-first setup when you want to:

- register observers or host reactions;
- compose admin/query surfaces in a particular host shape;
- use module-owned registration helpers directly.

The sample `Program.cs` is the best concrete repository anchor for this path.

## Core-runtime versus optional loading

### Core-runtime / metadata-first usage

- **Applicability:** `Core`
- Best for hosts that only need package-state awareness, deterministic storage, and package reconciliation.
- Works without installing the loading module.
- Keeps the runtime boundary small and host-neutral.

### Loading-enabled usage

- **Applicability:** `Optional Module`
- Use this when your host explicitly wants Nuplane-managed package assembly loading and load-state catalog reads.
- The loading module is opt-in, and it does not change the rule that the host owns plugin or activation semantics.
- If the loading module is absent or disabled, that is still a valid Nuplane integration.

## Sample-backed next steps

For the maintained end-to-end walkthrough, use:

- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)
- [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json)
- [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md)

Those sources own the deepest validation commands, route payload expectations, and sample behavior evidence.

## Where this page intentionally stops

This guide does **not** try to be:

- a plugin framework tutorial;
- a full operator runbook;
- the canonical route-by-route admin API reference;
- a phase-history change log.

For those topics, continue to repository-owned sources.

## Related pages

- [Getting Started](Getting-Started.md)
- [Architecture Guide](Architecture-Guide.md)
- [Concepts and Glossary](Concepts-and-Glossary.md)
- [Source References](_Source-References.md)

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)
- [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json)
- [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md)

