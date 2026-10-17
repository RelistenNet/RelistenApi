# Verify and operate Taper’s Section intake

Use this guide with the [release plan](README.md) and [contracts](contracts.md). The commands below are implemented. New schedules start paused; deployment and approval of live artists remain operator work.

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

To pause a candidate, set its `approved=false`. To resume an already mapped cohort member, set it true again using its explicit candidate ID. Retained membership and recordings remain. Do not edit mapped UUID, creation slug, numeric artist identity or published item ownership directly. Review labels can be extended while the candidate is paused, inside a row-locking transaction, after checking label collisions and the supporting items.

## Acceptance cases

Test through the typed service, workflow and listener API contracts. Use real disposable PostgreSQL for identity, locking and atomicity; fixture HTTP for provider behavior; and SDK-managed Temporal for retries, admission and rebuilds. Do not substitute tests of private helpers for an end-to-end assertion.

1. **First useful release.** Discover a synthetic new performer with two eligible items. Verify no artist exists before approval. Approve its candidate in SQL and run cohort refresh. Assert one artist, no fabricated Archive collection mapping, two sources, ordered playable tracks, default-list visibility and correct years/shows. Add a third item later; it imports without another approval.
2. **Existing and ambiguous identities.** A Phish match stays deferred. An unmatched label alone creates no artist. Two approved candidates with the same reviewed label produce ambiguity. Race cohort materialization against authenticated artist creation and against collection discovery; the losing path must recheck/reuse/defer without a duplicate artist. Repeated materialization returns the same UUID.
3. **Attribution and date checks.** Preserve the research’s Dear Jerry, tribute, compilation, collaboration and July 20/21 conflict as negative fixtures. Cover missing creator, array creators, full title-derived date, unsupported date format, single original MP3 and no VBR MP3. Assert the reason and absence of unintended source writes. Independently review fixture expectations rather than copying evaluator output into expected data.
4. **Publication freshness.** Change approval, reviewed labels or latest observation between evaluation and commit. Verify the transaction holds/skips stale work. Clear approval while an item commits; the ordering must be one complete committed import or no write. No half-written source/assignment is allowed.
5. **Stable identity and loss of acknowledgement.** Retry the same item after a committed activity loses its reply. Compare artist/source/set/track UUIDs and item publication references. Change the collection selector and rediscover the same item; reuse the same resource and source. A repeated identical observation does not create another evidence row.
6. **Partial failure and rebuild recovery.** Let one item succeed and another fail. The artist result reports partial failure and rebuilds successful work. Fail rebuild after source commit; dirty revisions remain. Restart and recover, then compare shows, years and cache results. Cancellation/expiry may leave committed work; a fresh refresh or rebuild must complete it. Commit an item, pause its candidate, cancel before rebuild and run global refresh: it must rebuild that paused artist without importing or revealing it.
7. **Conflicting updates.** Publish a valid recording, then change creator or date evidence. Retain its published assignment and playback while exposing the conflicting latest observation. A later clear observation with the same assignment can resume updates. A missing mapped artist produces a repair error instead of silent recreation.
8. **Discovery pagination.** Replay the observed repeated-cursor bug, a 5,000-item continuation, decreasing totals, an empty page with a continuation, overlapping IDs, cursor expiry and a malformed terminal page. Persist a whole received page before advancing. Restart after partial database application and prove no IDs are lost. A limited run is labeled limited. A live pilot must establish terminal-page behavior before scheduled full traversal is enabled.
9. **Execution-state loss.** Remove only the test’s Temporal/payload state, then start new workflows from retained PostgreSQL state. Approval, membership, evidence, published IDs and dirty rebuilds survive. Restore the test database backup with the intake schema and repeat the recovery.
10. **Ordinary ingestion compatibility.** Existing targeted Archive membership rejection still works. An artist with intake sources later receives a real collection mapping; ordinary refresh excludes intake-owned sources from deletion accounting while preserving existing guards for other sources. Ordinary full, filtered and targeted imports must return `owned_by_taperssection` for those items, including when the candidate is paused or upstream dates conflict; they must not bypass intake checks or change the published assignment. Existing provider test suites pass. A source update cannot silently change cohort artist UUID or create a second artist for a cohort collection label.

