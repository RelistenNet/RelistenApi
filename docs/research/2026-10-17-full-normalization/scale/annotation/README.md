# Blind annotation tasks for automatic matching

This task pack prepares real catalog inputs for independent labeling. It contains **18,628 evidence-extraction tasks and 866 entity-link tasks**, with **zero completed labels**. It does not establish matching accuracy. The [receipt](receipt.json) records selection counts and hashes; exact records and empty answer fields stay in the ignored local data directory.

The pack covers 6,000 recording sources across all 4,358 eligible artists, 6,000 venue rows across all 4,362 eligible artists, 4,500 file titles across all 4,362 active-file artists, all 1,696 sampled repertoire rows across 53 artists, and all 432 sampled setlist shows across 54 artists. These are selections from the [catalog-wide capture](../README.md). The source capture itself uses per-artist caps, so these tasks cannot estimate catalog-wide accuracy without a separate probability sample or explicit weights.

## What reviewers decide

Evidence extraction and identity matching need different labels. A correct name extraction can still be linked to the wrong person. Keep those errors separate when evaluating automatic matching.

For each `evidence_extraction` task, read its referenced record in `evidence.jsonl` and record only supported assertions:

- **People:** extract each credited person and role, with priority on tapers and transferrers. Keep their spelling, handles and contact strings as evidence; do not assume that a contact address establishes identity or that every credit field contains a person. Equipment, unknown credits and unclear role prose belong in `unknowns`. Full recording ancestry is outside this release.
- **Venues:** identify the stated name, room, location, coordinates and former names where present. A venue row is evidence about a location; it does not itself prove a globally unique place. Preserve uncertainty about rooms, reused names and conflicting locations.
- **Repertoire:** identify the artist-scoped song title and supported provider identifier. Do not assign a global composition from its title alone. Most of these rows contain no writer or work identifier.
- **File contents:** identify ordered title mentions, repeated titles, partial coverage, non-song text and uncertain separators. A printed `>` can support a title marker; it does not prove a canonical segue or audio timing. Do not invent boundaries in an audio file.
- **Setlists:** record the stored song memberships. These rows have no stored performance order, repeats, timing or segues. Mark those properties unknown rather than deriving them from the JSON array order.

Each assertion needs an evidence ID, a field path within `raw_record`, its exact supporting quote, and start/end offsets in Unicode code points. The end offset is exclusive. Use a structured field locator for a non-text value. Keep each assertion's scope explicit: artist repertoire, global person, global venue or global composition.

The `entity_link` tasks ask whether two referenced inputs support `same_entity`, `different_entities` or `insufficient_evidence`. They include 318 person pairs, 474 venue pairs and 74 cross-artist composition pairs. Equal folded credit strings, venue names/locations or song titles selected these candidates. These selection rules supply **no answer**. Name collisions and incomplete metadata can require a negative or unknown decision. The current sample contains no duplicate artist-scoped repertoire collision pair; further repertoire matching labels must include file-title mentions and independently checked candidate songs.

Reviewers must give the decision, supporting evidence and reason, then identify the reviewer and a separate adjudicator. External corroboration must retain its URL/provider key, observation time and exact supporting text or structured identifiers. For compositions, obtain writer/work evidence before declaring that two titles name the same work. Do not turn a model's proposal into its own expected answer. A second blind reviewer should decide disputed cases before they enter a scored holdout.

All `answer` fields begin as `null`. The builder does not contain extractor output, model output, accepted identity IDs or guessed labels. The pack is an annotation input, not an accepted-data import format.

## Selection and holdouts

The builder first selects one hash-ranked row for every eligible artist in each relation. It fills remaining source, venue and file quotas by cycling through coarse syntax strata, such as compound credits, HTML descriptions and separator-bearing titles. It retains all sampled repertoire and setlist rows. These strata describe raw syntax; they do not classify truth.

Every retained evidence item has two separate partition fields:

- `family_partition` assigns known recording/show families and byte-equivalent annotation inputs together, using the **entire input snapshot before selection**. A family connection through an unselected record therefore still prevents that evidence from crossing development, validation and test. Duplicate grouping is conservative input grouping, not a claim of identity.
- `artist_partition` assigns every input for one artist together. Use this as a separate artist-generalization suite. Global people, venues and compositions can occur under several artists; duplicate inputs and shared identities can therefore cross this suite. It does not promise identity-disjoint evaluation.

The hash buckets target 60% development, 20% validation and 20% test; observed counts vary with family sizes. A pair whose inputs belong to different family partitions is tagged `cross_partition_review_only`. It can support adjudication and future benchmark construction, but must not enter training or scored holdouts in this partitioning. Do not mix the two suite definitions or tune matching rules from validation/test answers.

Known-family keys do not discover recording families with different show assignments, alternate person identities, shared compositions, venue history or correspondences between the separate legacy show and setlist-show tables. Reviewers must identify these connections before claiming an independent identity holdout. Preserve exact held-out task IDs and rule versions when producing an evaluation report. This pack provides no candidate-recall measurement and no estimate of positive, hard-negative or unknown class counts until the cases are adjudicated.

## Files and reproduction

The actual pack is under:

```text
local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/
  evidence.jsonl
  tasks.jsonl
  manifest.json
```

`evidence.jsonl` retains exact selected records, resource UUIDs, artist UUID/name, known recording families and both partition assignments. Its provider name `relisten_catalog_snapshot` identifies this capture; it does not assert which upstream provider owns a recording. `tasks.jsonl` refers to evidence IDs without copying or transforming the raw text. The manifest records the input and builder hashes, output hashes, coverage, selection strata and uncompleted-label status.

Run from the repository root with a **new** output directory:

```sh
python3 docs/research/2026-10-17-full-normalization/scale/annotation/build_tasks.py \
  local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl \
  local-dev/normalization-data/2026-10-17-cross-artist/annotation-next
python3 -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/scale/annotation \
  -p 'test_*.py' -v
```

The builder validates the read-only replica header, final completion marker, unique resource IDs/UUIDs, per-artist capped samples and independent relation totals. It rejects truncated inputs, unknown record kinds and rows after completion. It never overwrites an output directory. A failed write can leave partial files but cannot publish `manifest.json`; choose another directory after investigating the failure. The complete manifest is written through a temporary filename and an atomic rename.

All **12 behavioral tests** pass. They exercise exact evidence retention, artist coverage, deterministic selection, known-family and duplicate isolation, links through unselected records, artist suites, unlabeled pairs, malformed captures, existing output preservation and failed writes. No production queries, paid model requests or normalization writes are involved.
