# Full-catalog normalization analysis

The target is the complete normalization layer for Grateful Dead, Phish, Taper's Section and all current and future artists. People, venues, artist repertoires, cross-artist compositions, ordered occurrences and recording-file contents are required domains. Recording provenance in this release identifies tapers and transferrers; full ancestry and audio-family reconstruction are outside scope. The [execution plan](../../plans/active/2026-10-17-full-normalization/README.md) owns that delivery. This analysis establishes the real inputs and constraints; it does not claim that a census has resolved their identities.

The [real-data evaluation corpus](evaluation/README.md) adds 132 evidence records and 102 labeled cases across 25 artists. It separates extraction, identity matches and relationship claims, including unsupported decisions. The [domain contracts](../../design/2026-10-17-normalization-contracts.md) define the proposed implementation and additive APIs. Neither artifact claims measured normalization accuracy.

## What was queried

The census reads the production replica directly in one repeatable-read, read-only transaction. It profiles every artist and every row in the named catalog relations. It does not select a few artists or extrapolate global counts from a sample. The [SQL](sql/catalog-census.sql) and [summary generator](scripts/summarize_census.py) retain the input snapshot, executed-query hash, exact totals and independently reconciled artist profiles.

The current results are in [census-summary.json](evidence/census-summary.json), [artist-profiles.csv](evidence/artist-profiles.csv) and [catalog-schema.json](evidence/catalog-schema.json). The raw JSONL receipt remains outside Git under `/tmp/relisten-full-normalization-20261017/`; its SHA-256 identifies it. All saved profiles contain counts and public artist identifiers, rather than recording descriptions, credit text or credentials.

The October 17 census contains **4,379 artists, 292,476 sources, 5,063,161 track rows, 215,560 shows, 216,304 venue rows, 16,605 repertoire rows and 38,475 provider setlists**. Only 53 artists have repertoire rows; 4,309 artists have files not flagged orphaned but no repertoire dictionary. The resolver cannot require a curated song catalog.

Credit and lineage evidence is already substantial: 246,849 sources have taper text, 227,655 have transferrer text, 229,568 have lineage text and 267,878 have lineage or source-chain text. Nonempty fields can contain multiple people, unknowns or unrelated narrative. These counts do not measure extracted identities or establish shared capture ancestry.

## Playback eligibility is a separate relationship check

The catalog marks 5,010,402 tracks as not orphaned. Of those, 116,932 have no remaining source or matching source-set row. Another 4,893,470 have matching artist/source/set/show references and a media URL. The latter is a catalog-link check, not an HTTP availability or full-audio test. Unmatched historical file rows must not become live normalization search results solely because their orphan flag is false.

This is a data-quality finding, not authorization to delete or rewrite these rows. Retain historical evidence and distinguish it from currently linked playback. The summary reports these conditions separately, and source/track UUIDs remain unchanged.

The live replica is on migrations 1–12. The locally tested performance and intake foundation includes migrations 16–17. The analysis must not assume those newer relations are deployed; the full implementation must account for that release dependency.

## What the next analysis must establish

**Within every artist**, inventory actual title forms, repertoire collisions, ordered provider evidence, repeated passages, compound files, partial recordings and recording credits. Preserve artist-scoped song identities. Distinguish a repertoire entry, a performance occurrence and its recording files.

**Across artists**, build candidate indexes for physical venues, people and compositions from the whole catalog. Retain the context that can distinguish rooms, relocations, namesakes, roles and different works with equal titles. Compare plausible candidates rather than every possible pair. Accepted identities are shared; an artist's derived view references them instead of creating a private copy of each person or venue.

**For recording credits**, examine structured taper/transferrer fields and explicit role statements in descriptions and lineage prose. Extract roles before resolving people globally. Retain equipment and processing text as evidence; full ancestry/audio modeling is outside this release.

**For compositions**, reconcile repertoire entries with supported work and credit information. Composer, lyricist, arranger and original performer remain different credits. Preserve arrangements and excerpts without pretending they are exact work equivalences. Shared titles retrieve candidates; they do not select the work.

The [provider evidence audit](provider-evidence.md) identifies where importers retain or discard the needed information. Missing stored fields require expanded input capture and metadata refresh as part of this program. The current recording-credit and composition outcomes require that acquisition work.

Evaluation must distinguish parsing accuracy, candidate retrieval, accepted identity accuracy, occurrence alignment and role accuracy. Use independently reviewed cases across providers, catalog sizes and ambiguity levels. Keep related recordings and shared upstream setlist families together in evaluation partitions. Full coverage means every artist is processed with an explicit outcome; unsupported facts remain unresolved rather than fabricated.

## Reproduce the census

From the repository root, use a new output filename. The wrapper reads the current database password directly from the existing Kubernetes secret, connects only to the production replica and writes evidence locally:

```sh
bash docs/research/2026-10-17-full-normalization/scripts/run_live_census.sh \
  /tmp/relisten-normalization-next/catalog-census.jsonl

python3 -B docs/research/2026-10-17-full-normalization/scripts/summarize_census.py \
  /tmp/relisten-normalization-next/catalog-census.jsonl \
  /tmp/relisten-normalization-next/report \
  --sql /tmp/relisten-normalization-next/catalog-census.jsonl.sql
```

The wrapper retains the exact SQL used beside the output. Run the summary into a new evidence directory, then compare it with the committed results; preserve both snapshots. A failed or incomplete SQL receipt must not produce a completed report.

The census has a per-statement timeout and advances no database state. A new capture uses a new transaction and can legitimately differ from the previous live snapshot. Provider failures, changed rows and missing evidence must remain visible in the later backfill, rather than being interpreted as deletions.

## Status

The all-artist census, initial importer-retention audit, proposed domain/API contracts and first development-labeled corpus are complete. Global candidate indexes, independently verified evaluation, provider evidence recovery, migrations, normalization services and public reader implementation remain work in the full plan. Automatic matching remains the release outcome. Limit recording provenance to taper/transferrer identities, without full ancestry/audio work. The [scale audit](scale/README.md) expands real-data profiling and sampling across every eligible artist; the initial 102 cases are regression fixtures, not acceptance evidence. The older [19-artist research](../2026-10-16-catalog-enrichment/README.md) is historical evidence; its statements that Collections or Aadam support were unimplemented are superseded by the [completed import/performance work](../../plans/active/2026-10-17-show-performances/README.md).
