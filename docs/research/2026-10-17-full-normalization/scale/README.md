# Catalog coverage and sampling for normalization

The 102 hand-selected cases are a development challenge set. They cannot establish accuracy across the catalog. This capture supplies broader real inputs and measures the formats the implementation must handle. The release still requires evaluated automatic matching for venues, artist repertoire, global compositions and people. Person extraction includes taper and transferrer credits found in recording and lineage prose. Full recording ancestry and audio analysis are outside the current release scope.

## What was captured

A read-only production replica transaction ran from **2026-10-17 17:03:23 UTC to 17:04:06 UTC**. Its population profiles cover all 4,379 artists, 292,476 recordings, 5,063,161 files, 216,304 venue rows, 16,605 repertoire rows, 38,475 setlist shows and 575,103 setlist memberships. Profiles and independent relation counts reconcile in the same repeatable-read snapshot. Production has migrations 1–12; this capture does not assume newer local normalization tables exist.

The sample contains **86,655 rows**:

- **32,593 recordings**, with up to eight per artist, covering all **4,358** artists with recordings. Exact taper, transferrer, description, source and lineage fields are retained.
- **34,861 active files**, with up to eight per artist, covering all **4,362** artists with active files. Source, recording set and show context are retained where the relationships exist.
- **17,073 venue rows**, with up to four per artist, covering all **4,362** artists with venues.
- **1,696 repertoire rows**, with up to 32 per artist, covering all **53** artists with repertoire rows.
- **432 setlist shows**, with up to eight per artist, covering all **54** artists with setlist shows. The selected shows retain **6,347 stored song memberships**. These memberships have no stored performance order.

There are **zero missing eligible artists** in each selection. The [coverage receipt](evidence/coverage.json) includes artists with no eligible rows, recording-count strata and configured provider coverage. A configured provider mapping does not prove that provider owns a particular recording. Newer local Jerry and Aadam evidence remains in the [existing evaluation capture](../evaluation/CAPTURE.md).

Selection ranks each relation's rows within artist by `md5('normalization-scale-v1:<relation>:' || id)`, with row ID as the tie-breaker. A fixed seed makes the selection reproducible within the captured snapshot. Small artists therefore receive more weight than they would in a uniform recording sample. Population-level accuracy estimates must use a separate probability sample or explicit sampling weights. This broad sample helps discover formats and select labels; it is not itself a labeled benchmark.

## What the full population tells us

Across all recordings, 246,849 have taper text and 227,655 have transferrer text. **17,656** contain `;`, `,`, `&` or `/` in either credit field, and **199** have more than 120 characters in either field. These are extraction candidates, including possible non-person text; populated fields are not certified credits. Descriptions contain HTML-like tags in **123,495** recordings. Lineage contains `>` in **216,902** recordings and can supply additional person mentions without requiring an ancestry graph.

Across the 5,010,402 flag-active files, **649,474** titles contain `>`, `→` or `»`; **176,913** contain `*` or `/`; **180,148** contain bracketed text; and **27** have no nonblank title. The measures overlap. Separators and brackets are candidate syntax, not proof of a medley, annotation or song identity. Flag-active files can still have missing catalog relationships. In this sample, **128 files** lack source, set and show context; they remain in the evidence rather than silently disappearing from coverage.

The selected recording inputs contain 5,912 distinct exact taper strings and 4,099 distinct exact transferrer strings. The 17,073 selected venue rows have 12,880 distinct exact `(name, location)` pairs. Exact repetitions identify useful review families; neither equal strings nor unequal strings establish whether two rows represent the same person or venue.

The source/file sample spans **44,657 artist/show families**, with **22,669 additional rows in already represented families**. Keep related recording evidence together when assigning development and evaluation partitions. The family key does not discover shared people, venue histories, compositions or recordings with different show assignments; independent label review must check those relationships too.

## Use the evidence

Raw exact inputs remain outside Git at `/tmp/relisten-normalization-scale-20261017/capture-v1/snapshot.jsonl` (**176,666,741 bytes**). The receipt retains the raw and executed-SQL hashes, snapshot identity and replica state. The SQL and schema/index records are in the raw capture. It contains no API key or database password.

