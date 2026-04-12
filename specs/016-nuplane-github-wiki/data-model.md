# Data Model — Nuplane GitHub Wiki

## 1. WikiPage

**Purpose**: Represents one first-scope GitHub wiki page authored from the repository.

### Fields
- **PageId** (`string`): Stable internal identifier for planning and review (for example `home`, `overview`, `getting-started`).
- **Title** (`string`): Reader-facing page title.
- **PrimaryAudience** (`enum`): `Evaluator`, `Integrator`, or `Contributor`.
- **PrimaryPurpose** (`string`): The single main reason the page exists in the information architecture.
- **RequiredTopics** (`list<string>`): Topics that must appear on the page to satisfy its contract.
- **StabilityScope** (`list<StabilityLabel>`): Labels used to mark optional, phase-based, or evolving areas discussed on the page.
- **CanonicalSourceReferences** (`list<SourceReference>`): Repository materials the page summarizes or points to.
- **OutboundLinks** (`list<NavigationLink>`): Reader routes to the next relevant pages or source documents.
- **InWikiOnly** (`bool`): Whether the content is fully owned by the wiki or intentionally points to repo docs for depth.

### Validation Rules
- Every required baseline page must map to exactly one `WikiPage`.
- Each `WikiPage` must have exactly one `PrimaryPurpose`.
- Each page must target at least one audience and one required topic.
- Pages that mention optional or staged capabilities must include at least one `StabilityLabel`.
- Each page must include at least one outbound link unless it is the terminal glossary/reference page.

### Relationships
- A `WikiPage` can support multiple `AudiencePath` journeys.
- A `WikiPage` can cite many `SourceReference` entries.
- A `WikiPage` can contain many `NavigationLink` entries.

## 2. AudiencePath

**Purpose**: Represents a reader journey through the wiki.

### Fields
- **AudienceType** (`enum`): `Evaluator`, `Integrator`, `Contributor`.
- **EntryPageId** (`string`): The page where this audience starts.
- **RequiredPageIds** (`list<string>`): Pages that must be traversable for the audience to complete the core journey.
- **CompletionQuestions** (`list<string>`): The questions the audience should be able to answer after following the path.
- **ExternalReferences** (`list<SourceReference>`): Repository references needed after the wiki path reaches its designed boundary.

### Validation Rules
- The initial scope must define exactly three audience paths: evaluator, integrator, contributor.
- Each path must begin at `home` and terminate without requiring an undefined future wiki page.
- Each path must answer at least one success-criteria question from `spec.md`.

### Relationships
- An `AudiencePath` references many `WikiPage` instances.
- An `AudiencePath` can depend on many `SourceReference` entries after the wiki boundary.

## 3. StabilityLabel

**Purpose**: Marks how a capability should be interpreted by the reader.

### Fields
- **LabelType** (`enum`): `Core`, `Optional Module`, `Phase-Based`, `Recently Changed`, `Evolving`.
- **Definition** (`string`): Human-readable meaning of the label.
- **UsageRule** (`string`): When the label must appear in wiki content.

### Validation Rules
- Baseline capabilities described as core behavior use `Core` or no special warning label only when ambiguity is impossible.
- Optional loading material must use `Optional Module`.
- Roadmap- or phase-scoped behavior must use `Phase-Based` when discussed outside baseline runtime context.
- Surfaces that are intentionally still changing or newly simplified must use `Recently Changed` or `Evolving` where relevant.
- Labels must be used consistently across pages that discuss the same capability.

## 4. SourceReference

**Purpose**: Captures the repository source material that anchors wiki accuracy.

### Fields
- **SourcePath** (`string`): Repository-relative path such as `README.md` or `samples/Nuplane.Sample.AspNetCore/Program.cs`.
- **TopicArea** (`string`): The concept or workflow supported by the source.
- **ReferenceType** (`enum`): `Canonical Summary Source`, `Validation Source`, `Sample Source`, `Further Reading`.
- **WhyReferenced** (`string`): Why the wiki links to or summarizes this source.

### Validation Rules
- Every baseline page must reference at least one repository source.
- Any hands-on or validation guidance in the wiki must point to a concrete `SourceReference`.
- Pages must not reference unpublished or external-only material to complete required journeys.

## 5. NavigationLink

**Purpose**: Defines a navigational relationship between pages or from a page to a repository document.

### Fields
- **FromPageId** (`string`): Source page.
- **TargetType** (`enum`): `WikiPage`, `RepositoryDoc`, `Sample`, `SpecArtifact`.
- **TargetIdOrPath** (`string`): Target page identifier or repository path.
- **LinkPurpose** (`string`): Why the reader should follow the link next.
- **RequiredForAudience** (`list<AudienceType>`): Audiences for whom the link is part of the primary route.

### Validation Rules
- The `home` page must route to all three audience paths.
- `getting-started` must route to at least one sample or validation source.
- `architecture` and `concepts-glossary` must route to deeper repository references where the wiki intentionally stops.

## 6. ContentBoundary

**Purpose**: Documents whether a topic is fully covered in the wiki or intentionally delegated to repository docs.

### Fields
- **TopicName** (`string`): For example `sample validation`, `module architecture`, `operational runbooks`.
- **OwnedBy** (`enum`): `Wiki`, `Repository Docs`, `Sample/Spec Artifacts`.
- **Reason** (`string`): Why that ownership boundary exists.
- **ReaderExpectation** (`string`): What the reader should expect to find before being linked elsewhere.

### Validation Rules
- Each topic that is intentionally not fully covered in the wiki must have an explicit boundary.
- Maintainer-only and runbook-style content must not be silently omitted; it must be classified as repository-owned.
- High-value onboarding topics must remain wiki-owned even when they link out for depth.

