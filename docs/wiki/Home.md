# Home

Nuplane is a **host-neutral runtime control plane for NuGet packages**. It resolves packages, reconciles desired and actual state, maintains a deterministic local store, and emits change signals that your host can react to.

## Why start here?

This wiki is a **hybrid hub**:

- it is self-sufficient for evaluation and first-use learning;
- it treats current repository behavior as the canonical product story;
- it links to repository-owned samples, roadmap notes, and accepted specs when deeper validation or fast-moving detail would otherwise create drift.

## What Nuplane is for

Nuplane exists to let .NET applications **install, update, and load NuGet packages at runtime** — without a restart.

- **Applicability:** `Core`
- Use it when you want to drop a `.nupkg` into a watched folder (or push to a feed) and have the package resolved, loaded into the running process, and available for type discovery within seconds.
- Use it when you need deterministic package resolution, transactional updates, runtime package-state visibility, and host-controlled reactions to package changes.
- Do **not** expect it to define plugin entry points, own your dependency-injection container, or dictate activation semantics. Your host keeps those decisions.

### Practical use cases

| Scenario | What you get |
|---|---|
| **Hot-reload plugin systems** | Drop a `.nupkg` into a folder; the running app discovers the new plugin types seconds later. |
| **Modular feature delivery** | Update individual feature packages at runtime without a full redeployment. |
| **SaaS per-tenant extensions** | Load per-tenant behaviour packages dynamically, isolated per load context. |
| **Workflow and rule engines** | Deploy new steps or validators as packages and pick them up live. |
| **Internal tool hosts** | Push a new package version to a watched folder; the host auto-reconciles. |

## Audience routes

### Evaluator route

Start here if you are deciding whether Nuplane is relevant.

1. [Overview](Overview.md) — why Nuplane exists, what it does, and what it does not do
2. [Getting Started](Getting-Started.md) — the recommended first-use path
3. [Concepts and Glossary](Concepts-and-Glossary.md) — optional terminology backstop

### Integrator route

Start here if you want to move from evaluation to first use.

1. [Overview](Overview.md)
2. [Getting Started](Getting-Started.md)
3. [Usage Guide](Usage-Guide.md)
4. [Concepts and Glossary](Concepts-and-Glossary.md) for terminology lookups

### Contributor route

Start here if you need architecture and repository-to-concept mapping.

1. [Overview](Overview.md)
2. [Architecture Guide](Architecture-Guide.md)
3. [Concepts and Glossary](Concepts-and-Glossary.md)
4. [Source References](_Source-References.md) to see which repo artifact owns deeper detail

## What you will find in this wiki

- **Evaluator framing**: purpose, value proposition, capabilities, and non-goals
- **Integrator guidance**: recommended setup path, scenario selection, and sample-backed next steps
- **Contributor orientation**: module map, control loop, and terminology normalization

## What stays repository-owned

The wiki intentionally links out to repository-owned sources for:

- full validation command sets and sample walkthrough depth;
- roadmap-phase detail and accepted feature history;
- maintainer or operator runbook material;
- low-level implementation specifics that are better anchored in code or specs.

See [Source References](_Source-References.md) for the ownership matrix.

## Stability and applicability labels

This wiki uses the shared label set from [_Footer](_Footer.md):

- `Core`
- `Optional Module`
- `Phase-Based`
- `Recently Changed`
- `Evolving`

These labels clarify applicability without replacing current repository behavior as the canon.

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`docs/roadmap.md`](../roadmap.md)
- [`specs/016-nuplane-github-wiki/spec.md`](../../specs/016-nuplane-github-wiki/spec.md)

## Next step

If you are new to Nuplane, continue to [Overview](Overview.md).

