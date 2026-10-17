# Normalize the full Relisten catalog

## Purpose and scope

Build a production normalization layer for Grateful Dead, Phish, Taper's Section and every current or future artist. Listeners must be able to find the same music, people and physical venues across inconsistent provider labels, inspect supported recording relationships, and report corrections. Preserve playable recordings and existing references while interpretations improve.

Required domains are artist repertoire, cross-artist compositions, people and credits, physical venues and their history, show performances, ordered setlist occurrences, recording-file contents, and capture/transfer/derivation/matrix lineage. Provenance, review, correction history, stable identities and incremental processing apply to every domain. Execution can proceed in dependent stages; none of these domains is an optional follow-up.

This plan gives the engineer and reviewer/operator the outcome and specification work. Schemas, public APIs and migration contracts must follow the audit; this is not yet an implementation-ready database design.

## Progress

- [x] 2026-10-17: establish the complete program scope and distinguish it from the completed local performance/import foundation.
- [x] 2026-10-17: census all 4,379 production artists through the read-only replica.
- [ ] Complete relationship, input-retention and provider audits; retain denominators and missing-data cases.
- [ ] Define domain schemas, public/read-review contracts, ownership and migration behavior from that evidence.
- [ ] Implement required domains, input capture and incremental normalization with controlled verification.
- [ ] Backfill the full catalog, verify public behavior and recovery, then stage production rollout.

## Context and current evidence

The [research record](../../../research/2026-10-17-full-normalization/README.md) owns the census, schema evidence and follow-up audits. Its production snapshot at `2026-10-17T16:32:22Z` contains 292,476 sources, 5,063,161 tracks, 215,560 shows, 216,304 imported venue rows, 16,605 song rows and 38,475 provider setlists. Of the tracks, 5,010,402 are not flagged orphaned. Among those, 116,932 lack a remaining source and matching set, while 4,893,470 have matching catalog playback relationships and media references. Neither the flag nor those links prove remote audio availability. Preserve historical file evidence while restricting playback results to current valid relationships.

Only 53 artists have a repertoire dictionary. Another 4,309 artists have tracks not flagged orphaned but no dictionary. A generic artist must work without a curated fan database. Provider song IDs and specialist catalogs remain valuable evidence where available.

Source text is substantial: 229,568 sources have nonempty lineage; 267,878 have lineage or source text; 246,849 have taper text; 227,655 have transferrer text. These are populated fields, not normalized people or established capture families.

The replica reports PostgreSQL 17.10, recovery/read-only mode and migrations 1–12. The [performance and larger-import foundation](../2026-10-17-show-performances/README.md), including migrations 16–17, is locally verified; it is not evidence of production deployment. Resolve that release dependency explicitly before backfill.

## Required model and ownership

Keep imported facts as evidence. PostgreSQL owns canonical identities, accepted relationships, original supporting inputs and review history. Temporal coordinates bounded, restartable work; Redis is not durable ownership.

An artist's repertoire entry and a global composition are separate identities. Link them through evidence. Distinguish composers, lyricists, arrangers and original performers; equal titles or a cover credit do not establish all these roles. Preserve provisional title-derived entries and their basis without presenting them as verified works.

People have global identities; recording credits carry roles and original wording. Namesakes, unresolved handles and teams must remain distinguishable. Taper, transferrer, musician and matrix-builder credits are different roles. Collection membership does not establish a taping credit.

A physical venue is distinct from an imported name or city coordinate. Retain historical names, dates and supported location evidence. A `Show` represents a performance engagement under the foundation contract; a song occurrence represents a passage within it. Reprises, repeated songs, gaps and unknown passages need independent representation. File contents can span occurrences or performances without invented offsets.

Lineage represents captures, copy/digitization transfers, processing derivations and matrices with several inputs. Keep each relationship typed and independently evidenced. Unknown contributors or an uncertain chain do not remove lineage from scope. Metadata extraction, file hashes and acoustic fingerprints provide different evidence: verified file hashes support byte identity, provider-reported hashes remain assertions, fingerprints and alignment support bounded audio comparisons, and extracted text proposes names, equipment and relationships. None alone establishes every lineage edge or show identity. A default show can retain uncertain or combined recordings; sharing that primary association does not prove shared performance coverage or capture ancestry.

