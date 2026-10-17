# Verify and operate Taper’s Section intake

Use this guide with the [release plan](README.md) and [contracts](contracts.md). The commands below are implemented. New schedules start paused; deployment and approval of live artists remain operator work.

## Current local evidence and extensions

The [Gov’t Mule verification](../2026-10-17-archive-collections/govt-mule-smoke.md)
records a complete isolated artist import, running v3 responses and repeat identities
under the original and current interpretation versions.
The [current roadmap](../2026-10-17-archive-collections/README.md) links collection
service/workflow and actual HTTP checks, plus the completed
[bounded Jerry proof](../2026-10-17-archive-collections/jerry-projects-smoke.md).
These checks preserve the earlier Pavement release evidence.

The [collection contract](../2026-10-17-archive-collections/contracts.md) defines
implemented membership, exact native-source reuse, configured discovery and read APIs.
The [performer contract](../2026-10-17-archive-collections/performer-contract.md) defines
explicit item-level reviews through the existing artist writer. Ordinary reviewed
creator matching and bulk artist approval remain the default. The
[existing-artist publication contract](../2026-10-17-archive-collections/existing-artist-publication.md)
adds explicit binding to a reviewed catalog identity without taking ownership of native recordings.
The [show contract](../2026-10-17-show-performances/contracts.md) defines distinct performances,
stable show UUIDs and publication under the date default when same-day coverage is unclear.
The [Phish proof](../2026-10-17-show-performances/phish-smoke.md),
[Jerry proof](../2026-10-17-show-performances/jerry-smoke.md) and
[sample runbook](../2026-10-17-show-performances/live-samples/README.md)
record the newer local verification. Read their stated completion and scope before using their results.

Local evidence does not replace the production pilot, collection-wide terminal
pagination or production backup checks below. `XX` partial dates and operator-only
date notes retain their existing contracts.

## Review artists in PostgreSQL

Use the read-only `normalization.archive_artist_review` view over the three intake tables. Return candidate ID, proposed name/slug, approval, mapped identity, current identity status, matched existing artists, item count, representative item IDs/provider URLs, observed creator labels, date range and counts by eligibility reason. Include enough retained title/description context to recognize a tribute or mixed bill. Render no provider HTML as executable content.

An operator reviews that view, checks the linked evidence and sets the approval flag for explicit candidate IDs. For example:

```sql
BEGIN;
UPDATE normalization.archive_artist_candidates
SET approved = true, updated_at = now()
WHERE candidate_id = ANY(ARRAY['REVIEWED-CANDIDATE-UUID']::uuid[])
  AND identity_status = 'pending'
RETURNING candidate_id, proposed_name, approved;
COMMIT;
```

The placeholder must be replaced with reviewed IDs. Bulk approval does not mean approval of every unmatched name. Inspect the returned rows; a row excluded by current status needs review. The worker rechecks current matches before creation, so approval cannot force an existing artist into the new cohort. No per-item approval file or review UI is required.

For an existing artist, use the reviewed binding operation in the existing-artist contract before enabling publication. A bulk approval flag alone cannot bind a candidate to a native artist. Do not set `is_existing_artist`, mapped UUIDs or published source references to force a match. A bound existing artist keeps its native flags and provider mappings. Exact native sources remain with their importer; only missing recordings committed by intake acquire intake ownership.

To pause a candidate, set its `approved=false`. To resume an already mapped cohort member, set it true again using its explicit candidate ID. Retained membership and recordings remain. Do not edit mapped UUID, creation slug, numeric artist identity or published item ownership directly. Review labels can be extended while the candidate is paused, inside a row-locking transaction, after checking label collisions and the supporting items.

## Acceptance cases

Test through the typed service, workflow and listener API contracts. Use real disposable PostgreSQL for identity, locking and atomicity; fixture HTTP for provider behavior; and SDK-managed Temporal for retries, admission and rebuilds. Do not substitute tests of private helpers for an end-to-end assertion.

