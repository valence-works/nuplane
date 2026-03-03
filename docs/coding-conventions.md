# Nuplane Coding Conventions

This document defines the coding standards and conventions for the Nuplane project. All contributors must follow these guidelines to maintain consistency and quality across the codebase.

## Table of Contents

- [General Principles](#general-principles)
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
| Private field | camelCase (no underscore prefix) | `reconciliationOptions`, `cycleLock` |
| Parameter | camelCase | `cancellationToken`, `configureFeeds` |
| Local variable | camelCase | `feedResolutionOptions`, `validationErrors` |
| Constant | PascalCase | `EmptyChangeSet` |
| Method | PascalCase, verb phrase | `TriggerManualAsync`, `EvaluateAsync` |
| Async method | `Async` suffix | `ResolveAsync`, `GetDesiredAsync` |

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
Nuplane.Abstractions          — Public contracts, DTOs, enums shared across packages
Nuplane.Runtime               — Core runtime logic
Nuplane.Runtime.Configuration — Options classes, validators
Nuplane.Runtime.Reconciliation        — Reconciliation engine orchestration
Nuplane.Runtime.Reconciliation.Models — Result/model records (data-only types)
Nuplane.Runtime.Reconciliation.FeedPolicy — Feed trust/resolution policy evaluators
Nuplane.Runtime.Reconciliation.Middleware — Pipeline middleware stages
Nuplane.Runtime.Observability  — Logging, metrics, telemetry
Nuplane.Runtime.Health         — Health evaluation
Nuplane.Runtime.Events         — Observer event dispatching
Nuplane.Runtime.Sources        — Desired-state source abstractions
Nuplane.Store                  — State persistence, transactions, activation
Nuplane.Loading                — Assembly loading, unloading, ALCs
Nuplane.Loading.Abstractions   — Loading contracts
Nuplane                        — Consumer-facing DI registrations & hosted services
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
  Nuplane.Runtime.Tests/       — Unit tests for Runtime
  Nuplane.Store.Tests/          — Unit tests for Store
  Nuplane.Integration.Tests/    — Integration / contract tests
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

---

## Reconciliation Engine Patterns

### Middleware Pipeline

The reconciliation engine uses a middleware pipeline (`ReconciliationPipeline`) with discrete stages:

1. **DesiredStateReadMiddleware** — Read desired package state from sources
2. **PackageResolutionMiddleware** — Resolve packages from feeds
3. **TrustAndLockGateMiddleware** — Evaluate trust policy and lock file
4. **PackageLoadingMiddleware** — Load assemblies via ALCs
5. **DiffAndChangeEventMiddleware** — Compute diff and emit change events
6. **TransactionExecutionMiddleware** — Execute atomic state mutations
7. **UnloadMiddleware** — Unload obsolete assemblies
8. **CleanupMiddleware** — Clean up old package versions
9. **HealthAndMetricsMiddleware** — Evaluate health and record metrics

Each middleware receives a `ReconciliationCycleContext` and must call `next()` to continue the pipeline.

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
  └── Nuplane.Runtime
        ├── Nuplane.Abstractions
        ├── Nuplane.Store
        ├── Nuplane.Sources.Directory
        └── Nuplane.Loading (optional)
              └── Nuplane.Loading.Abstractions
```

- **Nuplane.Runtime** must not reference `Microsoft.Extensions.Hosting.Abstractions` — keep it dependency-lean.
- **Nuplane** (consumer package) owns the hosted service and DI registration that bridges Runtime and Hosting.

### Multi-Targeting

The solution targets `net8.0`, `net9.0`, and `net10.0`. Ensure all code compiles against all targets. Use `#if` directives only when absolutely necessary for API differences.

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

## Version Parsing

Use the shared `VersionKey` and `NuGetVersionRangeParser` types from `Nuplane.Runtime.Versioning` for all version comparison and selection logic.

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
