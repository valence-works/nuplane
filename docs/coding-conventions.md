# Nuplane Coding Conventions

This document defines the coding standards and conventions for the Nuplane project. All contributors must follow these guidelines to maintain consistency and quality across the codebase.

## Table of Contents

- [General Principles](#general-principles)
- [Public API](#public-api)
- [Naming Conventions](#naming-conventions)
- [Code Organization](#code-organization)
- [XML Documentation](#xml-documentation)
- [Dependency Injection](#dependency-injection)
- [Error Handling](#error-handling)
- [Testing](#testing)
- [Reconciliation Engine Patterns](#reconciliation-engine-patterns)
- [Configuration & Options](#configuration--options)
- [Observability](#observability)
- [File & Project Structure](#file--project-structure)
- [Version Parsing](#version-parsing)
- [CancellationToken](#cancellationtoken)
- [Code Style Quick Reference](#code-style-quick-reference)

---

## General Principles

1. **Determinism** — Reconciliation behavior must be deterministic. Given the same desired state and actual state, the engine must produce the same result.
2. **Transactional Safety** — State mutations use atomic pointer switches with rollback/LKG (last known good) semantics. Never leave state in an inconsistent position.
3. **Trusted Sources** — All package sources must be validated against the trust policy. Secrets must never appear in logs, configuration files checked into source control, or exception messages.
4. **Fail-Safe Defaults** — Options default to the safest value. For example, `EnableAutomaticReconciliation` defaults to `false`; `EnableSingleFlight` defaults to `true`.
5. **Immutability** — Prefer `sealed record` for data transfer types. Use `readonly` fields and properties where possible.

---

## Public API

Types in Nuplane packages are internal by default. Keep a type public when a
supported host, package author, or extension needs to call or implement it, or when a supported
public signature uses it. First-party modules may share implementation types through scoped
`InternalsVisibleTo` declarations.

| Purpose | Supported entry points |
| --- | --- |
| Host configuration | `AddNuplane`, `NuplaneBuilder`, feed builders, and configuration options and enums |
| Reconciliation and observers | `IReconciliationService`, `IReconciliationTriggerIngress`, `INuplaneObserver`, `PackageChangeSet`, and the scoped, path-independent, and post-loading observer contracts |
| Catalog reads | `IActivePackageCatalog`, `IPackageAssemblyCatalog`, `IPackageTypeFinder`, and `IPackageLoadStateCatalog` |
| Host-free tooling and package content | `NuplaneRestore`, `NuplaneStore`, `NuplaneHostIntegratedLoader`, `PackageContent`, and `IPackageArchiveReader` |
| Coordinated stores | Builder participation and root declarations, admitted snapshots, package-store admission and graph-use leases, and the documented session-aware companions for store state, serializers, and callbacks |
| Host customization | Desired-state sources and contributors, package resolution and acquisition contracts, secret reference providers, operational state contributors, package metadata readers, activation gates, and load-mode advisors |
| Admin operations | `AddNuplaneAdmin`, `AddNuplanePackageStoreMaintenance`, `INuplaneAdminOperations`, and `INuplanePackageStoreMaintenanceOperations` with their request and result records. Hosts can share the registered `ManualReconcileCoordinator` across service providers. |

Resolve these contracts through the documented host registration APIs. Concrete pipeline,
persistence, observability, and registration classes belong to the implementation. External code
that composed these classes directly can use `AddNuplane`, `NuplaneRestore`, or `NuplaneStore`
for the host or offline workflow it needs.

`Microsoft.CodeAnalysis.PublicApiAnalyzers` checks the `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt` manifests in every packable project during ordinary builds. Review the API diff
with any intentional addition or change. Record new or changed signatures in the unshipped
manifest and promote reviewed entries to the shipped manifest with the release. The manifest
includes nullability. Package builds also run .NET SDK package validation. When the
`NUPLANE_PACKAGE_VALIDATION_BASELINE` repository variable names a stable version on NuGet.org,
the publication workflow compares packages against that binary compatibility baseline. Set
the variable once the first stable release is available on NuGet.org.

## Naming Conventions

### Types

| Kind | Convention | Example |
|------|-----------|---------|
| Interface | `I` prefix, PascalCase | `IReconciliationService`, `IPackageResolver` |
| Class | PascalCase, no prefix | `ReconciliationService`, `PackageLoader` |
| Record | PascalCase | `ReconciliationRunResult`, `FeedResolutionDecision` |
| Enum | PascalCase, singular | `FeedTrustLevel`, `CleanupExecutionMode` |
| Enum member | PascalCase | `FeedTrustLevel.Trusted` |

### Members

| Kind | Convention | Example |
|------|-----------|---------|
| Public property | PascalCase | `PollInterval`, `MaxRetryAttempts` |
| Private instance field | `_camelCase` (underscore prefix) | `_reconciliationOptions`, `_cycleLock` |
| Private static field | PascalCase | `JsonOptions`, `AssemblyNames` |
| Parameter | camelCase | `cancellationToken`, `configureFeeds` |
| Local variable | camelCase | `feedResolutionOptions`, `validationErrors` |
| Constant | PascalCase | `EmptyChangeSet` |
| Method | PascalCase, verb phrase | `TriggerManualAsync`, `EvaluateAsync` |
| Async method | `Async` suffix | `ResolveAsync`, `GetDesiredAsync` |

The private instance field rule is enforced by the naming policy in `Nuplane.sln.DotSettings`
(`AccessRightKinds="Private"`, `Prefix="_"`, `Style="aaBb"`), so ReSharper and Rider flag deviations
automatically. A small number of older files still use bare `camelCase`; treat `_camelCase` as correct
when adding fields or touching those files.

### Files

- One public type per file (exceptions: closely related nested types).
- File name matches the primary type name: `ReconciliationService.cs`.
- Organize files into domain-based folder groupings (e.g., `Reconciliation/`, `Observability/`, `Configuration/`).
- When a folder exceeds ~15 files, split into subfolders (e.g., `Reconciliation/Models/`, `Reconciliation/FeedPolicy/`).
- Extension methods go in `Extensions/` subdirectory with `{Feature}ServiceCollectionExtensions.cs` naming.

---

## Code Organization

### Namespace Layout

Use file-scoped namespaces (`namespace X;`). The namespace must mirror the folder path:

```
Nuplane.Abstractions                 — Public contracts, DTOs, enums shared across packages
Nuplane                              — Consumer-facing DI registrations, restore, and package content APIs
Nuplane.Reconciliation               — Reconciliation engine orchestration
Nuplane.Reconciliation.Configuration — Reconciliation options classes, validators
Nuplane.Reconciliation.Models        — Result/model records (data-only types)
Nuplane.Reconciliation.Middleware    — Pipeline middleware stages
Nuplane.Feeds                        — Feed access, configuration, credentials, resolution policy
Nuplane.Store                        — State persistence, transactions, activation, cleanup
Nuplane.Sources                      — Desired-state discovery and source implementations
Nuplane.Sources.Directory            — Directory feeds and watchers
Nuplane.Hosting                      — Hosted services and startup recovery
Nuplane.Observability                — Logging, metrics, telemetry
Nuplane.Health                       — Health evaluation
Nuplane.Events                       — Observer event dispatching
Nuplane.Versioning                   — Shared version parsing and comparison
Nuplane.Loading                      — Assembly loading, unloading, and public loading contracts
Nuplane.Admin                       — Optional administration services
Nuplane.Admin.Api                   — ASP.NET Core admin endpoints
Nuplane.Loading.Api                 — ASP.NET Core load-state endpoint
```

### Using Directives Order

1. `System.*` namespaces (implicit via global usings)
2. `Microsoft.*` namespaces
3. `Nuplane.*` namespaces (alphabetical)
4. Project-local namespaces

No blank lines between groups. Remove unused `using` directives.

---

## XML Documentation

All **public** and **protected** types and members must have XML doc comments:

```csharp
/// <summary>
/// Evaluates the trust policy for a package/feed pair.
/// </summary>
/// <param name="request">The package request to evaluate.</param>
/// <returns>The trust policy evaluation outcome.</returns>
public FeedTrustPolicyOutcome Evaluate(PackageRequest request) { ... }
```

- Use `<see cref="..." />` for cross-references.
- Use `<see langword="true" />`, `<see langword="false" />`, `<see langword="null" />` for keywords.
- Use `<inheritdoc />` on interface implementations when the base doc suffices.
- Document `<exception cref="..." />` for all thrown exceptions.
- Internal types may omit doc comments but should include a brief `///` summary when the purpose is not self-evident.

---

## Dependency Injection

### Interface Extraction

Extract an `I`-prefix interface for every DI-registered service (e.g., `IReconciliationService`, `IDesiredActualDiffEngine`). Register both the concrete type and the interface:

### Registration Pattern

Register concrete types first, then expose them via their interface using a factory delegate:

```csharp
services.AddSingleton<ReconciliationService>();
services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());
```

This allows:
- Resolving the concrete type in integration tests for detailed assertions.
- Resolving the interface in production code for loose coupling.

### Lifetime Rules

| Type | Lifetime | Reason |
|------|----------|--------|
| Options classes | `Singleton` | Immutable after startup |
| Services | `Singleton` | Shared runtime components |
| Hosted services | `AddHostedService<T>` | Framework-managed |

### Extension Method Conventions

- One `Add{Feature}` method per feature area.
- Accept optional `Action<TOptions>?` parameters for configuration.
- Wire options via `AddOptions<T>()`, register `IValidateOptions<T>`, and call `ValidateOnStart()` for required options.
- Return `IServiceCollection` for chaining.

---

## Error Handling

### Validation

- Use `ArgumentNullException.ThrowIfNull(param)` for null guards.
- Implement options validation through `IValidateOptions<T>` validators.
- Use `ValidateOnStart()` for required options to fail fast during startup.
- Keep options classes data-only; do **not** add `IsValid()` methods.
- Cross-validate related options via dedicated validator classes (e.g., `FeedCredentialOptionsValidator` wrapped in a cross-options `IValidateOptions<T>` implementation).

### Exception Types

| Exception | When |
|-----------|------|
| `ArgumentNullException` | Null argument passed to public API |
| `ArgumentException` | Invalid configuration or parameter value |
| `InvalidOperationException` | Operation not valid for current state |
| `FeedUnavailableException` | NuGet feed unreachable during resolution |
| `OperationCanceledException` | Cancellation token triggered |

### Retry & Resilience

- Transient failures use exponential backoff via `ReconciliationRetryPolicy`.
- Max retry attempts and backoff caps are configurable via `ReconciliationOptions`.
- Non-transient failures record the package as failed and continue with remaining packages.

---

## Testing

### Test Project Layout

```
test/
  Nuplane.Runtime.Tests/            — Unit tests for the core Nuplane package
  Nuplane.Store.Tests/              — Unit tests for Store
  Nuplane.NuGet.Tests/              — Unit tests for NuGet feed integration
  Nuplane.Loading.Tests/            — Unit tests for Loading (fixtures in Nuplane.Loading.Tests.Fixtures.*)
  Nuplane.Integration.Tests/        — Integration / contract tests
```

### Test Naming

```
MethodUnderTest_Scenario_ExpectedBehavior
```

Example: `AddNuplane_ResolvesAndRunsWithoutLoadingRegistration`

For test classes: `{TypeUnderTest}Tests` — e.g., `FeedTrustPolicyEvaluatorTests`.

### Test Structure

Use the **Arrange / Act / Assert** pattern. Keep tests focused on a single behavior.

```csharp
[Fact]
public async Task TriggerManualAsync_WhenSourcesEmpty_ReturnsEmptyChangeSet()
{
    // Arrange
    var sut = CreateService(sources: []);

    // Act
    var result = await sut.TriggerManualAsync(CancellationToken.None);

    // Assert
    Assert.NotNull(result);
    Assert.Empty(result.FailedPackages);
}
```

### Assertions

- Use `Assert.Equal`, `Assert.NotNull`, `Assert.Empty`, `Assert.Single`.
- Prefer `Assert.Single` over `Assert.Equal(1, collection.Count())`.
- Use `Assert.ThrowsAsync<T>` for expected exceptions.

### Documentation and sample walkthroughs

Validate maintained links whenever you move or rename a repository path. The same CI job compiles
each README C# example in an external web project and runs the sample against the package-drop
and query walkthroughs in the README, Getting Started guide, and query catalog quickstart.

```bash
fixture="$(mktemp -d)/nuplane-docs"
python3 build/check-docs.py links
python3 build/check-docs.py restore "$fixture"
python3 build/check-docs.py snippets "$fixture"
python3 build/check-docs.py sample "$fixture"
```

Use the .NET 10 SDK and Python 3.9 or later on Linux or macOS. Only restore needs access to NuGet.
The sample uses a fresh store and an OS-assigned loopback port for each walkthrough. Remove the
temporary fixture when you finish.

---

## Reconciliation Engine Patterns

### Middleware Pipeline

`ReconciliationComposition` assembles the middleware stages in this order:

1. **LockFileCycleMiddleware** — If the lock coordinator supports a cycle snapshot, capture it and publish a generated lock only after successful completion.
2. **DesiredStateReadMiddleware** — Read and aggregate desired state from sources.
3. **PackageResolutionMiddleware** — Resolve packages and dependencies using the cycle's available lock constraints.
4. **TrustAndLockGateMiddleware** — Report trust and lock outcomes.
5. **DiffAndChangeEventMiddleware** — Compute the diff and publish planned changes.
6. **TransactionExecutionMiddleware** — Apply resolved package transactions and calculate the proposed active versions.
7. **CleanupMiddleware** — Publish active state and evaluate cleanup policy. Ordinary stores with automatic loading prepare candidate graphs before they publish active state.
8. **HealthAndMetricsMiddleware** — Dispatch committed changes, evaluate cycle health, and record metrics.

Each middleware receives a `ReconciliationCycleContext` and calls `next()` unless it intentionally ends the cycle. Coordinated stores with staged loading defer active-state publication, cleanup, and observer completion. Loading runs outside the short root and state lock scopes. Reconciliation reacquires admission and verifies the state revision and protected graph before it publishes the transition.

### Single-Flight Protection

Only one reconciliation cycle may execute at a time when `EnableSingleFlight` is `true`. Concurrent invocations return a `Skipped` result.

### Correlation & Tracing

Every reconciliation cycle gets a unique correlation ID via `CorrelationContext`.

- Use `System.Diagnostics.Activity` with a shared `ActivitySource`:

```csharp
public static readonly ActivitySource Source = new("Nuplane.Runtime", "0.1.0");
```

- When an `ActivitySource` listener is active (e.g., OpenTelemetry), start an `Activity` named `reconciliation.cycle` with `ActivityKind.Internal` and set tags for the correlation ID.
- When no listeners are registered, fall back to an `AsyncLocal<string?>` scope so the correlation ID remains available throughout the async call chain.
- Access the current correlation ID via `CorrelationContext.Current`, which returns `Activity.Current?.Id` when available, or the `AsyncLocal` fallback value.
- Scopes are disposable — always use `using` to ensure cleanup.

---

## Configuration & Options

### Options Class Pattern

```csharp
public sealed class ReconciliationOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
}

internal sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
        => options.PollInterval > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("PollInterval must be greater than zero.");
}
```

- All options classes are `sealed` data-only types.
- Provide sensible defaults on options properties.
- Do not embed validation logic in options classes.
- Register `IValidateOptions<T>` validators for each options type.
- Use `ValidateOnStart()` for options required at startup.
- Complex cross-option validation goes in dedicated validators.

---

## Observability

### Logging

- Use `ILogger<T>` via DI.
- Use `[LoggerMessage]` source generators for all structured log methods. Define partial methods on the class:

```csharp
[LoggerMessage(
    EventId = 1000,
    Level = LogLevel.Information,
    Message = "Reconciliation cycle started [CorrelationId={CorrelationId}, RequestCount={RequestCount}]")]
private static partial void CycleStarted(ILogger logger, string correlationId, int requestCount);
```

- Log levels:
  - `Debug` — Detailed decision paths, feed resolution steps, trust policy evaluations
  - `Information` — Reconciliation cycle start/complete, major state changes, load/unload outcomes
  - `Warning` — Degraded state, fallback paths taken, observer callback errors
  - `Error` — Unhandled exceptions, failed packages
- Never log secrets, credentials, or full exception stack traces at `Information` level.

### Metrics

- Use `System.Diagnostics.Metrics` with the `nuplane.*` namespace.
- Counter names follow the pattern: `nuplane.{area}.{metric}` (e.g., `nuplane.dryrun.planned.packages`).
- Record metrics via `ReconciliationMetrics` and `ReconciliationTelemetry`.

### Health

- `ReconciliationHealthEvaluator` assesses system health after each cycle.
- Health is `Degraded` when any packages failed in the last cycle.

---

## File & Project Structure

### Project Dependencies

```
Nuplane (consumer package)
  └── Nuplane.Abstractions

Nuplane.Loading (optional)
  ├── Nuplane
  └── Nuplane.Loading.Abstractions
        └── Nuplane.Abstractions

Nuplane.Admin (optional)
  └── Nuplane

Nuplane.Admin.Api (ASP.NET Core)
  └── Nuplane.Admin

Nuplane.Loading.Api (ASP.NET Core)
  └── Nuplane.Loading.Abstractions
```

- **Nuplane** owns core composition, remote and directory feeds, reconciliation, store state, and archive reads.
- **Optional packages** own their extensions and configuration. The `Nuplane.Loading` package supplies `AutoloadPackages`; there are no separate hosting packages.

### Multi-Targeting

The solution targets `net10.0`. Use the .NET 10 SDK to build and test it.

### Build Policy

The root `Directory.Build.props` enforces these settings across all projects:

| Property | Value | Purpose |
|----------|-------|---------|
| `TreatWarningsAsErrors` | `true` | No warnings allowed; every warning is a build error |
| `Deterministic` | `true` | Reproducible builds |
| `Nullable` | `enable` | Nullable reference types enforced project-wide |
| `ImplicitUsings` | `enable` | Common `System.*` namespaces are auto-imported |
| `GenerateDocumentationFile` | `true` (src only) | Enforces XML doc comments on public API surface |

Combined with `TreatWarningsAsErrors`, missing XML documentation on public types/members causes a build failure.

### Central Package Management

Package versions are managed centrally in `Directory.Packages.props`. Never specify versions in individual `.csproj` files — use `<PackageReference Include="..." />` without a `Version` attribute.

---

## Module Ownership & Builder Integration

### Module Boundary Rule

Optional loading and admin services live in their owning packages.

| Concern | Owner |
|---------|-------|
| Options classes | Module implementation package (`Nuplane.Loading`) |
| Registration services | Module implementation package |
| Hosted services | Module implementation package (`Hosting/`) |
| Registration and endpoint extensions | Package that owns the service or endpoint |
| Loading builder extension | `Nuplane.Loading` |
| Configuration-driven setup translation | Module implementation package (`Configuration/`) |

### Registration Surface Rules

- Use one supported registration path per module. Loading uses `NuplaneBuilder.AutoloadPackages` and admin uses `IServiceCollection.AddNuplaneAdmin`.
- Use the owning package for builder and service collection extensions.
- Keep repeat registration deterministic and deduplicate singletons and observers.
- Core `Nuplane` owns runtime composition and both remote and directory-backed feed registration.

### Builder Integration Pattern

Loading builder extensions delegate to loading-owned registration services. Directory feeds are
registered by core `AddNuplane` from `Setup:Feeds`, or in code through
`Nuplane.Sources.Directory.Builder.AddDirectoryFeed`. Both paths share one directory feed
registration and validation path.

### Adding a New Module

1. Create the optional package with its options and registration services.
2. Choose a registration entry point that fits the host composition.
3. Add tests in the module's test project and integration test project.
4. Do NOT add module-specific code to the core `Nuplane` package.

---

## Version Parsing

Use the shared `VersionKey` and `NuGetVersionRangeParser` types from `Nuplane.Versioning` for all version comparison and selection logic.

- **`VersionKey`** — An `internal readonly record struct` that parses a semver string into `(Major, Minor, Patch, Suffix)` components and implements `IComparable<VersionKey>`. Use `VersionKey.Create(string)` to parse, then compare or sort:

```csharp
var ordered = candidates
    .OrderByDescending(x => VersionKey.Create(x.Version))
    .ToList();
```

- **`NuGetVersionRangeParser`** — Selects a concrete version from a NuGet version-range expression (e.g., `[1.0.0,2.0.0)`). Use `NuGetVersionRangeParser.SelectVersion(range)` in package resolvers.

Never hand-roll version parsing or comparison logic — always delegate to these shared utilities.

---

## CancellationToken

- Always accept `CancellationToken` as the last parameter on `async` methods.
- Forward the token through the entire async call chain — never drop it.
- Inside loops that may run for many iterations, call `cancellationToken.ThrowIfCancellationRequested()` to allow prompt cancellation:

```csharp
foreach (var package in packages)
{
    cancellationToken.ThrowIfCancellationRequested();
    await ProcessAsync(package, cancellationToken);
}
```

- Use `CancellationToken.None` only in tests or top-level entry points where no cancellation signal is available.

---

## Code Style Quick Reference

- **Access modifiers**: Always explicit (`public`, `private`, `internal`).
- **`var`**: Use when the type is obvious from the right-hand side.
- **Expression-bodied members**: Use for single-expression methods and properties.
- **Primary constructors**: Use for record types and simple DI constructors.
- **File-scoped namespaces**: Always use `namespace X;` (not block-scoped).
- **Nullable reference types**: Enabled project-wide. Annotate nullability explicitly.
- **`sealed`**: Seal all classes unless inheritance is an intentional design point.
- **`readonly`**: Mark fields `readonly` when they are assigned only in the constructor.
