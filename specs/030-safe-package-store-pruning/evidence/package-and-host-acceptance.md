# Package and actual-host acceptance plan

This is the T124 acceptance plan for [Spec 030](../spec.md), not an executed
release or host-acceptance report. The cross-repository source of truth remains
[Foundation Program 2500](https://github.com/elsa-workflows/elsa-foundation/issues/2500),
its [release plan](https://github.com/elsa-workflows/elsa-foundation/blob/main/docs/reports/modular-hosting-release-plan.md),
and adoption [Feature 2509](https://github.com/elsa-workflows/elsa-foundation/issues/2509).
Nuplane contains generic graph, store, admission and lifetime mechanisms. Elsa
readiness/schema policy and CShells generation composition stay in their owning
repositories; this plan adds no production dependency on either consumer.

## Acceptance boundaries and order

| Boundary | Required direct evidence | Insufficient substitute |
|---|---|---|
| Nuplane runtime admission | Actual enrolled two-state compositions sharing a physical package root; short ownership precedes package IO; selected complete graph protection precedes retained IO | A lock probe, synthetic graph inventory or package/version-only projection |
| Overlapping Loading generations | Gate A before its first actual retained package read, release A's short operation ownership, commit/load B through a second DI composition, then resume A; both real contexts can read their graph and keep sentinels busy | Reading a fixture file under a manually created pin, or keeping A's root lock held while B waits |
| Manual pruning | Actual owned-store preview and confirmed fresh execution, active/LKG/live-use protection, native no-follow quarantine/deletion, partial outcomes and crash recovery on supported platforms | Pure planner output, mocked deletion, or the non-destructive overlap test alone |
| Nuplane packages | Exact reviewed merge/main source and green gates; all eight stable public archives match publication artifacts and package metadata; external consumers run on .NET 8/9/10 | Private pack, preview qualification, tag creation alone or package availability alone |
| CShells integration | All ten stable packages, including CShells.Nuplane, reference the intended released Nuplane family and pass actual package-to-serving-shell lifecycle proof | Core CShells tests or an adapter archive without a serving consumer |
| Foundation adoption | Stable pins/reached locks, clean-cache locked restore, actual Host/Workbench startup/reload/drain/readability/backend proof, architecture/maps and resulting-main gates | Earlier public-preview proof, source-only builds or CShells.Workbench tests |

The actual non-destructive runtime/Loading gate precedes recursive deletion
implementation. All Spec 030 acceptance remains required before final Nuplane
release. Publish Nuplane first, update and qualify the CShells.Nuplane dependency,
publish the complete CShells family, then finish Foundation stable adoption. A
failure leaves the owning gate open; do not infer completion from issue closure.

## Upstream package audit

Nuplane's [publisher](../../../.github/workflows/publish-packages.yml) packs all
eight source projects: Nuplane, Nuplane.Abstractions, Nuplane.Loading,
Nuplane.Loading.Abstractions, Nuplane.Loading.Api, Nuplane.Admin,
Nuplane.Admin.Api and Nuplane.Sources.Directory. Source assets target net8.0,
net9.0 and net10.0. Verify the actual project list and target groups again at the
release commit; fail on missing, unexpected or mixed-version internal packages.

For every archive retain its public feed URL, SHA-256, nuspec package identity,
version, repository commit, dependency groups and framework assets. Download the
pipeline artifacts from the same successful publication run and compare archive
bytes. Verify Darwin native shim assets/manifest and Linux/Windows runtime assets
where the Nuplane package advertises them; source-level native tests do not prove
published runtime asset selection. Do not claim symbol-feed publication from a
local snupkg or an uploaded workflow artifact.

Run an external, package-only consumer outside every source checkout using
isolated NuGet package and HTTP caches. It must use public PackageReference
restore, not project references or private candidate feeds. Run each actual target
runtime (.NET 8/9/10), record loaded DLL paths and hashes, and compare them with
the public archive assets. Exercise public DI registration, state persistence,
completed change/removal notifications, startup cancellation/failure/recovery and
the new enrolled admission/lifetime/maintenance API. Native maintenance uses only
the harness's freshly created stores and packages. A successful Linux publication
job cannot replace the supported-platform native admission/deletion gates.

The CShells publisher currently enumerates CShells, CShells.Abstractions,
CShells.AspNetCore, CShells.AspNetCore.Abstractions, CShells.AspNetCore.Testing,
CShells.FastEndpoints, CShells.FastEndpoints.Abstractions, CShells.Management.Api,
CShells.Nuplane and CShells.Providers.FluentStorage. Repeat the all-archive audit,
including adapter Nuplane dependency metadata, and package-only consumers on the
three target runtimes. Prove shared host objects/disposal, distinct overlapping
generation objects, ordered catalog notifications, protected build/provider
teardown, package add/replace/removal-to-empty and serving generation promotion.

On 2026-10-10 the live GitHub latest releases were Nuplane 0.0.10 and CShells
0.0.29; development bases intend 0.0.11 and 0.0.30. Refresh tags, releases, NuGet
and merged version files immediately before selecting releases. Follow the
existing 0.0.N pattern and never reuse a published version. A GitHub published
release triggers the publishers; a pushed tag alone does not fulfill delivery.

## Foundation stable adoption and actual-host proof

Adopt existing preparation PRs
[2529](https://github.com/elsa-workflows/elsa-foundation/pull/2529) and
[2530](https://github.com/elsa-workflows/elsa-foundation/pull/2530); inspect their
current claims, heads and checks before editing. Their public-preview evidence
does not establish final stable acceptance. Preserve Foundation's root-owned
readability participant, borrowed nondisposable shell source, startup/reload
defaults, EF refusal interpretation and schema/readiness policy. Complete the
separately outstanding custom-registry settled-readiness proof using the same
registered objects; do not treat raw candidate visibility as settlement.

Change coherent central pins together, regenerate every reached lock through
Foundation's documented project-graph restore, then prove locked restore with
empty run-owned package/HTTP caches and the committed package-source mappings.
Verify the exact stable package hashes and loaded DLL identities in each owned
host. Regenerate affected maps only after the new locks are correct, review all
generated changes and stage the manifest with its changed maps. No unrelated
checkout edits or peer-session work belong in the adoption PR.

Run the affected Foundation test projects at the final adoption head:

- tests/essentials/Modularity/Tests/Elsa.Modularity.Tests.csproj
- tests/essentials/Workbench/Tests/Elsa.Workbench.Tests.csproj
- tests/essentials/Cluster/Readability/Tests/Elsa.Cluster.Readability.Tests.csproj
- tests/essentials/Cluster/Tests/Elsa.Cluster.Tests.csproj
- tests/essentials/Cluster/EntityFrameworkCore/Tests/Elsa.Cluster.EntityFrameworkCore.Tests.csproj
- tests/essentials/Architecture/Elsa.Architecture.Tests.csproj

Retain raw results and inspect skips rather than transferring earlier counts.
Use the normal build-slot wrapper and build the affected graph once before
running its suites; do not run parallel solution builds on the shared machine.
Run the architecture scripts required by the final Foundation workflow and
`dotnet run --project tools/maps/Elsa.Maps.Generator -- check` after the coherent
restore. The Nuplane repository does not own or emulate these Elsa gates.

The real-process journey must start both Foundation.Host and Elsa.Workbench from
owned temporary configuration/state/database roots. Observe initial serving,
install/replace packages, hold an old request/provider across promotion, verify
both generations remain readable, drain and confirm provider teardown, remove to
an empty active graph, and reintroduce a package. Cover startup refusal/recovery,
cancellation and retained schema constraints while the old provider is live.
Assembly collection belongs to Foundation 2362; do not replace actual lifetime
evidence with an unload request or claim GC/physical pruning from provider drain.

Rebuild Workbench and run the relevant REST suites from its current e2e-tests
README, including Test-WorkflowFlow.ps1 against a harness-owned server. Require
successful workflow completion, expected WriteLine output and zero incidents.
Use the existing owned-server lifecycle helpers, a free loopback port and fresh
SQLite state; stop only launched children and verify process exit. Add the
relevant package/reload/failure journey rather than claiming the workflow smoke
alone proves modular hosting. Known-issue/skipped branches remain explicit.

## Evidence and completion record

Every gate records exact source SHA/tree, commands/configuration/runtime and
platform/filesystem, terminal exit, test counters/skips, raw log/TRX hashes,
owned-process termination and any failed attempts/corrections. Behavioral safety
claims need causal negative controls with byte-identical restored source and a
passing corrected run. Root and independent self-review accompany automated
gates; Copilot and Greptile are waived by the program owner.

Post review/gate evidence to each owning issue/PR, keep project Status and
Verification State consistent, merge only qualified heads and inspect resulting
main. Finish Program 2500 only after release package identities, stable
Foundation adoption and all promised end-to-end behavior are verified. Production
deployment and existing customer-store mutation are outside this program.