1. **First useful release.** Discover a synthetic new performer with two eligible items. Verify no artist exists before approval. Approve its candidate in SQL and run cohort refresh. Assert one artist, no fabricated Archive collection mapping, two sources, ordered playable tracks, default-list visibility and correct years/shows. Add a third item later; it imports without another approval.
2. **Existing and ambiguous identities.** A Phish match stays deferred. An unmatched label alone creates no artist. Two approved candidates with the same reviewed label produce ambiguity. Race cohort materialization against authenticated artist creation and against collection discovery; the losing path must recheck/reuse/defer without a duplicate artist. Repeated materialization returns the same UUID.
3. **Attribution and date checks.** Preserve the research’s Dear Jerry, tribute, compilation, collaboration and conflicting current metadata as negative fixtures. Verify metadata preference over an older identifier, partial-date publication and precision/selection notes separately from blocking reasons. Cover missing creator, array creators, full title-derived date, unsupported date format, single original MP3 and no VBR MP3. Assert the reason and absence of unintended source writes. Independently review fixture expectations rather than copying evaluator output into expected data.
4. **Publication freshness.** Change approval, reviewed labels or latest observation between evaluation and commit. Verify the transaction holds/skips stale work. Clear approval while an item commits; the ordering must be one complete committed import or no write. No half-written source/assignment is allowed.
5. **Stable identity and loss of acknowledgement.** Retry the same item after a committed activity loses its reply. Compare artist/source/set/track UUIDs and item publication references. Change the collection selector and rediscover the same item; reuse the same resource and source. A repeated identical observation does not create another evidence row.
6. **Partial failure and rebuild recovery.** Let one item succeed and another fail. The artist result reports partial failure and rebuilds successful work. Fail rebuild after source commit; dirty revisions remain. Restart and recover, then compare shows, years and cache results. Cancellation/expiry may leave committed work; a fresh refresh or rebuild must complete it. Commit an item, pause its candidate, cancel before rebuild and run global refresh: it must rebuild that paused artist without importing or revealing it.
7. **Conflicting updates.** Publish a valid recording, then change its creator or introduce conflicting current mutable date fields. Retain its published assignment and playback while exposing the latest observation. A supported metadata date correction, including a partial date or a change of year, updates show/year grouping while preserving source/set/track identities and invalidating cached artist responses. A later consistent observation resumes held updates. Verify status returns nonblocking date details as structured JSON. A missing mapped artist produces a repair error instead of silent recreation.
8. **Discovery pagination.** Replay the observed repeated-cursor bug, a 5,000-item continuation, decreasing totals, an empty page with a continuation, overlapping IDs, cursor expiry and a malformed terminal page. Persist a whole received page before advancing. Restart after partial database application and prove no IDs are lost. A limited run is labeled limited. A live pilot must establish terminal-page behavior before scheduled full traversal is enabled.
9. **Execution-state loss.** Remove only the test’s Temporal/payload state, then start new workflows from retained PostgreSQL state. Approval, membership, evidence, published IDs and dirty rebuilds survive. Restore the test database backup with the intake schema and repeat the recovery.
10. **Ordinary ingestion compatibility.** Existing targeted Archive membership rejection still works. An artist with intake sources later receives a real collection mapping; ordinary refresh excludes intake-owned sources from deletion accounting while preserving existing guards for other sources. Ordinary full, filtered and targeted imports must return `owned_by_taperssection` for those items, including when the candidate is paused or upstream dates conflict; they must not bypass intake checks or change the published assignment. Existing provider test suites pass. A source update cannot silently change cohort artist UUID or create a second artist for a cohort collection label.

Verify listener behavior through `GET /api/v2/artists`, `GET /api/v3/artists`, `GET /api/v2/artists/{artist}/shows/{date}`, `GET /api/v3/shows/{showUuid}`, `GET /api/v3/artists/{artistUuid}/normalized` and `POST /api/v3/catalog/resolve`. Use the actual existing catalog-resolve request contract. Assert sources, ordered tracks, current URLs, UUIDs and availability; do not merely assert a Temporal execution completed. Test V2 numeric IDs and V3 UUID payloads with existing serialization expectations. Date ambiguity notes remain internal; performance fields and the additive date route follow the current show contract.

Local commands, from the repository root:

```sh
./start-local-databases.sh
./test-local.sh --filter 'FullyQualifiedName~TapersSection'
./test-local.sh --filter 'FullyQualifiedName~ArchiveOrg|FullyQualifiedName~ImportAdmission|FullyQualifiedName~IngestionPersistence'
dotnet build RelistenApi.sln
dotnet csharpier check .
```

The new fixtures should use names containing `TapersSection` so the first filter is useful. Run focused cases as their behavior is implemented; broaden once after shared writer/registry changes. The local bootstrap preserves existing data. Tests use disposable databases, isolated Redis prefixes and `WorkflowEnvironment.StartLocalAsync()`. Automated tests use fixture provider responses. A manually requested live smoke must use a separate catalog, Redis instance and Temporal server, with bounded discovery and no schedules. See the [live smoke record](live-smoke.md).