Verify listener behavior through `GET /api/v2/artists`, `GET /api/v3/artists`, `GET /api/v2/artists/{artist}/shows/{date}`, `GET /api/v3/shows/{showUuid}`, `GET /api/v3/artists/{artistUuid}/normalized` and `POST /api/v3/catalog/resolve`. Use the actual existing catalog-resolve request contract. Assert sources, ordered tracks, current URLs, UUIDs and availability; do not merely assert a Temporal execution completed. Test V2 numeric IDs and V3 UUID payloads with existing serialization expectations. No new public response fields are needed.

Local commands, from the repository root:

```sh
./start-local-databases.sh
./test-local.sh --filter 'FullyQualifiedName~TapersSection'
./test-local.sh --filter 'FullyQualifiedName~ArchiveOrg|FullyQualifiedName~ImportAdmission|FullyQualifiedName~IngestionPersistence'
dotnet build RelistenApi.sln
dotnet csharpier check .
```

The new fixtures should use names containing `TapersSection` so the first filter is useful. Run focused cases as their behavior is implemented; broaden once after shared writer/registry changes. The local bootstrap preserves existing data. Tests use disposable databases, isolated Redis prefixes and `WorkflowEnvironment.StartLocalAsync()`. Do not import real provider catalogs to prove local behavior.

Before broad approval, freeze a fresh bounded set of real items for reviewed candidate artists and include attribution/date negatives. Record the reviewer’s expected performer/date and the system’s outcome. Report wrong automatic assignments and eligible coverage separately. Synthetic regression fixtures do not establish accuracy across the live collection. A confirmed wrong assignment blocks widening the affected rule until corrected and covered by a regression case; this is an operational release check, not a claimed statistical threshold.

## Migration and deployment

Migration 13 creates the intake schema. Current API and standalone worker both run migrations; test the additive migration through both entry points and ensure the operator deploys schema before enabling new work. Create the three tables, their local foreign keys, unique keys and pending/approved lookup indexes. There is no catalog-wide metadata backfill. Existing data remains readable by the previous application version.

Verify backups include the new `normalization` schema; schema creation alone does not prove backup coverage. Restore a test backup and confirm retained approval and item identity. Leave the migration’s destructive down path unsupported once evidence exists. A code rollback retains the tables.

