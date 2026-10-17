# Real-data cases for normalization

Start with [cases.jsonl](cases.jsonl) to see the decisions the normalization layer must make. The corpus contains 102 development-labeled cases across 25 artists, using 132 evidence records. Twenty-four artists come from real catalog captures; two short official Bob Dylan song-page excerpts add writer evidence. The [domain contracts](../../../design/2026-10-17-normalization-contracts.md) define the implementation target. No matcher has been evaluated against these cases yet.

The cases cover all five domains: people (44), songs and setlists (28), lineage (15), compositions (8), and venues (7). There are 77 supported expectations, two evidenced distinctions, and 23 unresolved decisions. A supported extraction means the input makes that assertion; it does not independently certify the upstream assertion as historical fact.

## Read a case

Each case names its task and expected result, cites exact fields or text, and explains the decision. [evidence.jsonl](evidence.jsonl) preserves those inputs. [format.md](format.md) defines the machine-readable format and validator command. [CAPTURE.md](CAPTURE.md) explains cohort selection, capture commands and database provenance.

Examples illustrate the responsibilities between components:

- `people-credit-2169958` extracts David Gans and Charlie Miller as transferrer mentions. `people-charlie-cross-artist` leaves a global identity match unresolved: the captured names lack an explicit cross-recording identity reference.
- `people-local-4283991` extracts Aadam Jacobs, Avery and auto_pilot with distinct roles from Wire's HTML description. `people-aadam-cross-artist` proposes a shared Aadam identity using explicit recorded-by credits and the named archive context across artists. Collection membership alone supplies no credit.
- `people-unresolved-3976559` refuses to turn `DAT > Prodif 96 > Wavelab` into a person merely because it occupies the transferrer field.
- `songs-title-33689439` preserves two title-derived song segments in one file, with no invented timing or occurrence alignment. `songs-unordered-78383` does not turn sorted legacy membership IDs into Phish's performance order.
- `venues-fillmore-different-cities` separates equally named venues in Denver and San Francisco. Venue identity positives are development judgments using name and locality together; they still require independent verification.
- `lineage-reconstruction-derived` retains an explicit predecessor resource and conversion assertion without claiming independently verified audio ancestry.
- `compositions-not-fade-away-writers` extracts generic writer credits. `compositions-not-fade-away-phish-gd` keeps same-title repertoire identities unresolved until work-link evidence is acquired. A writer credit and a repertoire-to-work assignment are different decisions.

## Partition and label authority

The corpus reserves 66 cases for development and 36 for evaluation. Related recording/credit examples, shared work candidates, the Aadam project and venue families stay together. The validator rejects an evidence ID or named family appearing in both partitions. It cannot discover unrecorded family relationships; label review must check those too.

All labels are `development_labeled`, including the reserved evaluation cases. They were authored with Codex reasoning and checked in a separate development review. This is not a blind independently adjudicated benchmark. Before enabling automatic acceptance, expand the evaluation sample, independently verify its labels, and report extraction, candidate retrieval and accepted-match results separately. Do not tune on the evaluation partition. If a case is inspected to change a rule, move its whole family into development and acquire a replacement evaluation family.

This deliberately selected challenge set is not a random catalog sample. It includes four sparse production artists, several providers and local Jerry/Aadam imports. It cannot estimate a full-catalog acceptance rate, rare false-merge rate or cost. No paid model requests were used to create it.

## Provenance and integrity

The combined evidence file concatenates separately retained production, venue and local snapshots plus [reference excerpts](references/evidence.jsonl). Each database directory retains its exact query and snapshot receipt. IDs prefixed `local:` refer to `relisten_jerry_20261017`; they are not production rows. The root manifest hashes the combined inputs, cases and capture receipts. Changed labels require a reviewed corpus revision and a new manifest, rather than updating hashes to hide a mismatch.

The reference excerpts were read on October 17, 2026 from the official artist song pages for [Not Fade Away](https://www.bobdylan.com/songs/not-fade-away/) and [Johnny B. Goode](https://www.bobdylan.com/songs/johnny-b-goode/). They retain only titles and brief writer credits, with URLs and retrieval method. They are not whole-page snapshots. The writer wording supports a generic `writer` role; it does not distinguish composer from lyricist. These references do not silently enrich a same-titled catalog row or grant permission for a new provider-wide crawler.

The saved [validation report](validation.json) confirms evidence/case hashes and coverage. All 22 capture and validator behavior tests pass. Run the [format guide's commands](format.md) to reproduce these checks. Validation checks references, exact quotations, structured values, partition separation and hashes. It reports coverage, not model accuracy.

## Next implementation checks

Use [assertions.md](assertions.md) to implement domain-specific evaluators for the documented assertion fields before scoring extraction or matching output. Count missing predictions and invalid evidence separately from wrong identities. Expand real examples for historical venue names/rooms, namesakes with distinguishing evidence, positive repertoire-to-work links, provider-qualified person identities and supported multi-recording ancestry. Retain the current unresolved cases when richer evidence is added: a narrow input must still produce a narrow conclusion.