Before broad approval, freeze a fresh bounded set of real items for reviewed candidate artists and include attribution/date negatives. Record the reviewer’s expected performer/date and the system’s outcome. Report wrong automatic assignments and eligible coverage separately. Synthetic regression fixtures do not establish accuracy across the live collection. A confirmed wrong assignment blocks widening the affected rule until corrected and covered by a regression case; this is an operational release check, not a claimed statistical threshold.

## Migration and deployment

Migration 13 introduced retained intake evidence and ownership. Migration 16 adds durable performance identities and recording/setlist assignments; migration 17 distinguishes reviewed existing-artist bindings from intake-created artists. The current rollout must preserve all of these records. The API and standalone worker both run migrations, but startup success alone does not establish a safe backfill or release.

### Current rollout for migrations 16 and 17

The linked proofs use isolated local catalogs. They are implementation evidence, not a production release or a production migration rehearsal.

1. Pause catalog schedules and stop new manual admissions before taking the migration snapshot. Include both Phish providers, native Archive imports, intake refresh/discovery and any Hangfire catalog writer. Wait for admitted activities to finish or stop them before changing ownership rules. Pausing only the new intake schedules is insufficient.
2. Drain incompatible Temporal executions before deploying changed workflow contracts. If histories must remain open, deploy a compatible Temporal versioning strategy first. Keep writers stopped while the schema and binaries differ.
3. Verify the backup includes catalog playback rows, the public performance key/assignment tables, and all `normalization` observations, reviews, ownership decisions, memberships and dirty revisions. A successful schema migration does not prove backup coverage. Restore a representative catalog to an isolated environment.
4. Rehearse migrations 16 and 17 and the supported rebuild on that restored catalog. Compare artist, show, source, set and track UUIDs and media references before and after. Ordinary one-performance dates must keep their UUIDs; a reviewed split must retain its chosen original UUID and move sources without duplicating recordings. Compare recorded-show, source and track counts, date browsing and collection results; account for empty retained identity rows separately.
5. Apply the rehearsed migration with production writers still stopped, then deploy matching API and worker revisions. Confirm worker dependency registration, database access and payload-store configuration independently of API readiness. Do not bind existing artists while an older worker can still interpret every mapped candidate as intake-created ownership.
6. Admit one bounded canary under the current ownership and performer rules. Inspect the typed workflow result, including held and failed items. Check actual v3 show/source/catalog references, the preserved date destination, the date-performance response, collection deduplication and sampled media. Repeat and compare identities. Unclear same-day coverage must remain playable under its default primary show with internal uncertainty retained; it must not acquire unsupported performance-specific enrichment.
7. Resume catalog writers only after the canary and recovery checks pass. Resume the intended schedules explicitly and inspect their first results before widening approval. Existing-artist bindings must preserve native flags and mappings, exact native reuse and published intake ownership.

If the rehearsal changes unexpected identities, counts or ownership, keep production writers stopped and repair the migration or assignment policy before continuing. Retain the compared snapshots and deployment revisions with the release record.

