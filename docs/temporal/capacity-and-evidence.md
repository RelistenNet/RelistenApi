# Capacity and evidence

Research date: 6 October 2026. [Recommendation](README.md) · [Migration plan](migration-plan.md)

## What was measured

Queries ran against the documented production **read-only replica**, with `default_transaction_read_only=on`, explicit read-only transactions, and a ten-second statement timeout. No job or importer was invoked. Credentials were passed directly in the process environment and were not saved. Results are a moving catalog snapshot while separate recovery work may be in progress.

| Observation | Result |
|---|---:|
| Artists | 4,374 |
| Artist/provider mappings | 4,379 |
| Archive mappings | 4,372 |
| Artists with at least one source | 4,339 |
| Recording sources | 289,718 |
| Shows | 213,218 |
| Tracks, including orphaned rows | 5,056,966 |
| Tracks not marked orphaned | 5,004,461 |
| Source count per artist with sources: p50 / p95 / p99 | 16 / 234 / 902 |
| Grateful Dead sources / distinct shows | 18,200 / 2,081 |
| Artists matching the current active-artist date predicate | 37 |
| PostgreSQL version / replica flag | 17.10 / true |

The active predicate is `date > CURRENT_DATE - 7`, exactly as in `ShowService`; it is not a measurement of successful recent ingestion. The source percentiles exclude the 35 artists with no source rows. The 4,379 mappings comprise Archive 4,372; Phantasy Tour 2; and one each for local, Jerry Garcia, Phish.in, Phish.net, and Setlist.fm. PanicStream was registered in the inspected code but had no current mapping. Alec has excluded it from this migration; the plan leaves it disabled.

The supplied incident context reported 18,386 Archive items for Grateful Dead. That is an **upstream listing count**, not the same population as the 18,200 persisted eligible sources. The difference is not evidence of 186 missing/deleted recordings.

From September 29 through October 5, the current database contains 18–40 sources created per day, 208 total. These timestamps do not measure update traffic, all upstream additions, failed attempts, or deleted/recovered rows. They must not be substituted for the daily number of source imports. The current importer also revisits recently added sources and updated reviews/dates.

Exact query evidence:

- [Catalog schema and version](evidence/catalog-schema.txt), measured 15:31 UTC.
- [Counts/distribution SQL](evidence/catalog-measure.sql) and [results](evidence/catalog-measure.txt), starting 15:32 UTC.
- [Track/storage SQL](evidence/catalog-extra.sql) and [results](evidence/catalog-extra.txt), starting 15:33 UTC. The largest measured query took 1.23 seconds server/client-reported elapsed time.

Existing table-plus-index sizes were 1.075 GB for sources, 0.140 GB for shows, and 4.313 GB for tracks. These are application tables; they do not predict Temporal storage. Replica `pg_stat_user_tables.n_live_tup` returned zero, so those statistics were discarded; exact counts and `pg_class` estimates are explicitly distinguished in the saved results.

## Capacity model

This is an arithmetic sensitivity model for **one source activity per selected recording**, not a load test. The implementation plan splits multi-request providers into additional fetch/apply activities, so this is a lower-bound scenario for those providers. Measure calls per item and update the model during the local pilot. Archive can retain one fetch-and-apply activity per recording. Rollover thresholds now count total activity calls, not just imported items. Reproduce it with `python3 docs/temporal/capacity_model.py`; see [script](capacity_model.py) and [CSV](evidence/capacity-scenarios.csv).

Measured inputs are 4,374 artists, 37 currently active artists, and 289,718 sources. If the active set stayed constant, daily artist refresh requests would be `4,374 + 9 × 37 = 4,707`. That is an estimate: the active set changes and overlaps can skip runs. About 4,712 provider scans assumes one mapping per active artist. Some adapters use multiple HTTP requests per scan, so this is a unit count, not an observed request rate.

Let `D` be selected source items in a day. `D` is unknown. Model 500 and 5,000 as sensitivity cases. A forced full reimport uses all 289,718 persisted sources as a bounded starting population; fresh discovery can find more.

