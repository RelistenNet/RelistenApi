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

1. Open an original account and choose its correct target.
2. Apply the correction to that account, or to an exact spelling within an explicit
   artist, provider, locality or date scope. Other spellings need their own alias.
3. Preview current affected accounts and record the reason.
4. Save. The service pins the original evidence, checks the current revision and
   records an attributed operation. Retrying the same operation is idempotent.
5. Reload the directory to see the reviewed result. History retains the earlier
   decision; withdrawal creates a new revision and removes its matching authority.

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
