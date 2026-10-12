# Implement Temporal ingestion

> Historical planning record. The implemented design is documented in [architecture.md](architecture.md) and [operations.md](operations.md); these supersede activity, persistence, and recovery proposals below.

**Accepted implementation plan — 6 October 2026.** For Alec and Switz. Self-host Temporal on PostgreSQL, develop it alongside Hangfire, test locally, and switch ingestion over in an operator-controlled rollout. Keep this proportional to a small service whose deployments Alec controls.

Use shared activities composed by `ArtistRefreshWorkflow` and `ReimportItemWorkflow`, with artist children for batches. Start with **ten concurrent activities per provider worker and ten artists per batch**. Do not invent low request-per-second caps. PanicStream is disabled and excluded from this migration. AI/search and HA are out of scope.

[Importer contracts](importer-workflows.md) describe the seven active providers. [Queue configuration](upstream-throttling.md) gives the simple concurrency/throttling setup. [Evidence](capacity-and-evidence.md) contains the catalog measurements and Temporal limits.

## Shape

```text
Daily/thin -> RefreshBatchWorkflow
  -> ArtistRefreshWorkflow (10 active artist children)
     -> prerequisites -> discover -> import selected items
     -> eligible Archive reconciliation -> rebuild shows/years/cache

Manual item -> ReimportItemWorkflow
  -> the same fetch/apply activities -> rebuild
```

Full artist, manual item and rebuild entry points use the same active Workflow ID, `ingest/artist/{uuid}`, so an overlapping request gets the running workflow rather than starting another writer. They can use different workflow types while sharing that ID. Run providers in their current order; tracks stay inside the source import transaction.

Use small request/item manifests for selection and resumption, and record item completion with catalog writes so a retry after a lost acknowledgment is harmless. Keep large upstream bodies out of workflow history. No database ownership generations, dispatch outbox or separate admission platform is required: start Temporal directly and return its handle. A workflow's first activity creates/loads its manifest using the logical request ID.

Use Continue-As-New to bound history, initially after about 500 total activity calls at an item boundary; drain child workflows before batch rollover around 250 completed artists. These are implementation defaults, not operational approval gates. Keep IDs/cursors across rollover.

## 1. Local setup and shared extraction

Add `local-dev/temporal/docker-compose.yml` as an isolated Compose project with its own volumes: disposable application database, Redis for Hangfire comparison, Temporal PostgreSQL persistence/Visibility databases, Temporal server/UI and a fake upstream. Reuse the repo's application schema and local conventions. Pin compatible versions. Use local configuration for database addresses, Temporal namespace, queue settings and provider endpoint overrides; don't reuse production credentials or restore over the existing development database.

Create a small `RelistenIngestionWorker` host and shared DI registrations. Put workflows/activities under `RelistenApi/Services/Ingestion/` initially; avoid moving every importer into a new framework. Keep existing Hangfire entry points working while extracting discovery, item import and finalization. Replace `PerformContext` dependencies with a small progress adapter and keep mutable importer state scoped to each operation.

Suggested implementation files:

| Area | Location |
|---|---|
| Contracts, manifest access, DI | `RelistenApi/Services/Ingestion/` |
| Workflow composition and activities | `Services/Ingestion/Workflows/` and `Activities/` |
| Worker host and configuration | `RelistenIngestionWorker/` |
| Run/item receipt schema | Normal API migrations |
| Local fixtures and integration tests | `RelistenApiTests/Ingestion/` |
| Compose and local instructions | `local-dev/temporal/` |

Start with Archive's full/thin/exact-item flow. Preserve PR89's independent deletion verification and the existing UUIDs/upserts. Fetch before SQL transactions; keep source/sets/tracks/reviews/links and the completion receipt atomic. Continue through recorded item failures and report a partial run. Rebuild successful changes even when other items fail.

## 2. Compose the other active importers

Extract Phish.in, Phish.net, Setlist.fm, Phantasy Tour, Jerry Garcia and Local using their existing parser/selection rules. Keep Phish.in prerequisites, Phish.net enrichment and Local's merged directories per date. Split multi-request operations into request activities plus apply activities, allowing both workflow entry points to reuse them. Do not implement, register or add fixtures for PanicStream.

Add Archive artist discovery, show/year rebuild, Phish.in-only import and Jerry venue backfill. Keep current full/thin scheduling selectors and UTC times. Several existing targeted methods are no-ops and several providers ignore `OnlyYear`; the importer document distinguishes parity from proposed new exact-item/year capabilities. Don't silently change those behaviors while moving scheduling.

Preserve API authentication and response contracts. Existing destructive/debug actions can stay on a legacy adapter for now and be run while normal ingestion is stopped. They are not automatically retried item activities.

## 3. Test the local vertical slice

Run existing importer fixtures plus focused tests for the new execution boundaries:

- Compare old/new output for each active provider: UUIDs, selected items, source/track/setlist relationships and aggregates.
- Interrupt an activity after its SQL commit; the retry returns the receipt without duplicating effects. Restart the worker mid-import and resume unfinished items.
- Test Archive's partial listings, Present/Deleted/Unknown, thin-scope rules and existing count guard. Temporal success must not establish deletion eligibility.
- Exercise full/thin/manual/backfill composition, cancellation and Continue-As-New. Export/replay representative histories using the [.NET test tools](https://docs.temporal.io/develop/dotnet/best-practices/testing-suite).
- Check ten-concurrent worker behavior, finite retries and an optional task-queue rate using fake 429/503/timeout responses. No elaborate multi-process chaos suite is required.

Run a large saved/synthetic artist fixture to check history rollover and staging size. Measure enough to choose initial server/worker resources; do not turn this into a benchmarking project. The existing arithmetic model is a starting estimate, not a production capacity guarantee. An optional small real-provider read-only smoke test can confirm actual parsing before rollout.

## 4. Operator-controlled rollout

Once the local slice and provider parity tests pass, deploy one Temporal server, UI and worker using PostgreSQL. Reuse the existing PostgreSQL installation, with separate Temporal persistence/Visibility databases and backup coverage. Keep short history retention and basic queue/failure/storage monitoring. A restore procedure belongs in the runbook; multi-replica failover drills are not a prerequisite.

Disable Hangfire ingestion schedules and manual admission, let running imports finish or stop that worker, then enable Temporal ingestion for the active providers. No mandatory small/p99/GD cohort ladder, observation windows or provider ownership tables. Alec can run an artist manually and inspect results before enabling the regular schedules. Keep ordinary Hangfire popularity/cache jobs running.

Rollback: stop Temporal ingestion and re-enable the old Hangfire ingestion paths. Keep committed catalog changes and unfinished manifests. Don't run both ingestion writers simultaneously. Remove ingestion-specific Hangfire registrations/adapters after Alec is comfortable with the new system; unrelated Hangfire jobs remain.

## Review

The necessary mechanics are activity retries, existing domain safety, stable IDs, small manifests/receipts and history rollover. Deployment coordination is operational: Alec stops one ingestion system and starts the other. No custom limiter, global concurrency service, write-fencing scheme, multi-stage canary program, AI pipeline or PanicStream work is part of this plan.

Implementation status and departures are recorded in [GOAL.md](GOAL.md). Follow [operations.md](operations.md) for the executable commands and handoff procedure. Production deployment is a separate action.
