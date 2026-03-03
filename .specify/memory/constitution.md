<!--
Sync Impact Report (v1.1.0)
- Version change: 1.0.0 → 1.1.0
- Modified principles: none renamed
- Added sections:
	- VI. Specification & Task Decomposition Discipline
	  (under Delivery Workflow & Quality Gates)
- Removed sections: none
- Templates requiring updates:
	- ✅ updated: .specify/templates/plan-template.md
	  (Constitution Check: added decomposition discipline bullet)
	- ✅ updated: .specify/templates/spec-template.md
	  (FR guidance: added prescriptive-requirement note)
	- ✅ updated: .specify/templates/tasks-template.md
	  (Notes: added one-artifact-per-task and config-consumer rules)
	- ⚠ pending (not present in repo): .specify/templates/commands/*.md
- Follow-up TODOs: none
- Bump rationale: MINOR — adds a new normative principle section
  without removing or redefining existing principles.

Prior Sync Impact Report (v1.0.0)
- Version change: template-placeholder → 1.0.0
- Modified principles:
	- placeholder principle 1 → I. Deterministic Reconciliation
	- placeholder principle 2 → II. Transactional Store Safety
	- placeholder principle 3 → III. Source & Supply Chain Integrity
	- placeholder principle 4 → IV. Observability & Operability
	- placeholder principle 5 → V. Test & Contract Discipline
- Added sections:
	- Technical Boundaries
	- Delivery Workflow & Quality Gates
- Removed sections: none
- Templates requiring updates:
	- ✅ updated: .specify/templates/plan-template.md
	- ✅ updated: .specify/templates/spec-template.md
	- ✅ updated: .specify/templates/tasks-template.md
	- ⚠ pending (not present in repo): .specify/templates/commands/*.md
-->

# Nuplane Constitution

## Core Principles

### I. Deterministic Reconciliation
Nuplane MUST reconcile desired and actual package state deterministically. Given the same inputs,
the reconciler MUST produce the same active package set and state transitions. Reconciliation MUST
be idempotent, retries MUST be bounded, and failure handling MUST preserve forward progress without
state corruption. Rationale: deterministic behavior is required for safe automated runtime updates.

### II. Transactional Store Safety
Package activation MUST follow transactional semantics: stage, validate, publish immutable version,
atomically switch active pointer, then persist state metadata. On failure, Nuplane MUST keep
last-known-good active and MUST record diagnostic failure details. Partial updates that can leave an
unknown active state are prohibited. Rationale: hosts must remain stable during failed updates.

### III. Source & Supply Chain Integrity
Only explicitly configured and trusted desired-state sources (NuGet feeds and directory sources)
MUST influence reconciliation. Package identity and version MUST be validated before activation;
integrity checks (hash/signature where available) MUST run in the validation stage. Credentials and
secrets MUST NOT be committed to source control. Rationale: runtime package control is a
supply-chain boundary and must default to least trust.

### IV. Observability & Operability
Every reconciliation cycle MUST emit structured logs with a correlation identifier. Nuplane MUST
publish baseline metrics for active packages, reconciliation outcomes (add/update/remove),
transaction duration, and failures by stage. Health reporting MUST distinguish healthy and degraded
states; failures MUST be surfaced as events and logs, never silently ignored. Rationale: operators
need actionable visibility to run automated runtime updates safely.

### V. Test & Contract Discipline
Changes to reconciliation logic, store transaction flow, package resolution, or public contracts MUST
include automated tests. Bug fixes MUST include at least one regression test that fails before the
fix and passes after. Contract/integration coverage is mandatory for boundary changes between
runtime, store, and source/nuget components. Rationale: safe runtime mutation depends on proven,
repeatable behavior under change.

## Technical Boundaries

- Nuplane is infrastructure only and MUST remain host-neutral; it MUST NOT define plugin programming
	models or host-specific activation semantics.
- `Nuplane.Abstractions` MUST stay minimal and contain only stable, implementation-agnostic contracts
	and pure models.
- Host integrations MAY consume Nuplane events and contracts, but host-specific dependencies MUST NOT
	be introduced into core runtime/store/nuget/source packages.

## Delivery Workflow & Quality Gates

- Feature delivery MUST follow `spec -> plan -> tasks -> implement`, and each plan MUST pass an
	explicit Constitution Check before implementation.
- Pull requests MUST include: constitution check outcome, test evidence, and operational impact notes
	(observability, rollback/LKG behavior, and source trust implications when applicable).
- Breaking contract changes MUST include a migration note and semantic version impact statement.

### VI. Specification & Task Decomposition Discipline

Functional requirements and tasks MUST be decomposed so that each unit of work
maps cleanly to a single architectural concern. Conflating mechanism and driver
in a single requirement or task leads to partial implementations and silent gaps.

1. **Separate mechanism from driver.** Functional requirements that describe BOTH
   a capability/mechanism (e.g., reconciliation engine) AND a driver/trigger
   (e.g., polling hosted service, API endpoint, CLI command, event handler) MUST
   be decomposed into separate tasks — one for the engine, one for each
   invocation mechanism. Rationale: a single task that spans two independent
   architectural layers will be marked complete when only one layer is delivered.

2. **Prescriptive, not descriptive, requirements.** Functional requirements MUST
   name the concrete architectural element being required (e.g., "a
   BackgroundService using PeriodicTimer that invokes the reconciliation engine")
   rather than describing behavior abstractly ("polling-based reconciliation").
   Descriptive-only requirements produce ambiguous task decomposition and allow
   implementers to satisfy the letter while missing the intent. Rationale:
   prescriptive language makes gaps visible during plan review.

3. **One task ≙ one deployable artifact.** Each task MUST map to exactly one
   deployable artifact — a single class, file, or tightly coupled file group. If
   a task description implies multiple independent classes or architectural
   layers, it MUST be split before implementation begins. Rationale: artifact-
   level granularity makes progress auditable and prevents scope bleed.

4. **Configuration properties MUST have consumers.** Every options/configuration
   property that is defined (e.g., `PollInterval`) MUST have an explicit task
   that implements the component consuming that property. A defined-but-
   unconsumed configuration property is a specification gap signal and MUST be
   flagged during plan review. Rationale: orphan configuration indicates a
   missing implementation task.

## Governance

- This constitution is the highest-priority engineering policy for this repository; conflicting local
	conventions are superseded by this document.
- Amendments require a documented change proposal, explicit update of impacted templates/guidance
	files, and a Sync Impact Report appended at the top of this file.
- Versioning policy for this constitution follows semantic versioning:
	- MAJOR: principle removal/redefinition or governance changes that invalidate prior workflows.
	- MINOR: new principle/section or materially expanded normative requirements.
	- PATCH: clarifications, wording refinements, and non-semantic edits.
- Compliance review is mandatory in plan reviews and pull request reviews; unresolved MUST-level
	violations block merge.

**Version**: 1.1.0 | **Ratified**: 2026-03-02 | **Last Amended**: 2026-03-03
