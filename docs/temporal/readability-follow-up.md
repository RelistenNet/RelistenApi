# Readability follow-up and prompt changes

The implementation at `f67d09d` satisfied many behavioral contracts but failed the agreed source-reading standard. This follow-up reviews the full branch against `d99b56f` and fixes both correctness findings and code organization. No deployment is part of this work.

## What went wrong

The earlier conversation and [readability recommendation](readability-review.md) explicitly called for named workflow phases, braces, conceptual blank lines, and a human reading pass after formatting. The [prompt templates](https://chatgpt.com/space/page_c2d80e22b8288191b4e8dbbe10ba26af) already require readable code, appropriate comments, and reader context. The user did not omit the requirement.

The implementation replaced the universal importer with provider-specific files but left each provider's execution, pagination, checkpoint state, and result accounting packed into one `RunAsync`. The review emphasized behavioral tests and formatter output. It did not require the reviewer to explain the execution directly from the final source. Claims that simplification was complete were not supported by that reading check.

The fix uses ordinary named methods within concrete workflows. It keeps Temporal calls visible in the methods that schedule them and keeps explicit continuation state. It adds blank lines between conceptual steps, braces, and comments explaining domain policy and durable-state constraints. It does not introduce an orchestration framework or a child workflow for each item.

## Recommended template edits

These are copy-ready edits to the current Page. The Page itself has not been changed.

### Planning: add a code-reading acceptance criterion

Insert after the paragraph that organizes implementation steps:

```text
Define how the main flow should read in source code for an engineer who knows the product but may be new to this subsystem. Show one representative entrypoint or short code sketch with named domain operations in execution order. State where iteration, persistence, retry/cancellation, and state transitions belong. Make source readability an acceptance criterion, distinct from behavioral tests and formatter checks. Carry these requirements into implementation tasks and review instructions.
```

### Implementation: replace the general readability paragraph

```text
Treat source readability as part of the outcome. Write for an engineer who knows the product but may be new to this subsystem. A reader should see the main execution sequence from its entrypoint without mentally executing pagination, retry, persistence, and bookkeeping code mixed into one function.

Keep each method at one level of detail. Use named methods for coherent domain phases, including methods with only one caller when they make responsibilities or invariants clear. Keep the actual work easy to find. Avoid arbitrary wrappers, excessive parameter plumbing, and generic frameworks that hide the sequence.

Use braces for control-flow bodies. Separate setup, guards, domain operations, state transitions, and returns with blank lines where they form distinct thoughts. After formatting, inspect the source as a reader; formatter success does not prove that code is understandable.

Document public interfaces and give files enough context to explain their responsibilities. Add local comments for non-obvious ownership, invariants, failure/recovery behavior, and trade-offs. Do not make comments compensate for mixed responsibilities or repeat obvious syntax. Apply single-responsibility and DRY principles where they improve understanding. Prefer clarity over minimum line count.
```

### Implementation: add a completion review

Append after the testing and writing requirements:

```text
Establish one representative flow before repeating its structure across components. Review its readability, then use it as the reference for the remaining work. Include the same requirements in delegated tasks.

Before completion, have a fresh reviewer inspect the final formatted source separately for correctness and readability. Ask them to explain the execution order, data ownership, state transitions, and failure/recovery path from the entrypoints. A formatter result, method-count metric, or passing test suite does not satisfy this review. Fix places where the reader must reconstruct the design from a long function, scattered state, or vague helper names. Report concrete examples of the resulting structure and any remaining concerns.
```

These additions make the existing intent inspectable. They do not guarantee good judgment. The agent must still enforce them. Avoid rigid line-count limits, comment quotas, and tests that search source text for formatting; those can reward worse code.

## Repository enforcement

[AGENTS.md](../../AGENTS.md) now preserves the same acceptance criteria for future changes. The scoped `.editorconfig` brace rule is now an error, and the API and ingestion worker enable style analysis during builds. This was verified against an existing unbraced condition: the build failed with IDE0011 before the condition was fixed. CSharpier continues to control mechanical formatting. The source-reading review is still required because those tools cannot judge domain boundaries, conceptual spacing, or useful comments.

## Review and verification

An independent reviewer found two correctness defects, and a second reviewer validated both:

- Archive discovery moved selection outside the old per-document failure boundary. An existing recording with review counts but no optional freshness timestamps could throw before any selected item ran. Shared selection now conservatively fetches that recording's metadata. Regression cases exercise Hangfire and Temporal with an incomplete existing document and a second valid recording.
- Batch membership used primary data while admission and activities resolved artists and mappings on the replica. Replication lag could reject a selected artist or silently omit a provider. `ArtistService.FindArtistForImport` now routes both queries to the primary. Temporal HTTP/admin/CLI resolution and activity contexts use it; listener and Hangfire lookups keep their existing behavior. Tests simulate a lagging replica with local shadow views for UUID, slug, and numeric requests.

A proposed expiration-drain concern was rejected: children retain the same deadline, and item transactions enforce that deadline before commit. The contract requires draining before continuation and on cancellation; no separate expiration defect was established.

A fresh source reader traced Archive, Phish.in, Setlist.fm, Jerry repair, the artist coordinator, batch rollover/cancellation, collection discovery, and the shared transaction/codec paths without relying on this document. The reviewer found a misleading Phish.in comment implying provider-only requests also run Phish.net; that comment was corrected. No blocking readability finding remained in the reviewed paths.

Integrated verification passed on October 12, 2026:

- `dotnet build RelistenApi.sln --nologo` passed.
- `RELISTEN_LOCAL_INGESTION_TESTS=1 dotnet test RelistenApiTests/RelistenApiTests.csproj --no-build --nologo` passed 250 tests with zero failures. The 24 skipped tour tests require the separate `RELISTEN_TOUR_TEST_DATABASE_URL` fixture. The run used the dedicated local Postgres, Redis, and Temporal stack.
- The Release worker publish passed. Its command interface rejected conflicting import/batch flags, combined migration/schedule registration, and a provider without an operation before admission.
- CSharpier checked 363 files. Whitespace and local documentation-link checks passed.

The existing `OpenTelemetry.Resources.Host` 1.15.1-beta.1 vulnerability warning remains. No production verification or deployment occurred. The fixes are in `e525870`; workflow organization and build enforcement are in `3c8ff64`. The earlier 245-test result remains historical evidence for the implementation before this review.
