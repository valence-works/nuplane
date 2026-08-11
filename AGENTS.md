# Repository Instructions

## Project Overview
Nuplane is a .NET runtime control plane for NuGet packages. It resolves desired package state from feeds, stores packages in a deterministic local store, applies transactional updates with last-known-good fallback, optionally loads assemblies, and emits runtime events for host applications.

## Canonical References
- Start with `README.md` for product framing, concepts, and sample workflows.
- Follow `docs/coding-conventions.md` for coding style, architecture rules, DI patterns, testing conventions, observability, and module boundaries.
- Use `docs/wiki/` for user-facing conceptual documentation.
- Use `specs/` for feature-specific requirements and validation history.
- Use `.github/agents/` and `.github/prompts/` only when working on Spec Kit-driven workflows.

## Agent Operating Principles
- Do not assume, hide confusion, or flatten uncertainty; surface questions, constraints, and tradeoffs explicitly.
- Write the minimum code that solves the defined problem; do not add speculative abstractions, features, or cleanup.
- Touch only the files and behavior required for the task; clean up only issues introduced by your own changes.
- Define success criteria before implementation, then iterate until the criteria are verified or clearly state what could not be verified.

## Repository Layout
- `src/`: Packable source projects. Most libraries multi-target `net8.0;net9.0;net10.0` through `src/Directory.Build.props`.
- `test/`: xUnit test projects targeting `net10.0` through `test/Directory.Build.props`.
- `samples/`: Sample host, sample abstractions, and sample plugin projects.
- `docs/`: Wiki pages, posts, and contributor-facing documentation.
- `specs/`: Feature specs, plans, tasks, contracts, and quickstarts.
- `build/` and `branding/`: Build support and project assets.

## Build And Test Commands
- Restore: `dotnet restore nuplane.sln`
- Build all projects: `dotnet build nuplane.sln`
- Run all tests: `dotnet test nuplane.sln`
- Run a targeted test project: `dotnet test test/<ProjectName>/<ProjectName>.csproj`
- Run targeted tests by name: `dotnet test <project-or-sln> --filter "FullyQualifiedName~<Name>"`

Run focused tests first for the area changed, then run the full solution when practical. If a command cannot be run, state why and identify the closest validation performed.

## Coding Rules
- Nullable reference types, implicit usings, deterministic builds, and warnings-as-errors are enabled.
- Source projects generate XML documentation; all public and protected APIs require XML docs.
- Package versions are centrally managed in `Directory.Packages.props`; do not add `Version` attributes to individual `PackageReference` items.
- Prefer `sealed record` for DTO/result types and sealed data-only options classes.
- Use file-scoped namespaces that mirror folder structure.
- Use `_camelCase` for private instance fields and PascalCase for private static fields; the naming policy in `Nuplane.sln.DotSettings` enforces this.
- Use `ArgumentNullException.ThrowIfNull(...)` for public API null guards.
- Keep options validation in `IValidateOptions<T>` implementations; do not add `IsValid()` methods to options classes.
- Use `[LoggerMessage]` source-generated logging for structured log methods.
- Never log secrets, credentials, or full exception stack traces at Information level.

## Architecture Guardrails
- Preserve deterministic reconciliation behavior and transactional safety.
- Do not leave package/store state partially mutated; use atomic pointer switches and LKG fallback patterns where applicable.
- Validate package sources against trust policy before resolving or activating packages.
- Keep `Nuplane.Runtime` dependency-lean; it must not reference `Microsoft.Extensions.Hosting.Abstractions`.
- Keep module-specific options, registration services, hosted services, and builder conveniences in the owning module package, not in the core `Nuplane` package.
- Every DI-registered service should have an interface. Register the concrete implementation first, then expose the interface via a factory delegate.
- Reconciliation pipeline middleware must call `next()` unless intentionally short-circuiting with a documented result.
- Preserve single-flight reconciliation semantics when `EnableSingleFlight` is enabled.

## Testing Conventions
- Test classes use `{TypeUnderTest}Tests`.
- Test methods use `MethodUnderTest_Scenario_ExpectedBehavior`.
- Follow Arrange / Act / Assert structure.
- Prefer `Assert.Single(...)` over count comparisons.
- Use `Assert.ThrowsAsync<T>` for expected async exceptions.
- Keep tests deterministic; avoid real network calls unless the test is explicitly integration-scoped and isolated.
- Use temporary directories/files for filesystem tests and clean them up where possible.

## Documentation And Specs
- Update `README.md`, `docs/wiki/`, or relevant `specs/` files when behavior or public usage changes.
- Keep repository-owned wiki pages under `docs/wiki/` self-sufficient for onboarding and evaluation.
- Feature work driven by specs should keep implementation, tests, and quickstart validation aligned with the corresponding `specs/<feature>/` directory.

## Git And Workspace Safety
- Do not overwrite unrelated local changes.
- Do not run destructive git commands such as `git reset --hard` or `git checkout --` unless explicitly requested.
- Before editing a file with existing modifications, inspect it carefully and preserve unrelated changes.
- Prefer small, reviewable changes that match existing project patterns.

## Active Technologies
- C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`.
- Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol, NuGet.Versioning, System.Runtime.Loader, xUnit, and NSubstitute.
- File-backed Nuplane store state and package install directories under configured state/package roots; package metadata is read from extracted package-root `nuplane.json`; no database.
- C# / .NET 8.0, 9.0, 10.0 source libraries; tests target .NET 10.0 + Microsoft.Extensions.Configuration, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Options, Microsoft.Extensions.Logging; existing Nuplane builder and feed registration APIs (027-keyed-feed-config)
- N/A for this feature; configuration is translated into runtime options and desired-state sources (027-keyed-feed-config)

## Recent Changes
- 017-dependency-closure-loading: Added C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0` + Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol and NuGet.Versioning already used by feed version resolution, System.Runtime.Loader, xUnit, NSubstitute
- 018-host-integrated-loading: Added host-integrated package loading using non-collectible assembly load contexts and framework-visible resolution catalog metadata.
- 019-tolerate-facade-packages: Added graph loading tolerance for no-assembly facade/support packages while preserving real loader failures.
