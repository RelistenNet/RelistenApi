# Relisten documentation

Start with the task you need to do:

- [Run locally](../local-dev/README.md): databases, services, dummy/Google sign-in and tests.
- [Integrate the catalog](api/README.md): concert/recording identity, imported versus canonical data and ranking context.
- [Use the catalog graph](api/graphql/README.md): full V4 GraphQL reads, shared REST mappings, pagination, consistency, and execution budgets. The [implementation plan](plans/active/2026-11-06-v4-catalog-graphql.md) records the design decisions.
- [Persist catalog responses](api/mobile-sync.md): collection ownership, shared records and ETag limitations.
- [Work on accounts](api/accounts.md): personal membership, catalog references and authentication ownership.
- [Maintain derived counts](normalization-counts.md): writer responsibilities, concurrency and repair.
- [Maintain popularity](popularity/README.md): score publication, demand refresh and recovery.
- [Work on ingestion](ingestion/README.md): provider contracts and operator procedures.
- [Work on normalization](../Relisten.Normalization/README.md): shared vocabulary and storage contracts.
- [Index Archive artists](archive-org-artist-indexing.md): collection discovery.

[Swagger](http://localhost:3823/api-docs/index.html) is the catalog route and
payload reference. The docs above explain domain meaning, responsibilities and
operational procedures that span multiple components.

Dated playlist/history/Sonos documents under `architecture/`, `design/` and
`plans/active/` describe broader future work; use the account guide above for
implemented behavior.
