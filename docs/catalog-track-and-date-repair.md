# Returning tracks and Phantasy Tour date repair

Returning tracks now clear `is_orphaned` during the UUID upsert. Phantasy Tour imports preserve the offset-bearing local concert date rather than truncating UTC. For example, show 5238 has local `2002-04-25T20:00:00-05:00` and UTC `2002-04-26T01:00:00Z`; it belongs on April 25.

## Existing data and import behavior

No schema migration or automatic one-off data rewrite is needed. Subsequent imports repair the affected rows through existing paths.

- A source refresh reactivates returning tracks while preserving numeric IDs and UUIDs, then rebuilds durations and show/year aggregates. Missing tracks still become orphaned under the existing reconciliation policy.
- A normal Phantasy Tour import reads all listing pages. Listings contain local dates for existing shows, so thousands of forced per-show detail fetches are unnecessary. After all pages succeed, one SQL statement corrects stored dates by artist and provider identifier. It leaves venue assignments, IDs, UUIDs and song links intact, then recalculates tour bounds and rebuilds shows and years.
- Date updates are keyed by provider identity, never by the old date. Adjacent shifted shows are corrected together before date-based show/venue joins are rebuilt. Multiple legitimate shows on one date remain supported; no blanket date subtraction or venue renaming is performed.
- HTTP errors, null listings, missing IDs/dates or conflicting dates for the same provider ID fail the import before the collected date corrections are applied. Newly discovered shows may already have been imported under the existing per-show behavior; this is not a transaction covering the entire provider import.
- The existing targeted Phantasy Tour show importer is still a no-op. Use a full **artist import**, not a show-targeted import or the all-years-and-shows rebuild endpoint, for the date backfill. A rebuild alone cannot recover the provider's local dates.

## Post-deployment backfill

These steps require separate deployment and production-execution authorization. None was run while preparing this change.

1. Deploy the fix and verify the deployed revision. In the authenticated admin/operator context, enqueue `POST /api/v2/import/war-on-drugs/twod2026-10-02.flac24?deleteOldContent=false`. Verify source **4260907** exposes 15 tracks, including **35758663** and **35764190**, with their original UUIDs. The expected current duration is **6,366 seconds** rather than 5,323. Check normal cache expiry/invalidation before attributing a stale public response to a failed repair.
2. Recheck the provider cohort with the read-only query below, then enqueue the full artist imports **one at a time**, keeping `deleteOldContent=false`:
   - `POST /api/v2/import/disco-biscuits?deleteOldContent=false`
   - `POST /api/v2/import/lotus?deleteOldContent=false`
3. Each full import also runs that artist's other configured providers. Inspect the Phantasy Tour stage specifically for successful completion, the `Corrected N Phantasy Tour setlist dates` message, and successful show/year rebuilds. No additional all-artist rebuild is required after these complete.
4. Validate Disco Biscuits setlist row **75486 / pt:5237** on **2002-04-24**, and **75485 / pt:5238** on **2002-04-25**. Show **196968839** should then use the Phantasy Tour Canal Club venue **106816** (`pt:1773`); its recording retains Archive venue **107513**, also Canal Club. Keep Louisville venue **106628** unchanged. Review April 23–27 for residual date/venue conflicts and check tour bounds. Validate Lotus against a small sample of provider local dates, then rerun the imports if necessary; unchanged dates should report zero corrections.

The read-only production cohort on October 8, 2026 was:

| Artist | Slug | Phantasy Tour band | Stored provider setlist rows |
|---|---|---:|---:|
| Disco Biscuits | `disco-biscuits` | 4 | 2,013 |
| Lotus | `lotus` | 12 | 1,721 |

These are candidate rows to reconcile, not measured wrong-date counts. Other artists do not currently have this provider configured. Recheck rather than hard-code that assumption into a bulk job:

```sql
SELECT a.id, a.slug, aus.upstream_identifier AS provider_band_id,
       (SELECT count(*) FROM setlist_shows ss
        WHERE ss.artist_id = a.id AND ss.upstream_identifier LIKE 'pt:%') AS setlist_rows
FROM artists a
JOIN artists_upstream_sources aus ON aus.artist_id = a.id
JOIN upstream_sources us ON us.id = aus.upstream_source_id
WHERE us.name = 'phantasytour.com'
ORDER BY a.slug;
```

Recent production Phantasy Tour jobs received non-JSON responses, and direct diagnostic API requests received HTTP 403. The browser could read the public listing/detail JSON, but this does not prove worker access is healthy. If the backfill encounters a provider access error, resolve that access problem and retry the same import; a failed or guarded job is not evidence of completed repair. This change does not bypass provider access controls.

## Partial inventories

This change fixes permanent invisibility after a track returns. It deliberately retains the existing rule that a track absent from an accepted nonempty inventory becomes orphaned. As a result, an incomplete transient response can still hide a track until a later successful refresh. Extending protection requires a policy for when an inventory is complete or when absence is confirmed; matching MP3 counts to FLAC counts is not a general contract across providers. A future focused change can add delayed confirmation or a provider-specific readiness signal. Do not clear all orphan flags or silently disable legitimate removals globally.

## Validation

The behavioral tests use PostgreSQL to exercise the production upsert and date-correction SQL: 15 tracks → 13 → 15 restores visibility and preserves identity; adjacent setlist corrections preserve provider identities/venues and do not affect another artist. Date parsing covers the exact rollover fixture, a non-rollover timestamp and an offset ahead of UTC. PR CI supplies a disposable PostgreSQL service.

To run locally against a dedicated loopback database named `relisten_test`:

```sh
RELISTEN_TEST_DATABASE_URL=postgresql://postgres:catalog_test@127.0.0.1:15439/relisten_test \
RELISTEN_TOUR_TEST_DATABASE_URL=postgresql://postgres:catalog_test@127.0.0.1:15439/relisten_test \
  dotnet test RelistenApiTests/RelistenApiTests.csproj
```

The PostgreSQL tests create and remove unique test schemas and never read application database configuration. They skip when the explicit test URL is absent.