For initial arithmetic only, assume three planning/finalization activities and twenty history events per artist request, plus one import activity and six history events per selected source. Real counts include manifest pages, retries, cancellations, workflow-task scheduling, and Continue-As-New. Replace the coefficients with pilot histories.

| Scenario | Selected sources | Modeled activities | Modeled events | History at 1 KiB/event |
|---|---:|---:|---:|---:|
| Daily sensitivity: 500 | 500 | 14,621 | 97,140 | 0.099 GB/day |
| Daily sensitivity: 5,000 | 5,000 | 19,121 | 124,140 | 0.127 GB/day |
| One full reimport plus a day's coordinator overhead | 289,718 | 303,839 | 1,832,448 | 1.876 GB |

These history sizes exclude mutable state, Visibility indexes, WAL, replicas, backups, manifests, and large payloads. Seven-day retention at the two daily rates is approximately 0.70–0.89 GB of modeled history; a full backfill within that window adds approximately 1.88 GB. At 0.5–4 KiB/event, multiply those figures by 0.5–4. A trial budget of **tens of GB**, with monitored free space, is reasonable to test; it is not an allocation proven sufficient. History retention is counted after execution closes, so an open long-running workflow can exceed that planning window.

No extra hardware purchase is justified by these counts alone. First try the existing PostgreSQL installation with dedicated databases and connection limits. A single Temporal server with a **trial resource budget around 2 vCPU and 4 GiB RAM** is a starting experiment, not a sizing guarantee or deployment prescription. Measure actual resident memory and database load before selecting requests/limits. The .NET worker's CPU/memory and upstream HTTP work are separate from the server budget. Self-hosting cost is added compute/storage/backups plus time maintaining the service; the incremental cash cost on existing machines is unmeasured.

### Why rate and fanout both matter

For the simplified `provider scans + selected items` model, each counted unit uses one HTTP request. Effective throughput cannot exceed the lowest of the provider budget, worker concurrency divided by request latency, or database throughput.

| Scenario | At 1 request/s | At 5 requests/s | At 10 requests/s |
|---|---:|---:|---:|
| Daily, 500 selected | 1.45 h | 0.29 h | 0.14 h |
| Daily, 5,000 selected | 2.70 h | 0.54 h | 0.27 h |
| All persisted sources | 81.8 h | 16.4 h | 8.2 h |

These are idealized HTTP time floors, not completion forecasts or approved provider quotas. They omit parse/commit work, retries, pagination, finalization, and blocking by provider-specific limits. With ten active source activities and two-second average service time, capacity is only five items/s before other limits; at ten seconds it is one item/s. More workers cannot fix a one-request/s upstream limit.

For GD, 18,200 source activities in one history would imply about 109,200 events using the six-event assumption. Even the three activity lifecycle events alone exceed 51,200. Rolling over every 500 source activities requires about 37 artist runs for a forced full import. A daily parent with 250 artist children per run needs about 18 runs to cover 4,374 artists. Drain each bounded set before rollover; do not raise server limits to accommodate a single giant workflow.

Creating source child workflows for every source every day would start roughly 290,000 additional workflows daily even when no recording changed. It is unnecessary. Under either option A or B, discover/select first, then dispatch only selected items. Track-level workflows would multiply the work toward five million units and are outside this migration.

## Decision-affecting unknowns

The remaining useful experiment is a small local/test pilot using saved fixtures: ordinary artist, p99-sized artist, and a synthetic 18,200-item full reimport. It should measure actual history bytes/events, replay time, worker memory, PostgreSQL writes/WAL, and duration. Restart the worker once during a database commit and verify no duplicate catalog effect. Test a forced rollover after a small number of items. These results decide resource limits and safe batch sizes.

Actual daily metadata fetches, HTTP latency/retry distributions, provider quotas, and spare production database capacity were not measured. Do not generate a production full reimport just to answer those questions. Add counters during the first pilot/migration stage, then observe normal traffic.

## Code evidence