## Plan of work

1. **Finish the audit.** Extend the research queries across all artists and providers. Check playback relationships, ordered setlist information, repeated entries, retained identifiers, credits and lineage text. Inspect provider adapters and retained inputs before deciding what can be backfilled. Existing catalog rows cannot recover fields discarded during import. Specify input capture and targeted metadata refresh where required, with provider failures and unsupported fields recorded explicitly.
2. **Write executable contracts.** Revise the earlier schema and people proposal against the audit. Specify identities, merges/splits and redirects, accepted versus proposed relationships, supporting revisions, review authority, correction submission, permissions and removal behavior. Define public queries for compositions, artist repertoire, people by role, venues/history, ordered occurrences, file contents and lineage. Record exact response schemas, pagination, uncertainty, compatibility and errors before implementation. Name ownership between adapters, extractors, resolvers, review services and read projections.
3. **Implement and backfill incrementally.** Preserve input representation before lossy conversion. Add schema and domain services in reviewable migrations. Make parsing and model-assisted extraction reproducible and separate proposals from acceptance. Include audio-analysis work for lineage and content alignment with measured, versioned methods. Process the full artist registry through resumable checkpoints, including artists without dictionaries; newly discovered artists use the same contracts. Retain accepted identities and reviews across recomputation.
4. **Verify and release.** Run shadow comparisons against existing behavior, review disagreements, and stage activation with reversible read selection. Exercise the full required domain set before declaring the program complete. Validate production migration dependencies and restore evidence before enabling writes.

## Concrete steps and acceptance

Run `docs/research/2026-10-17-full-normalization/scripts/run_live_census.sh` for the documented read-only census. Store audit commands and receipts beside that research. Use `dotnet build RelistenApi.sln` and targeted `./test-local.sh --filter ...` checks for each implementation increment; run the affected regressions and `dotnet csharpier check .` before commits.

Verify through service contracts and running public APIs: the same composition across artists; role-filtered recordings for one person across artists; namesakes; historical venue names; repeated songs and multi-song files; early/late and partial-date shows; several transfers of a capture; matrices; unresolved text; reviewed corrections; changed provider evidence; repeated imports; cancellation and restart. Compare IDs, ordering, counts, relationships and complete playback URLs. Use artist cohorts drawn from the complete census, including sparse and unfamiliar catalogs. GD and Phish are reference cases, not the entire acceptance population.

Audio validation must report the evaluated material, false matches and missed matches. No universal correlation threshold, overlap percentage or disjoint-cluster rule can be assumed to establish capture or show identity.

## Recovery and rollout

Commit evidence, accepted decisions and checkpoint advancement atomically where required. A partial fetch cannot imply deletion. Failed extraction preserves prior accepted playback and records unresolved work. Resume safely after losing execution history. Backfill and correction jobs need bounded resource use, progress receipts and cancellation.

Rehearse migrations, backup/restore and API compatibility on a representative restored catalog. Stage matching workers and readers, drain incompatible histories, and document rollback for each migration. Read rollback must preserve identities, evidence and reviewed decisions; it cannot rely on dropping the normalization schema. This plan authorizes no production write or deployment.

## Surprises and discoveries

The earlier 30-table proposal excludes normalized people/lineage and defers composition reconciliation. It cannot be implemented unchanged for this scope. The colleague's catalogue terminology and audio thresholds also require adaptation rather than adoption. Populated text and non-orphan flags measure available evidence, not normalized truth or proven playability.

## Decision log

- 2026-10-17, Alec: full normalization covers every artist; lineage and cross-artist compositions are required in the same program.
- 2026-10-17: separate feature scope from execution dependencies and unknown facts. Complete the audit before fixing schema/API details.
- 2026-10-17: preserve stable playback references, evidence and reviewed corrections; use the locally verified performance contract as a deployment dependency.

## Outcomes and retrospective

The scope and all-artist census are established. Domain contracts, full input audits, implementation and production verification remain open. The [people brief](../../../design/2026-10-17-people-and-recording-credits.md) and [earlier schema](../../../design/2026-10-16-catalog-normalization-schema.md) are design inputs; this plan supersedes their narrower scope and deferrals.