Deploy matching API and worker images according to [the ingestion runbook](../../../ingestion/operations.md#deploy-or-update). Shared writer and registry changes require existing ingestion tests even though the new workflows are separate. Pause affected schedules and stop manual admission; drain incompatible workflow histories before changing workflow contracts. If histories must remain open, use Temporal versioning instead. Worker dependency registration and Sentry settings are separate from the API host.

Register two new schedules, initially paused, with UTC times outside the current 06:30 discovery and 07:00 full refresh: `taperssection-discovery` at 08:00 and `taperssection-refresh` at 09:00. Use skip-on-overlap and the existing activity queue limits. These are initial operational defaults; queue and database measurements may justify changing their time or rate without changing publication policy. Schedule registration must preserve an existing paused state. PostgreSQL inspection/status remains usable while schedules are paused.

Pilot sequence:

1. Apply the migration and deploy with new schedules paused. Confirm existing readiness and a native Archive fixture/regression import.
2. Run bounded discovery, review candidate rows and approve one new artist with multiple eligible recordings. Sample actual metadata and playback before approval; a research candidate name is not preapproved.
3. Start cohort refresh and wait for the typed result. Check the default artist list, normalized artist, shows, catalog references and playable media. Repeat and compare identities.
4. Discover and import another eligible item for that same artist. Exercise a paused approval and a held conflict. Confirm status distinguishes source commit from successful rebuild.
5. Complete pagination terminal verification, then enable the discovery and refresh schedules. Inspect their first complete results before approving more artists.

## Failures, pause and rollback

Item failures are counted and retained with a reason. Successful items remain committed and rebuild. Ordinary provider failure attempts the recovery rebuild; cancellation and expiry follow the existing rule and may require a fresh rebuild. Existing activity retry limits apply. When retries are exhausted, leave durable pending/error state for the next explicit or scheduled run; do not create an endless retry loop inside an activity.

Monitor discovered/observed counts, pending approvals, deferred existing matches, review reasons, published/unchanged items, exhausted failures, artist admission conflicts and dirty rebuild count/age. Log item and candidate IDs plus existing workflow/artist/provider scopes. Use current JSON logging and Sentry behavior; add no separate monitoring service. A completed coordinator with failed items must remain visibly partial.

To pause all new intake, pause both schedules and stop new manual cohort admissions. Clearing approval pauses a specific artist at its next publication transaction. Neither action cancels already committed work. Drain or cancel running workflows and wait for their activities to stop before replacing the worker. `Ingestion__Engine` alone does not stop Temporal schedules.

To roll back the code, first drain the new workflow types and record current image digests. A rollback image must retain the artist registry and native Archive ownership guards, or Archive ingestion must remain stopped. Older images can otherwise update or delete intake-owned recordings after a real collection is attached to a cohort artist. Pause Archive discovery and full/thin schedules, stop manual Archive starts, and keep the Hangfire Archive path stopped while using an image without those guards. Draining only the new workflow types is insufficient.

Keep the intake tables, artist membership and catalog rows when deploying previous matching API/worker images. Existing listener APIs can still read the published recordings. New arrivals and refreshes remain stopped until compatible workers return. On resumption, process pending items and dirty rebuilds from PostgreSQL.

Code rollback is not an import undo. A known wrong published ownership/date requires a reviewed, targeted catalog repair because those values participate in existing identities. Pause the affected candidate, retain its evidence and identify its exact source/track/show references before repair. Do not reparent a source, delete an artist or restore the whole database as a generic rollback procedure.

## Implementation verification record

The implementation was reviewed independently for correctness and readability. The review led to fixes for retained label normalization, review-only refreshes, catalog update timestamps and evidence retention before legacy parsing. Publication now reads as explicit validation and commit phases. Outcome counters use successful activity results; database status supplies the retained state for failed attempts.

Local verification covers metadata and pagination rules, authenticated registry collisions, artist admission, paused schedules, initial publication and later arrivals, listener catalog resolution, normalized artist cache invalidation, stable source/track identities, held updates, transaction rollback, partial failures and paused rebuild recovery. The existing Archive suite also checks lost acknowledgements, native deletion guards, targeted membership and compatibility of the extracted writer.

A fresh disposable database was migrated through the worker's actual `--migrate` entry point. A full PostgreSQL dump and restore preserved approval, evidence, published references and dirty revisions. The status command read the restored state with Temporal pointed at an unavailable endpoint. These checks establish the application's restore contract; production backup configuration must still be checked by the operator.

The production pilot, complete live pagination terminal behavior and production backup coverage remain operational release checks. No production artist has been approved, no real catalog has been imported, and no production schedule has been enabled by this implementation task.

Verification completed October 16, 2026:

- Full API suite: 370 passed, 24 skipped, 394 total.
- Final conflict and persisted-interpretation checks: 4 passed, including changed creator/date assignments and recovery to the reviewed assignment.
- `dotnet build RelistenApi.sln --no-restore --verbosity quiet`: succeeded with zero errors. Existing dependency advisory warnings remain.
- `dotnet csharpier check .`: 401 files checked; whitespace and local documentation links passed.
- Actual worker migration, bulk review aggregates, status with Temporal unavailable, invalid CLI combinations and full PostgreSQL dump/restore checks passed on disposable databases.

The status counter definition and unconditional ancillary metadata refresh are the implementation refinements described in the [decision record](README.md#implementation-decisions). Fresh correctness and readability reviews found no remaining catalog ownership or publication issue after the corrections. Production checks in the pilot sequence remain pending.
