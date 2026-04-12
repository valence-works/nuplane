# Architecture Guide

## Primary purpose

This page explains how Nuplane works structurally: the control loop, the major modules, the ownership split between core and optional modules, and how repository areas map to user-facing concepts.

## High-level control loop

Nuplane’s architecture centers on a deterministic control loop:

1. collect desired package state from configured sources;
2. compare desired state with the current active state;
3. resolve package versions and compute a diff;
4. apply transactional per-package changes to the deterministic store;
5. expose authoritative package and operational read surfaces;
6. emit change signals for the host to react to.

- **Applicability:** `Core`

## Module map

| Repository area | Product concept | Applicability | Responsibility |
|-----------------|-----------------|---------------|----------------|
| `src/Nuplane/` | Core composition and builder surface | `Core` | Wires the runtime story together and exposes host-facing setup entry points |
| `src/Nuplane.Runtime/` | Reconciliation engine | `Core` | Owns desired-vs-actual reconciliation and runtime orchestration |
| `src/Nuplane.Store/` | Deterministic package store | `Core` | Owns transactional storage, activation pointers, and LKG safety |
| `src/Nuplane.NuGet/` | Feed resolution and acquisition | `Core` | Integrates with NuGet feeds and package acquisition |
| `src/Nuplane.Abstractions/` | Shared contracts | `Core` | Keeps common interfaces and models lightweight |
| `src/Nuplane.Sources.Directory/` | Directory-backed desired source | `Core` | Adds local `.nupkg` directory feed behavior |
| `src/Nuplane.Admin/` + `src/Nuplane.Admin.Api/` | Admin and HTTP read surfaces | `Core` | Exposes management and read routes for package and operational state |
| `src/Nuplane.Loading/` + `src/Nuplane.Loading.Api/` | Optional runtime loading | `Optional Module` | Adds load-state and assembly-loading behavior when the host opts in |
| `src/Nuplane.Loading.Hosting/` | Loading module composition helpers | `Optional Module` | Adds module-owned builder integration for loading scenarios |

## Ownership boundaries

### Core behavior

- **Applicability:** `Core`
- Runtime package reconciliation, deterministic storage, transactional safety, query-first state access, and observability belong to the main Nuplane product story.

### Optional module behavior

- **Applicability:** `Optional Module`
- Assembly loading, load-state-specific reads, and loading-specific composition stay outside the baseline runtime story until a host explicitly installs them.

### Roadmap-stage context

- **Applicability:** `Phase-Based`
- Phase 2 governance and reproducibility work, Phase 3 optional loading, and later convergence/rollout work live in the roadmap/spec system as staged context. The wiki summarizes their role but does not replace [`docs/roadmap.md`](../roadmap.md).

## Repository-to-concept mapping

| Reader concept | Repository anchor |
|----------------|-------------------|
| Host-neutral runtime control plane | `README.md`, `src/Nuplane.Runtime/` |
| Deterministic store and LKG semantics | `README.md`, `src/Nuplane.Store/` |
| Feed-backed desired state | `README.md`, `src/Nuplane.NuGet/`, `src/Nuplane.Sources.Directory/` |
| Query-first package and operational reads | `README.md`, `src/Nuplane.Admin/`, `src/Nuplane.Admin.Api/` |
| Optional load-state and assembly access | `README.md`, `src/Nuplane.Loading/`, `src/Nuplane.Loading.Api/` |
| Staged feature evolution | `docs/roadmap.md`, accepted specs under `specs/` |

## Architecture notes that matter to contributors

### Query surfaces vs observer callbacks

- **Applicability:** `Recently Changed`
- The repository currently emphasizes query-first reads for authoritative state, with observers acting as supplemental invalidation or logging signals.
- That matters when you are reading sample code or adding documentation, because the product story should not drift back toward “event history as the canonical state model.”

### Module-owned registration pattern

- **Applicability:** `Recently Changed`
- Optional modules now own their own registration helpers and builder integration packages.
- That keeps the core package from absorbing module-specific implementation details.

### Phase 4 convergence and rollout work

- **Applicability:** `Evolving`
- The roadmap documents broader convergence and administrative patterns that are real planning surfaces, but they are not the universal baseline for every current host.

## What this guide intentionally does not replace

This guide points to repository-owned sources for:

- exact admin route shape and HTTP contracts;
- full phase-history detail and acceptance matrices;
- maintainer-only operational procedures;
- low-level implementation mechanics that are better learned in code.

## Related pages

- [Overview](Overview.md)
- [Usage Guide](Usage-Guide.md)
- [Concepts and Glossary](Concepts-and-Glossary.md)
- [Source References](_Source-References.md)

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`docs/roadmap.md`](../roadmap.md)
- [`src/Nuplane/`](../../src/Nuplane/)
- [`src/Nuplane.Runtime/`](../../src/Nuplane.Runtime/)
- [`src/Nuplane.Store/`](../../src/Nuplane.Store/)
- [`src/Nuplane.Loading/`](../../src/Nuplane.Loading/)
- [`src/Nuplane.Admin.Api/`](../../src/Nuplane.Admin.Api/)