API snapshot: `8414bad39d854a5f06bac5c3975d1e144fa9efa2`; mobile: `603352fc8bf6572e33463fdcdb7a503c40a97131`; web: `360c079fefaaf41b8cf1dc8af054e800638e0eb0`; infrastructure: `6a84ce3ee0d6e23980a3653858f62b0f95a45f67`. Snapshots were exported with `git archive` into this workspace. Original checkout state and jobs were not changed. API snapshot contains PR89's verification code; this is not a statement about deployment completion.

| Ref | Source and relevant behavior |
|---|---|
| C1 | [ScheduledService.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/ScheduledService.cs): discovery line 52, daily line 68, artist overlap check line 214, thin cadence line 292. Current overlap tracking is an in-process dictionary, not a distributed guarantee. |
| C2 | [Startup.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Startup.cs): Redis Hangfire line 241; ten artist workers line 265. [Worker manifest](https://github.com/RelistenNet/relisten-flux/blob/6a84ce3ee0d6e23980a3653858f62b0f95a45f67/clusters/relisten3-k3s/apps/relistenapi.yaml): declared replicas and enabled worker. |
| C3 | [ImporterBase.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Importers/ImporterBase.cs): registered adapters line 54; provider dispatch line 120; aggregate routines line 363 onward. [ProgressExtensions.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Vendor/ProgressExtensions.cs): awaited sequential helper; Phish.net separately uses `ForEachAsync` with concurrency ten when a Hangfire context exists. |
| C4 | [ArchiveOrgImporter.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Importers/ArchiveOrgImporter.cs): selection line 130; transaction line 249; caught item errors line 266; reconciliation line 290; metadata verification line 376; source import line 424. [ImportOptions.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Importers/ImportOptions.cs): current year and seven-day rescrape. |
| C5 | [ArchiveOrgSearchClient.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Vendor/ArchiveOrg/ArchiveOrgSearchClient.cs): rows=* and response validation. [ArchiveOrgMetadataClient.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Vendor/ArchiveOrg/ArchiveOrgMetadataClient.cs): matching metadata wins; explicit errcode 104 establishes deletion; 404/invalid response is Unknown. |
| C6 | [PhishinImporter.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Importers/PhishinImporter.cs): prerequisite imports line 94; show loop line 504. Other adapters are in the same folder. |
| C7 | [SourceService.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Data/SourceService.cs): source upsert/UUID line 335. [SourceTrackService.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Data/SourceTrackService.cs): batch upsert, URL-derived UUID, and orphaning line 50 onward. |
| C8 | [SourceSetService.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Data/SourceSetService.cs): versioned source-set UUIDs. [ImporterBase.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Importers/ImporterBase.cs): display-date-derived show UUID line 523. Database unique indexes were also inspected in the replica results. |
| C9 | Mobile [show](https://github.com/RelistenNet/relisten-mobile/blob/603352fc8bf6572e33463fdcdb7a503c40a97131/relisten/realm/models/show.ts), [source](https://github.com/RelistenNet/relisten-mobile/blob/603352fc8bf6572e33463fdcdb7a503c40a97131/relisten/realm/models/source.ts), and [track](https://github.com/RelistenNet/relisten-mobile/blob/603352fc8bf6572e33463fdcdb7a503c40a97131/relisten/realm/models/source_track.ts) models use UUID primary keys. Web [RelistenAPI.ts](https://github.com/RelistenNet/relisten-web/blob/360c079fefaaf41b8cf1dc8af054e800638e0eb0/src/lib/RelistenAPI.ts) uses both v2 date routes and v3 UUID routes; [player scrobble](https://github.com/RelistenNet/relisten-web/blob/360c079fefaaf41b8cf1dc8af054e800638e0eb0/src/lib/scrobble.ts) uses track UUID. |
| C10 | [ImportController.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Controllers/Import/ImportController.cs): existing authorization, response shapes, synchronous debug and destructive delete flags. [PopularityJobs.cs](https://github.com/RelistenNet/RelistenApi/blob/8414bad39d854a5f06bac5c3975d1e144fa9efa2/RelistenApi/Services/Popularity/PopularityJobs.cs): non-ingestion Hangfire jobs that prevent removing Hangfire entirely during this migration. |

Repository AGENTS.md files were read. Available `.agents/skills` were inventoried; the mobile skills concern UI/Expo work and do not apply to this ingestion investigation. The API's `.agents` files concern its unrelated task runner. Local memory summary supplied historical context about API contracts and backup practice; current code and measured data support the claims above. Alec's [Explore/Planning templates](https://chatgpt.com/space/page_c2d80e22b8288191b4e8dbbe10ba26af) were read. Deslop was applied to the writing; grill-me was used to resolve the scope, after which the extra questionnaire was stopped.

## Temporal facts checked against official documentation

Sources were read on 6 October 2026. Exact release pins remain an implementation choice to validate in the pilot; current SDK main-branch documentation can describe features newer than a released package. The proposal uses established workflows, activities, child workflows, schedules, and Continue-As-New rather than depending on newer optional features.

| Fact | Official source |
|---|---|
| History warns at 10,240 events / 10 MB; fails at 51,200 / 50 MB. Default pending activities or children per workflow: 2,000. Payload warning/error: 256 KB / 2 MB. gRPC message ceiling: 4 MB. These are execution/request limits, not a catalog-size limit. | [Self-hosted defaults](https://docs.temporal.io/self-hosted-guide/defaults) |
| A parent should generally stay below roughly 1,000 children per run; children have their own history. Parent Close Policy matters at parent rollover. | [Child workflows](https://docs.temporal.io/child-workflows) |
| .NET exposes `StartChildWorkflowAsync`, `ExecuteChildWorkflowAsync`, and `CreateContinueAsNewException`; `ContinueAsNewSuggested` helps determine rollover. Signal/update handlers must finish before rollover. | [.NET children](https://docs.temporal.io/develop/dotnet/workflows/child-workflows), [Continue-As-New](https://docs.temporal.io/develop/dotnet/workflows/continue-as-new) |
| The SDK supports modern .NET, has hosting/DI and telemetry packages, and bundles a native runtime. Workflow code must be deterministic: HTTP/SQL, ordinary clocks/timers, `Task.Run`, and mutable importer state belong in activities. Use Temporal workflow task/timer helpers. | [SDK README](https://github.com/temporalio/sdk-dotnet/blob/main/src/Temporalio/README.md) |
| Activities have retry policies; workflows do not retry by default. Timeout and heartbeat behavior must be configured. | [Retry policies](https://docs.temporal.io/encyclopedia/retry-policies), [.NET timeouts](https://docs.temporal.io/develop/dotnet/activities/timeouts) |
| .NET supports schedule trigger/backfill/pause and configurable overlap; catalog-item selection is application logic. | [.NET schedules](https://docs.temporal.io/develop/dotnet/workflows/schedules) |
| `MaxConcurrentActivities` is per worker. `MaxTaskQueueActivitiesPerSecond` is server-controlled per task queue; polling workers must use a consistent value. | [Worker API](https://dotnet.temporal.io/api/Temporalio.Worker.TemporalWorkerOptions.html) |
| PostgreSQL can provide persistence and Visibility, using separate schemas/databases and matching SQL migrations; modern Visibility needs PostgreSQL 12+ / server 1.20+. Visibility indexes can bloat despite retention. | [PostgreSQL setup](https://docs.temporal.io/self-hosted-guide/visibility/postgresql), [Visibility compatibility](https://docs.temporal.io/self-hosted-guide/visibility) |
| Production server images require schema management; current server/chart combinations must match. Server upgrades proceed through consecutive minor versions, with required schema updates first. | [Deployment](https://docs.temporal.io/self-hosted-guide/deployment), [Upgrade procedure](https://docs.temporal.io/self-hosted-guide/upgrade-server) |

The October 6 incident figures—2,372 deletions across 963 artists—come from the task's supplied investigation context, not a fresh incident recount here. They explain why current deletion verification is a migration requirement; they are not a capacity measurement.
