# Move Relisten ingestion to Temporal

**Implementation · 6 October 2026 · for Alec and Switz**

Use self-hosted Temporal with PostgreSQL. Compose shared activities through `ArtistRefreshWorkflow` and `ReimportItemWorkflow`. Use native worker concurrency and optional provider task-queue rate limits; do not build a distributed limiter. The daily refresh starts a bounded number of artist child workflows. Keep the existing importer rules, database IDs, API responses, and schedules.

An Archive item is a recording source, not a Relisten show. A show groups sources for an artist and display date. Start with **source activities**, because the current operation is a fetch followed by a database transaction. A source child workflow becomes useful when that operation actually needs several independently recoverable steps. Future additions do not need to be built now.

The measured catalog contains **4,374 artists, 289,718 sources, 213,218 shows, and 5,056,966 tracks**. Grateful Dead has 18,200 sources across 2,081 shows. These numbers justify bounded loops and history rollover; they do not establish a hardware capacity guarantee.

Use ten-concurrent provider workers and ten artists per normal batch; leave arbitrary low request-per-second caps out. Start with one Temporal server instance, its Web UI, and a .NET worker. Use separate `temporal` and `temporal_visibility` databases on PostgreSQL. Reusing the existing PostgreSQL installation is a reasonable first choice with ordinary monitoring. Ingestion downtime is acceptable. There is no need to add high availability or another search database for this migration.

Read the detail in this order:

1. [Operator instructions](operations.md) and [local setup](../../local-dev/temporal/README.md): run, verify, switch engines and recover.
2. [Concrete implementation plan](implementation-plan.md): local setup, implementation stages, verification gates and later rollout.
3. [Importer contracts](importer-workflows.md): the seven active providers, current behavior and proposed activity composition.
4. [Task-queue throttling](upstream-throttling.md): queues, rates, retries and operator-controlled Hangfire handoff.
5. [Decision memo](decision.md): boundaries, existing behavior, retries, manual runs, and PostgreSQL operations.
6. [Capacity and evidence](capacity-and-evidence.md): measured counts, reproducible assumptions, code references, and official Temporal sources.


## Scope and status

Alec confirmed **self-hosting only**, acceptable ingestion downtime, and **migration of existing ingestion first**. AI normalization, embeddings, search indexing, identity redesign, and a new review platform are outside this plan. They were examples of future uses for finer ingestion units.

The agreed plan is implemented on `codex/temporal-ingestion-plan`, with a separate worker, shared provider activities, API handoff and local integration tests. PanicStream is disabled and excluded. [The persistent goal](GOAL.md) records scope, standards and verification. Production deployment and the PR89/reindex work remain separate. The catalog measurements below are historical observations, not a deployment capacity guarantee.

## Workflow boundaries

Use artist workflows and shared activities, with a separate manual item entry point. Per-item child workflows remain an option if independent control becomes a concrete requirement. The plan preserves existing full/thin behavior; it identifies unsupported targeted/year operations without silently adding them. Temporal is added alongside Hangfire and tested locally before any production deployment.
