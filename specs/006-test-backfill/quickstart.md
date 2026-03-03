# Quickstart: Test Backfill

**Branch**: `006-test-backfill` | **Date**: 2026-03-03

A fast-path guide for a contributor implementing this spec from scratch.

---

## Prerequisites

- .NET 10 SDK installed
- Solution checked out on branch `006-test-backfill`
- `dotnet build` passes clean with zero warnings from `main`

---

## Step 1 — Contract Change (do this first; gates all other work)

1. Create `src/Nuplane.Runtime/Reconciliation/Models/DesiredAggregateResult.cs`:
   ```csharp
   namespace Nuplane.Runtime.Reconciliation.Models;

   /// <summary>Result of aggregating desired package requests from multiple sources.</summary>
   public sealed record DesiredAggregateResult(
       IReadOnlyList<PackageRequest> Requests,
       IReadOnlyDictionary<string, Exception> SourceErrors);
   ```

2. Update `IDesiredStateAggregator.AggregateAsync` return type to `Task<DesiredAggregateResult>`.

3. Update `DesiredStateAggregator.AggregateAsync`: wrap each source's `GetDesiredAsync` in a `try/catch`, collect healthy results, populate `SourceErrors` dict. Return `new DesiredAggregateResult(requests, sourceErrors)`.

4. Update `DesiredStateReadMiddleware`: unpack `result.Requests` for `context.DesiredRequests`; iterate `result.SourceErrors` and call `failureRecorder.RecordAsync(...)` per entry.

5. `dotnet build` — must produce zero warnings/errors before proceeding.

---

## Step 2 — Create Nuplane.Loading.Tests.Fixtures

```bash
# From repo root
mkdir -p test/Nuplane.Loading.Tests.Fixtures
```

Create `test/Nuplane.Loading.Tests.Fixtures/Nuplane.Loading.Tests.Fixtures.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

Create one exportable type (`FixtureMarker.cs`) — this gives the project a non-empty DLL:
```csharp
namespace Nuplane.Loading.Tests.Fixtures;
/// <summary>Marker type providing a resolvable assembly path for ALC tests.</summary>
public static class FixtureMarker { }
```

Add the project to `Nuplane.sln`:
```bash
dotnet sln add test/Nuplane.Loading.Tests.Fixtures/Nuplane.Loading.Tests.Fixtures.csproj
```

---

## Step 3 — Create Nuplane.Loading.Tests

```bash
mkdir -p test/Nuplane.Loading.Tests
```

Create `test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Nuplane.Loading\Nuplane.Loading.csproj" />
    <ProjectReference Include="..\..\src\Nuplane.Loading.Abstractions\Nuplane.Loading.Abstractions.csproj" />
    <ProjectReference Include="..\Nuplane.Loading.Tests.Fixtures\Nuplane.Loading.Tests.Fixtures.csproj" />
  </ItemGroup>
</Project>
```

Add to solution:
```bash
dotnet sln add test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
```

---

## Step 4 — Implement Test Classes (in any order within each group)

### Group 1: Middleware tests
Path: `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/`

Each file follows this pattern:
```csharp
namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class DesiredStateReadMiddlewareTests
{
    // Hand-rolled fake inner class
    private sealed class FakeDesiredStateAggregator(DesiredAggregateResult result) : IDesiredStateAggregator
    {
        public Task<DesiredAggregateResult> AggregateAsync(...) => Task.FromResult(result);
    }

    [Fact]
    public async Task InvokeAsync_PopulatesDesiredRequests_AndCallsNext()
    {
        // Arrange
        bool nextCalled = false;
        Func<Task> next = () => { nextCalled = true; return Task.CompletedTask; };
        var expected = new[] { new PackageRequest(...) };
        var sut = new DesiredStateReadMiddleware(
            sources: [],
            sourceTrustOptions: new SourceTrustOptions { ... },
            desiredStateAggregator: new FakeDesiredStateAggregator(new(expected, new Dictionary<string, Exception>())),
            ...);
        var ctx = new ReconciliationCycleContext { CorrelationId = "test", ... };

        // Act
        await sut.InvokeAsync(ctx, next);

        // Assert
        Assert.Equal(expected, ctx.DesiredRequests);
        Assert.True(nextCalled);
    }
}
```

### Group 2: Concrete unit tests
- `AllowlistGateTests.cs` — construct `new AllowlistGate()` + `SourceTrustOptions`; assert `AggregateException` for blocked cases
- `DesiredStateAggregatorTests.cs` — hand-rolled `FakeDesiredPackageSource`; assert `DesiredAggregateResult.SourceErrors` populated on throw
- `LockFileCoordinatorTests.cs` — write JSON to `Path.GetTempFileName()`; cleanup in `IDisposable.Dispose()`
- `PackageCleanupServiceTests.cs` (`test/Nuplane.Store.Tests/Packages/`) — construct `new CleanupPolicyEvaluator()` and `new PackageCleanupService(evaluator)`
- `DesiredSourceSnapshotCacheTests.cs` — construct with `FakeStoreRegistry`; test `SaveAsync` / `TryGetSnapshot` / `LoadSnapshotAsync`

### Group 3: Loading tests
- `PackageAssemblyLoadContextTests.cs` — ALC collectibility uses `[MethodImpl(MethodImplOptions.NoInlining)]` helper; fixture path via `typeof(FixtureMarker).Assembly.Location`

---

## Validation

```bash
dotnet build        # Must produce zero warnings
dotnet test         # Must pass all pre-existing + all new tests
```

Minimum expected new test count: 72 (36 middleware + 20 concretes + 16 loading).