Use the sample to select and independently adjudicate larger positive, negative and unresolved groups for each automatic matching task. Extracting a name from a credit and matching that mention to a global person need separate expectations. The same distinction applies to parsing a compound title and linking it to repertoire or a global composition. A fallback model's own proposal is not an independent accuracy label.

No paid model requests or production writes were performed for this capture. It does not report matching precision, recall, coverage or cost. Those require running the implementation against independently reviewed cases, including ordinary inputs and difficult cases.

## Reproduce and verify

Run from the repository root, choosing a new capture directory and a new report filename. The capture command reads the current database password from the documented Kubernetes secret and passes it directly to `psql`. It does not print or save the password. Each statement has a three-minute timeout; the connection defaults to read-only and the transaction explicitly requires read-only mode. Failed queries retain their partial output. The completed filename is published only after the query's completion marker.

```sh
python3 docs/research/2026-10-17-full-normalization/scale/scripts/capture.py /tmp/relisten-normalization-scale-new
python3 docs/research/2026-10-17-full-normalization/scale/scripts/summarize.py /tmp/relisten-normalization-scale-new /tmp/relisten-normalization-scale-new-coverage.json
python3 -m unittest discover -s docs/research/2026-10-17-full-normalization/scale/scripts -p 'test_*.py' -v
```

The validator checks replica/read-only state, snapshot completion, SQL integrity, unique sampled IDs, all eligible artist selections, independent relation totals and setlist membership totals. All **14 behavioral tests** pass. They exercise successful reconciliation and reject missing samples, duplicates, incomplete captures, wrong population counts, writable endpoints and changed executed SQL. They also verify that an existing output is preserved, secret lookup failure prevents a database connection, and query failure leaves a partial capture without publishing it as complete. They verify capture integrity; they do not assess label correctness.

## Local evidence retention

A hash-verified copy of the raw snapshot, executed SQL and capture query receipt is retained under `local-dev/normalization-data/2026-10-17-cross-artist/` in this workspace. This directory is Git-ignored and survives temporary-directory cleanup. `retention.json` records the copied hashes. The committed coverage receipt retains the original capture path and hash; it is not a backup of the input bytes. Use the retained workspace copy for extraction and annotation work.

## Local extraction run

The [extraction receipt](evidence/extraction-shadow.json) records a completed local run over all 32,593 source samples and 34,861 file samples: 67,454 outputs across 4,362 artists, with zero processing errors. It reports 33,547 title segments and 3,055 unresolved title results. All 128 files with absent source/set/show context remain in the output. Credit counts are mentions, including repeated assertions in structured fields and descriptions, not distinct people or accepted matches.

The runner currently processes credit and title syntax. Venue, repertoire and setlist samples are counted explicitly as unsupported by this command; they are not silently considered normalized. Outputs remain under the ignored workspace data directory. The receipt hashes the input, output and implementation files. No paid model or database mutation is involved. Neither completion nor recognized-title coverage establishes extraction or identity accuracy.

See the [tool guide](../../../../tools/RelistenNormalization/README.md) to reproduce extraction into a new output directory. The expanded sample exposed real credit-parser errors in role-prefixed handles and trailing narrative; exact regressions now protect those cases. Continue using new samples and independently checked decisions to evaluate matching rather than treating the small regression set as release evidence.

## Annotation and Luna integration

The [blind annotation pack](annotation/README.md) contains 18,628 extraction tasks and 866 candidate entity links selected from this retained capture. Every answer is null. Exact evidence stays in the ignored workspace directory; the committed receipt records hashes, coverage and partitioning. Twelve behavioral tests and an independent rebuild verify the pack. Independent labeling and adjudication remain required.

A separate two-input [Luna connectivity receipt](evidence/luna-connectivity.json) records schema-valid, span-validated real Responses calls costing $0.0001745. Repeating both requests without an API key returned cached results, with the same two durable attempts and unchanged cost. This verifies integration and retry reuse; it supplies no independently judged accuracy or routing result. The broader training, matching and fallback comparison follows the [evaluation strategy](../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md).

## Local publication rehearsals

The [repertoire rehearsal](repertoire-local-rehearsal.md) publishes 209 imported
songs and checks compatibility through the API. A separate
[recording-input preparation](materialization-local-inputs.md) retains a published,
replayable page of 25 recordings across 17 artists for the setlist and file-content
consumers. The latter preparation publishes no accepted file contents or setlists.
