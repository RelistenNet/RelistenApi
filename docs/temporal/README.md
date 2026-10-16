# Temporal ingestion

Relisten imports recordings and setlists in a separate .NET worker. Temporal records which operations have finished and resumes unfinished work after a worker restart. Each provider has a concrete workflow with typed inputs and results. Related HTTP requests and catalog writes remain together in item activities, which Temporal can retry.

The catalog is a rebuildable upstream cache. A later sync can repair occasional stale or missed metadata. Keep identity and deletion safeguards, but avoid machinery that tries to make every sync a perfect snapshot. See [cache consistency and extension choices](architecture.md#cache-consistency-and-extension-choices).

Start with [the execution graph and code map](architecture.md). Read [provider behavior](provider-behavior.md) for each provider's operations and deletion rules. Use [the production cutover](production-cutover.md) for staged live testing and [operations](operations.md) for commands and recovery, or [local setup](../../local-dev/README.md) to run development services and isolated tests.

The [implementation plan](provider-workflow-plan.md) and [contract reference](provider-workflow-contracts.md) explain the intended outcome, decisions, and verification requirements. [GOAL.md](GOAL.md) records the work's scope and standards. [Verification evidence](evidence/provider-workflow-verification.md) records the checks, reproduction steps, and remaining rollout limits. These documents are written for engineers who know the Relisten app but may be new to its importers or Temporal.

The implementation preserves catalog identities, listener API bodies, the seven active providers, and independent Archive deletion verification. Artist operations reject overlapping starts. A batch runs up to ten artists at once. Each provider workflow admits up to ten item activities concurrently, so a single artist can use the worker. Each provider worker permits ten concurrent activities by default across all its workflows. PanicStream remains excluded.

Temporal owns execution progress. PostgreSQL owns catalog rows. A Redis payload codec stores oversized serialized Temporal values for 15 days; small values remain in history. There are no application ingestion snapshots, commit receipts, or database resume commands. Item retries converge through existing unique constraints, conflict handling, and ordinary PostgreSQL transactions. They do not promise exact first-attempt mutation counts.

Production was deployed from the unmerged branches on October 16. See the [production validation record](evidence/production-cutover-2026-10-16.md) for tested images, provider results, fixes, and operating state. Earlier workflow protocols and the draft migration were replaced directly before deployment. AI processing and search indexing remain outside this work.

The [readability follow-up](readability-follow-up.md) records the full-branch review, code-reading acceptance criteria, and copy-ready prompt-template changes.

## Earlier research

[Capacity evidence](capacity-and-evidence.md) contains historical October 6 measurements. [The API proposal](api-proposal.md) records earlier alternatives; the plan and contracts supersede it where they differ. Older migration and implementation proposals are research history, not current setup instructions.
