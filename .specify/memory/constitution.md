<!--
Sync Impact Report
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
- Removed sections:
	- None
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

**Version**: 1.0.0 | **Ratified**: 2026-03-02 | **Last Amended**: 2026-03-02
