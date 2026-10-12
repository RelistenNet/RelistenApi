# Temporal ingestion

The next change is specified by the [provider workflow implementation plan](provider-workflow-plan.md) and its [contract reference](provider-workflow-contracts.md). They cover concrete provider workflows, shared item activities, typed commands/results, Redis payload offloading, and receipt-free catalog retries. The [earlier proposal](api-proposal.md) records design alternatives; the new plan supersedes it where they differ.

The code and guides below still describe the current implementation. PostgreSQL snapshots and receipts described here are being replaced by the planned refactor; they have not yet been removed.

Relisten ingestion runs in a separate .NET worker. Artist workflows compose provider preparation, paginated item imports, reconciliation, and a final catalog rebuild. A manual Archive reimport uses the same item activity without enumerating the artist or reconciling unrelated sources.

Start with [the execution graph and code map](architecture.md). [Provider behavior and logging](provider-behavior.md) traces the shared Hangfire/Temporal methods and their intentional differences. Use [the operator instructions](operations.md) to configure, run, and recover imports, and [the local setup](../../local-dev/temporal/README.md) for integration tests. [The goal](GOAL.md) records the intended outcome and development standards.

Related HTTP GETs run inside an item activity. Ten concurrent activities per provider and ten active artist children per batch provide the normal backpressure. No custom limiter or one-request-per-activity rule is needed. PanicStream is excluded.

Temporal owns execution progress. PostgreSQL retains only expiring discovery/catalog snapshots and transactional catalog receipts. Fetched item metadata, HTML, ratings responses, and redirect hops stay in activity memory. The workflow and activity code can change freely while this branch remains undeployed; there are no compatibility layers for its previous protocol.

The migration preserves catalog identities, API response contracts, the seven active providers, and independent Archive deletion verification. AI normalization, embeddings, search indexing, production infrastructure, and deployment are outside this work.

## Historical research

The [capacity evidence](capacity-and-evidence.md) records the October 6 catalog measurements and assumptions. These are historical observations, not a production capacity guarantee. The original decision, migration, and implementation documents remain as research history; the architecture and operations documents describe the current code.
