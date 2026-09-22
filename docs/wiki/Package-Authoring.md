# Package Authoring

## Purpose

This page gives package authors the Nuplane-specific metadata shape for packages that need predictable runtime loading behavior.

## Load-mode metadata

Packages that require framework/default assembly load context integration can declare that requirement once in package-root `nuplane.json`:

```json
{
  "schemaVersion": 1,
  "loading": {
    "loadMode": "HostIntegrated",
    "scope": "DependencyClosure",
    "reason": "Uses framework type resolution and runtime scheduler integration."
  }
}
```

Place the file at the root of the NuGet package next to the package contents. Nuplane v1 loading metadata is not discovered from `build/`, `contentFiles/`, or nuspec metadata.

Allowed values:

- `schemaVersion`: `1` or `2`
- `loading.loadMode`: `HostIntegrated` or `Collectible`
- `loading.scope`: `DependencyClosure` or `PackageOnly`
- `loading.reason`: optional bounded human-readable explanation

`HostIntegrated` is a requirement. With `DependencyClosure`, Nuplane promotes the loadable graph containing the declaring package so framework services, by-name assembly resolution, provider metadata, schedulers, migrations, and similar long-lived integrations can see the package graph consistently.

`Collectible` is only a preference. It does not force an application down from a `HostIntegrated` default or another package's host-integrated requirement.

## Capability metadata

Schema 2 is a superset of schema 1: `loading` keeps the same shape and rules, and a package can additionally declare `capabilities` — named choices of root package the host must resolve for the declaring package to run. In schema 2, `loading` and `capabilities` are each optional, but at least one must be present.

```json
{
  "schemaVersion": 2,
  "loading": {
    "loadMode": "HostIntegrated",
    "scope": "DependencyClosure",
    "reason": "Registers EF contexts and migrations."
  },
  "capabilities": [
    {
      "name": "ef-provider",
      "description": "The EF Core relational provider engine this module binds at run time.",
      "options": [
        { "name": "Sqlite",     "packageId": "Microsoft.EntityFrameworkCore.Sqlite",    "version": "[10.0.10]" },
        { "name": "SqlServer",  "packageId": "Microsoft.EntityFrameworkCore.SqlServer", "version": "[10.0.10]" },
        { "name": "PostgreSql", "packageId": "Npgsql.EntityFrameworkCore.PostgreSQL",   "version": "[10.0.0]"  },
        { "name": "MySql",      "packageId": "MySql.EntityFrameworkCore",               "version": "[10.0.9]"  }
      ]
    }
  ]
}
```

Each capability says: "to run, I need exactly the package of one of these options in the host's closure as a root, and the host decides which." Declaring a capability alone changes nothing — Nuplane parses and validates it, but no root is added and no package is affected until the host selects an option. Selection, and how a selected option becomes a resolved root, is separate follow-on work.

Allowed values and rules, validated as a whole document (valid or invalid, never partial):

- `capabilities[].name` and `capabilities[].options[].name`: required, 1–64 characters of letters, digits, `.`, `_`, or `-`. Matched case-insensitively; capability names must be unique within the document, and option names unique within their capability.
- `capabilities[].options`: at least one per capability.
- `capabilities[].options[].packageId`: required, a syntactically valid NuGet package id, and never the declaring package's own id.
- `capabilities[].options[].version`: required, must parse as a NuGet version range. A floating version (e.g. `1.*`) is refused because it cannot be pinned. A bare version (e.g. `10.0.10`) is normalized to an exact single-version range (`[10.0.10]`).
- `capabilities[].description`: optional, bounded the same way as `loading.reason`.
- There is no `default` option: an unselected capability is a refusal, never a silent fallback.

## Application overrides

Application authors remain in control. `Loading:PackageLoadModes` overrides win over metadata for the same package, and Nuplane records a suppression diagnostic so operators can see that package metadata was ignored intentionally.

Use metadata for package-owned requirements that every host would otherwise need to rediscover. Use app overrides for deployment-specific policy, temporary migration, or emergency compatibility controls.

## Trust model

Nuplane reads `nuplane.json` only after the package has already been resolved and installed through the configured package source, trust, and integrity paths. Metadata is trusted only as much as the package itself.

Metadata cannot:

- grant additional package source trust;
- bypass package validation or lock-file policy;
- mutate package/store state during selection;
- define host activation semantics;
- sandbox untrusted package code;
- name a feed. A capability option names a package id and version range only; which feed supplies it is always a host fact, never something metadata can assert.

Invalid, unsupported, unreadable, or oversized metadata is ignored for selection and reported through load-mode diagnostics.

## Related pages

- [Usage Guide](Usage-Guide.md)
- [Concepts and Glossary](Concepts-and-Glossary.md)
