# ADR 0003 — Require explicit feed trust before acquisition

**Status:** Accepted on 20 October 2026 in [Nuplane #138](https://github.com/valence-works/nuplane/issues/138#issuecomment-6300027016).

## Decision

A feed is untrusted until its operator explicitly sets `Trust: Trusted`. The release that introduces this policy has no compatibility window. Explicit `Untrusted` and omitted trust use the same policy. The built-in resolver evaluates each candidate against the policy before it enumerates versions, accesses a local install, or acquires package content. This applies to remote and directory feeds and to requested roots and their dependencies. Trust is required for the actual candidate feed, including a local cache selected for a request that names a remote feed.

A refused candidate reports the feed name, package ID, and the exact `Setup:Feeds:…:Trust` configuration path when available. Normal fallback and strict resolution rules still apply as each candidate is evaluated. A refusal leaves the existing last-known-good package active.

An operator can configure an exception for an exact feed name and package ID, with a nonblank reason. Both names match case-insensitively. A reason-bearing override is logged on use. The exception applies to the named package alone, so a dependency acquired from that same feed requires trust or its own exception. Pattern-based overrides and validator-based trust levels described in the phase 2 design are outside this decision.

## Migration

Review each feed the host uses as an acquisition source. Add `"Trust": "Trusted"` to approved entries under `Nuplane:Setup:Feeds`, including the local package cache if it supplies packages. In code-first hosts, call `WithTrust(FeedTrustLevel.Trusted)` for a remote feed or set `feed.Trust = FeedTrustLevel.Trusted` in a directory feed callback. A direct `FeedDefinition` can specify `Trust = FeedTrustLevel.Trusted` in its initializer.

Elsa core hosts upgrading Nuplane must set this property for each approved remote service index and local cache. Where a feed is used for one approved package only, configure a reason-bearing exception under `Nuplane:FeedResolution:TrustOverrides`:

```json
{
  "Nuplane": {
    "FeedResolution": {
      "TrustOverrides": [
        {
          "FeedName": "internal-preview",
          "PackageId": "Acme.Plugins.Reporting",
          "Reason": "Approved for the reporting preview rollout"
        }
      ]
    }
  }
}
```

This policy controls which feed may supply a package. The package lock file still controls version and exact archive hash when enabled, and feed credentials still govern authentication.