Use [the ingestion runbook](../../../ingestion/operations.md#deploy-or-update) for deployment commands and the separate API/worker host settings. Shared writer and registry changes require the existing provider regressions.

Register two new schedules, initially paused, with UTC times outside the current 06:30 discovery and 07:00 full refresh: `taperssection-discovery` at 08:00 and `taperssection-refresh` at 09:00. Use skip-on-overlap and the existing activity queue limits. These are initial operational defaults; queue and database measurements may justify changing their time or rate without changing publication policy. Schedule registration must preserve an existing paused state. PostgreSQL inspection/status remains usable while schedules are paused.

The original intake pilot below applies after the current migration and compatibility checks. It does not authorize older workers to write a split catalog.

Pilot sequence:

1. Complete the current rollout with new schedules paused. Confirm existing readiness and a native Archive fixture/regression import.
2. Run bounded discovery, review candidate rows and approve one new artist with multiple eligible recordings. Sample actual metadata and playback before approval; a research candidate name is not preapproved.
3. Start cohort refresh and wait for the typed result. Check the default artist list, normalized artist, shows, catalog references and playable media. Repeat and compare identities.
4. Discover and import another eligible item for that same artist. Exercise a paused approval and a held conflict. Confirm status distinguishes source commit from successful rebuild.
5. Complete pagination terminal verification, then enable the discovery and refresh schedules. Inspect their first complete results before approving more artists.

## Failures, pause and rollback

Item failures are counted and retained with a reason. Successful items remain committed and rebuild. Ordinary provider failure attempts the recovery rebuild; cancellation and expiry follow the existing rule and may require a fresh rebuild. Existing activity retry limits apply. When retries are exhausted, leave durable pending/error state for the next explicit or scheduled run; do not create an endless retry loop inside an activity.

Monitor discovered/observed counts, pending approvals, deferred existing matches, review reasons, published/unchanged items, exhausted failures, artist admission conflicts and dirty rebuild count/age. Log item and candidate IDs plus existing workflow/artist/provider scopes. Use current JSON logging and Sentry behavior; add no separate monitoring service. A completed coordinator with failed items must remain visibly partial.

To pause all new intake, pause both schedules and stop new manual cohort admissions. Clearing approval pauses a specific artist at its next publication transaction. Neither action cancels already committed work. Drain or cancel running workflows and wait for their activities to stop before replacing the worker. `Ingestion__Engine` alone does not stop Temporal schedules.

After a performance split, older binaries that assume one show per artist/date are unsafe catalog writers. Stop all affected admissions and writers before rollback, including both Phish providers and every Archive execution path. Record the deployed image digests and drain incompatible histories. A rollback revision must understand migrations 16 and 17, preserve performance assignments, and retain native/intake ownership guards. Otherwise leave catalog writes stopped until compatible code returns; a matching old API/worker pair does not establish compatibility with the new data.

Retain migrations 16 and 17, performance identities and assignments, collection membership, retained evidence and publication ownership. Do not collapse shows, drop assignments, rewrite existing-artist flags or transfer source ownership as an automatic rollback. Verify listener reads against the retained schema before serving a rollback revision. On compatible resumption, process pending items and dirty rebuilds from PostgreSQL and repeat the bounded public-interface checks.

Code rollback is not an import undo. A known wrong published artist or performance assignment requires a reviewed, targeted catalog repair. Pause the affected candidate, retain its evidence and identify its exact source/track/show references before repair. Do not reparent a source, delete an artist or restore the whole database as a generic rollback procedure.

## Implementation verification record

The implementation was reviewed independently for correctness and readability. The review led to fixes for retained label normalization, review-only refreshes, catalog update timestamps and evidence retention before legacy parsing. Publication now reads as explicit validation and commit phases. Outcome counters use successful activity results; database status supplies the retained state for failed attempts.

Local verification covers metadata and pagination rules, authenticated registry collisions, artist admission, paused schedules, initial publication and later arrivals, listener catalog resolution, normalized artist cache invalidation, stable source/track identities, held updates, transaction rollback, partial failures and paused rebuild recovery. The existing Archive suite also checks lost acknowledgements, native deletion guards, targeted membership and compatibility of the extracted writer.

A fresh disposable database was migrated through the worker's actual `--migrate` entry point. A full PostgreSQL dump and restore preserved approval, evidence, published references and dirty revisions. The status command read the restored state with Temporal pointed at an unavailable endpoint. These checks establish the application's restore contract; production backup configuration must still be checked by the operator.

A subsequent [full Pavement smoke](full-artist-smoke.md) imported its complete 107-recording indexed inventory, verified every published show through v3 HTTP, and verified partial-date publication plus metadata preference over immutable identifiers. Date precision and disagreements remain in internal notes; they do not alone require review.

The production pilot, complete live pagination terminal behavior and production backup coverage remain operational release checks. No production artist has been approved and no production schedule has been enabled. A later [live smoke](live-smoke.md) imported two real recordings into a separate local catalog and verified them through actual v3 HTTP requests.

Verification completed October 16, 2026:

- Full API suite: 370 passed, 24 skipped, 394 total.
- Final conflict and persisted-interpretation checks: 4 passed, including changed creator/date assignments and recovery to the reviewed assignment.
- `dotnet build RelistenApi.sln --no-restore --verbosity quiet`: succeeded with zero errors. Existing dependency advisory warnings remain.
- `dotnet csharpier check .`: 401 files checked; whitespace and local documentation links passed.
- Actual worker migration, bulk review aggregates, status with Temporal unavailable, invalid CLI combinations and full PostgreSQL dump/restore checks passed on disposable databases.

The status counter definition and unconditional ancillary metadata refresh are the implementation refinements described in the [decision record](README.md#implementation-decisions). Fresh correctness and readability reviews found no remaining catalog ownership or publication issue after the corrections. Production checks in the pilot sequence remain pending. The live smoke also found and corrected identifier-only discovery returning unrelated upstream results; its evidence and repeat-import checks are recorded separately.
