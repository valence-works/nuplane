# Quickstart: Tolerate Facade Packages

## Validate Targeted Loading Behavior

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
```

Expected result:
- Loading tests pass.
- Graph regression tests include a loadable package plus a `Microsoft.Data.Sqlite`-shaped no-assembly dependency.
- Host-integrated tests verify the skipped dependency does not create a catalog entry.

## Validate Whole Solution

```bash
dotnet restore nuplane.sln
dotnet test nuplane.sln --no-restore
```

Expected result:
- All solution tests pass.
