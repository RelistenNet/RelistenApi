# Migration plan

The [concrete implementation plan](implementation-plan.md) replaces the initial migration outline. It covers local setup, all importer compositions, queue throttling, verification gates, provider handoff, rollback and eventual removal of ingestion-specific Hangfire paths.

Read [importer contracts](importer-workflows.md) and [task-queue throttling](upstream-throttling.md) for implementation detail. This remains planning only; no product code or production infrastructure has been changed.
