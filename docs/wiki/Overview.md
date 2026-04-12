# Overview

## Why Nuplane exists

Nuplane exists to give .NET hosts a predictable way to **resolve, synchronize, and manage NuGet packages at runtime** without forcing them into a plugin framework or a framework-specific runtime model.

Teams usually feel this need when they want to:

- keep a runtime package set aligned with desired state from feeds or local package drops;
- make package updates safe, observable, and retryable;
- let the host stay in charge of what happens when packages change.

- **Applicability:** `Core`

## Who should keep reading?

Nuplane is a good fit when you are:

- evaluating runtime package reconciliation for a web app, worker, modular host, or other .NET application;
- integrating package-state awareness into a host that needs deterministic storage and explicit change signals;
- contributing to Nuplane and need the architecture vocabulary before reading the codebase.

## What Nuplane does

Nuplane’s current repository behavior centers on a straightforward control loop:

1. determine desired packages;
2. compare them with current state;
3. compute the diff;
4. apply transactional per-package updates;
5. emit change events and expose query surfaces.

### Current major capabilities

| Capability | Applicability | What it means now |
|-----------|---------------|-------------------|
| Package resolution from NuGet v3 feeds and local directory-backed feeds | `Core` | Nuplane resolves packages from remote feeds and `.nupkg` drop folders |
| Deterministic local package store | `Core` | Downloaded and extracted packages live in a predictable on-disk structure |
| Transactional updates with LKG protection | `Core` | Failed updates preserve the last-known-good active version |
| Reconciliation and query-first state access | `Core` | Nuplane reconciles desired vs actual state, then exposes authoritative package and operational surfaces |
| Observability and operational visibility | `Core` | Logs, metrics, health, and persisted state are part of the current product story |
| Assembly loading and load-state surfaces | `Optional Module` | Available through the loading module when the host explicitly opts in |
| Phase-specific governance or rollout features | `Phase-Based` | Roadmap work exists, but not every phase feature is part of every host’s baseline story |

## What Nuplane does not do

Nuplane does **not**:

- define a plugin entrypoint or plugin programming model;
- mutate your DI container to activate package content for you;
- impose host-specific activation semantics;
- guarantee in-process assembly unload;
- sandbox untrusted code.

Nuplane is **infrastructure for package reconciliation**. Your host owns the meaning of those packages.

## Why Nuplane is not a plugin framework

Nuplane can help a host acquire packages, expose authoritative package state, and optionally load assemblies through an opt-in loading module. That still does **not** make Nuplane the owner of plugin semantics.

- **Applicability:** `Core`
- A host still decides what counts as a plugin, how types are discovered, and when code is activated or deactivated.
- The sample host demonstrates this boundary by using query surfaces and host-owned discovery logic instead of asking Nuplane to define a plugin model.

## Scenario summary

| Scenario | Best starting point | Why |
|----------|---------------------|-----|
| Quick product evaluation | [Getting Started](Getting-Started.md) | Fastest route to the first-use mental model |
| Metadata-only or runtime-state integration | [Usage Guide](Usage-Guide.md) | Focuses on core-runtime and query-first usage |
| Loading-enabled host integration | [Usage Guide](Usage-Guide.md) | Separates optional loading from baseline runtime usage |
| Module / contributor orientation | [Architecture Guide](Architecture-Guide.md) | Maps concepts to repository structure and roadmap context |

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`docs/roadmap.md`](../roadmap.md)
- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)

## Next steps

- Continue to [Getting Started](Getting-Started.md) for the recommended first path.
- Continue to [Architecture Guide](Architecture-Guide.md) if you are approaching Nuplane as a contributor or architect.
- Use [Concepts and Glossary](Concepts-and-Glossary.md) when you want the normalized vocabulary.

