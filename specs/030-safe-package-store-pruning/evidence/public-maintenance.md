# Public manual maintenance boundary

`AddNuplanePackageStoreMaintenance` explicitly registers the optional
`INuplanePackageStoreMaintenanceOperations` surface. Ordinary `AddNuplane` and
`AddNuplaneAdmin` do not register it; existing `INuplaneAdminOperations` remains
unchanged. `MapNuplanePackageStoreMaintenance` separately maps enrollment preview,
inspection, explicit enrollment/migration/recovery and prune endpoints and returns the route group for the host to apply
its authorization policy. No maintenance timer or automatic endpoint mapping is
introduced.

Prune requests select a configured label and an optional nonnegative retention
policy. Preview is the default; omitted policy retains every install, including
when Execute is explicitly selected. Execute confirmation must match the label,
physical root, enrollment epoch and exact retention request. The core reopens
native authority and freshly plans while holding all required member ownership;
a preview never supplies deletion candidates or grants a reusable capability.

Results distinguish preview eligibility from actual attempts, physical outcomes,
partial removal and journal state. Cancellation after physical work begins
retains observed outcomes. Repetition safely recognizes prior removals. Request
DTOs reject unknown JSON properties instead of accepting arbitrary root or
candidate paths. Enrollment observations expose detached native identities,
state-slot facts and state digests as descriptive input for later explicit
cutover verification; they do not declare quiescence. `CurrentMemberIds` reports
the exact observed ledger roster separately from configured target declarations.
Migration recovery binds the observed Incomplete epoch and old/new union; the
durable marker supplies the prior Complete epoch internally.

## Qualification state

The thirteen public native/API cases cover preview preservation, missing-policy
retention, protection added after preview, confirmation refusal, actual endpoint
deletions, repeat execution, partial cancellation, opt-in routing, blank labels,
arbitrary-path rejection, a real two-member initial cutover, recovery-evidence JSON
transport, state-slot relocation/member addition/explicit retirement, and HTTP
recovery after partial migration using preview epoch/roster values, including
recovery after termination of an owned child process at a durable migration checkpoint. Their names are included in the strict
retained-loading manifest for every supported native lane.

The earlier October 12 local attempt 002 passed six cases and failed two JSON endpoint cases because
the test harness omitted `IHttpRequestBodyDetectionFeature`. The harness now
models readable request bodies. Attempt 003 stopped at compilation because the
new enrollment DTO exposed an internal state-slot type. That projection now uses
a detached public value. Neither failed attempt is product acceptance.

The October 13 integrated attempt 001 compiled and passed 12/14 selected cases;
two new test setups violated supported composition selection (configured state
location and custom serializer), so neither failure was hidden or waived.
Corrected attempt 002 passed all 14 selected cases without skips: twelve public
maintenance/enrollment cases and both actual native-library lifetime scenarios.
The interrupted-migration fixture injects a core serializer fault to establish
partial durable state; public recovery uses the supported built-in composition.
Raw TRX is retained under program artifacts `resume-2026-10-13/integrated-local-attempt-002`.

Independent source review found the prior-versus-current recovery epoch mismatch;
the root correction and public regression now use the observed current epoch and
refuse the stale prior value. Native cleanup lifetime review added an explicit
no-retention contract and direct-alias guard. Fresh final-head hosted qualification
on every supported native lane, package publication and downstream adoption remain
required; these local results alone do not complete Spec 030 or authorize a release.
