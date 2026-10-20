# Correct a match and reuse the answer

The private catalog readers give Alec a correction action beside venue accounts,
artist songs, composition choices and person name groups. A correction records a
specific correspondence, its scope and the reason. Accepted venue and composition
decisions change the private reader without changing original files or playback
ownership. Person name-group decisions are saved for normalization; they do not
reassign individual recording credits.

## Responsibility and contracts

`NormalizationExpertKnowledgeService` owns durable decisions in PostgreSQL. It
uses the existing normalization review ledger. Migration 60 adds typed selectors,
target references and revision history. A reference identifies its domain,
namespace and original stable key. Only an actual canonical entity can supply a
canonical UUID; a temporary directory group is not a canonical identity.

`ExpertKnowledgeResolver` is the shared C# policy. It evaluates an original account
and its name, artist, provider, locality and date against accepted decisions.
An exact account correction overrides a reusable positive alias. A rejected match
excludes its named target. Conflicting accepted targets remain visible for review.
A rejection never removes the recording or its audio.

The standalone `review-serve` command starts the private review host without API
Startup, ingestion or schedules. It verifies the caller's Tailscale user identity,
uses same-origin writes and a CSRF token, and derives the reviewer from that
identity. It connects only to a dedicated local normalization database. It never
uses the production catalog as its write destination.

The Python readers expose original source references, target search and paged
observations. They fetch C# evaluations and apply temporary read-only projections;
they contain no second identity matcher. Original source evidence bypasses those
projections so a saved correction cannot rewrite the evidence that justified it.

## Review journey

1. Open an original account. Inspect its evidence and each candidate’s evidence,
   then choose the correct target. Evidence links open separately from selection.
2. Apply the correction to that account, or to an exact spelling within an explicit
   artist, provider, locality or date scope. Other spellings need their own alias.
3. Preview current affected accounts and record the reason.
4. Save. The service pins the original evidence, checks the current revision and
   records an attributed operation. Retrying the same operation is idempotent.
5. Reload venue or composition results to see the correction. Song groups use the
   saved correspondence on the next normalization run. Individual recording
   credits stay unchanged. History retains earlier decisions; withdrawal creates
   a new revision and removes the withdrawn decision’s matching authority.

The host rechecks evidence between preview and save. Changed source evidence or
an intervening decision requires a new preview. A failed save does not partially
publish a rule. If the authoritative review service is unavailable, a reader that
uses its decisions returns a retryable error instead of quietly discarding those
decisions. Original playback remains in its existing catalog home.

## Reference-led improvement

The [whole-artist benchmark](reference-benchmark.md) compares the actual C# results
with retained Phish and Grateful Dead song memberships. Disagreements supply
review leads with original evidence. They are not automatically false matches:
recordings can be incomplete, references can omit songs, and legacy dictionaries
can assign several IDs to one title.

Preserve raw-ID scores when expert identity equivalences are added. Report any
adjudicated-equivalence score separately. User corrections are development data;
they must not become undisclosed answers in untouched evaluation data.

## Local operation and release boundary

The durable local store is `relisten_normalization_knowledge`. Its retained setup,
configuration and evidence live under
`local-dev/normalization-data/2026-10-20-expert-knowledge/`. Protect this database
and its evidence from disposable-test cleanup. The host configuration contains
fixed reader origins, an evidence root, allowed decimal Tailscale user IDs and
separate binding and public addresses. There is no OpenAI key requirement.

Song correspondence review is currently limited to exact accounts within the
same artist and captured scope. Original and title-derived keys remain stable.
The C# title resolver and consolidator consume accepted direct correspondences;
they preserve every imported ID and each distinct play. Transitive identity merges
are not inferred. Rejected candidates do not invent another identity.

Export `/api/knowledge` to a retained JSON file and pass its file SHA256 with
`music-consolidate ... --knowledge <snapshot.json> <snapshot-sha256> [--resume]`.
The batch records both the resolver snapshot hash and retained file hash. A changed
snapshot cannot resume or relabel a completed run. Saving a song decision records
knowledge immediately; the next normalization pass applies it to song grouping.

The public normalized API and production publication remain separate consumers.
A reviewed local correspondence does not itself merge canonical entities, move
recording ownership or activate a production matching policy. Backfills
consume the pinned reviewed knowledge and preserve its revision fingerprint.

The live private review page is
[Review a match](http://alecs-macbook-pro.tail09dbf.ts.net:18778/).
Venue pages offer **Correct venue**; artist-song pages offer **Resolve song** and
a separate composition action. The home page lists retained Phish/GD benchmark
disagreements with artist and category filters. Links use authenticated original
accounts rather than treating a raw title as an identity.

A full-catalog HTTP check saved a temporary MSG account decision, loaded the
reviewed venue with 950 recordings in 4.94 seconds, and withdrew that decision.
The history survived a host restart with the original target and account scope.
No temporary decision remains active. Missing CSRF tokens and unrecognized Host
headers returned HTTP 403. The 390-pixel review layout was checked in the browser. The reference queue
opened the Phish TMWSIY account, and target search returned the imported
The Man Who Stepped Into Yesterday song before title-derived variants. No song
decision was saved during this browser check.

Source and target evidence URLs resolve only within the configured reader origin.
The review host accepts root-relative paths, and the browser rejects executable,
external and credential-bearing links. Original evidence remains read-only.

## Current reader and development correction

The music reader now uses generation `1f5161e4f8df48981cf996bb`. Song grouping,
composition hypotheses and model selections were replaced together; the exact
previous command is retained for rollback in
`local-dev/normalization-data/2026-10-20-expert-knowledge/music-cutover-receipt-v1.json`.
The generation covers all 971,127 original title records. It retains 10,321
candidate song groups and 2,656 external works; 100 groups have model results.

Furthur’s “West L.A. Fadeaway” demonstrates an actual correction. Shared spelling
retrieval recovered the Garcia/Hunter candidate after the artist dictionary
standardized “LA” to “L.A.”. The model still declined to select it. Codex checked
the official song credits and the matching Furthur performance reference, then
saved an account-scoped development correspondence through the UI. The reader
now prefers the reviewed work while retaining the original model result under
expandable history and all 16 linked performances. This decision is attributed as a Codex development
correction in its reason; it is not a human adjudication by Alec.

The permanent rule is `310d15c8-5085-4f3f-39bb-268b9bdd39ee`, revision 1. Its
reference check and public response are retained as
`furthur-west-la-reference-check.json` and
`furthur-reviewed-composition-http.json` in the expert-knowledge directory. The
reference check cites the [official Garcia/Hunter credits](https://www.dead.net/song/west-l-fadeaway)
and [Furthur’s February 12, 2010 performance record](https://www.setlist.fm/setlist/furthur/2010/hampton-coliseum-hampton-va-33deb4c9.html).
The decision is development-known evidence and must not enter untouched accuracy
measurements as an independent answer.

Evidence ownership checks establish which source a model cited. They do not
establish that the model interpreted the source correctly. One Jamie McLean
“Such a Night” suggestion relies on a weak connection between a New Orleans
festival and a Last Waltz recording. Keep that as a semantic review lead. The
exact result and the five separately inspected development cases are retained in
`2026-10-20-composition-refresh-preparation-v1/next-v4/selection-projection-receipt.json`
and `source-audit.json`. Neither is a population accuracy estimate.
