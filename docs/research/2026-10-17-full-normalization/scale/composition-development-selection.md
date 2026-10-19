# Composition matching needs writer and work evidence

The retained annotation pack contains 74 cross-artist composition questions, but
only **two questions, four repertoire rows and four artists** meet both existing
development partitions. Those four rows contain titles and artist-scoped song
keys, without writer credits or global work identifiers. They cannot establish
whether either pair represents the same composition.

The [selection receipt](evidence/composition-development-selection-v1.json)
records this inventory. It supplies no new identity labels or matching policy.
The full source records and executable selector remain in ignored local storage.

## What was selected

The [original annotation pack](annotation/README.md) assigns recording-family
and artist partitions separately. Of its 74 composition questions, 22 belong to
the development family partition, seven to validation, three to test and 42 span
partitions. Requiring both records' artists to belong to development removes 20
of the 22 development questions. No reserved family removes a further question.

The remaining pairs are:

- “Mystery Train”: Grace Potter and The Other Ones.
- “Stop”: Ryan Adams and Umphrey's McGee.

Equal titles selected these questions. Neither an equal title nor a different
performer answers them. The retained `upstream_identifier` values are title
slugs scoped to the artist; they are not qualified global work identifiers.

The selector authenticates the original task file, evidence file, metadata
projection and reservation index. It selects eligible physical line numbers
from metadata before decoding any raw record. It verifies the four selected
line hashes and source coordinates, then rechecks the input hashes before
writing a completed receipt. Only four raw records are decoded; all answers
remain null. No provider request or database write occurs.

## What the original selection does not establish

The broader development pipeline below now retains work candidates and writer
facts across the proposed song population. It still needs independently supported
catalog-song-to-composition labels, including covers, equal-title different works,
medleys and insufficient evidence. These two original questions can exercise
evidence acquisition and correspondence contracts, but cannot train or evaluate
an identity matcher by themselves.

Use the existing [composition processor](../../../plans/active/2026-10-17-full-normalization/composition-resolution.md)
to retain bounded MusicBrainz search and lookup results. A failed lookup leaves
the correspondence unresolved. Search rank, performer identity and title
agreement alone do not authorize a link. Successful live search verification
also remains open in the [acquisition receipt](evidence/musicbrainz-search-local-v1.json).

Keep artist and identity generalization suites explicit. An experiment may use
a declared family-development cohort for one suite and a separate artist
holdout for another; it must not silently relabel held-out artists as development
or claim that one split establishes both kinds of generalization. Follow the
[evaluation strategy](../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md)
when adding training, tuning and untouched test cases.

## Expanded retained pool and development trial

The completed `composition-hypotheses-output-v2` local run reconciles all
896,216 unique proposed artist-scoped song targets from the frozen music image.
Its retained pool contains 2,656 qualified works: 8,389 targets have literal-title
or explicit-alias candidates and 887,827 have none in this pool. The refreshed
input includes 114 previously retained work-search responses and six cited
program/recording/work responses. It does not complete the unexecuted acquisition
queue or establish an exhaustive MusicBrainz universe. These are candidate
relationships, not accepted composition identities or independent songs.

The expanded composition projection is live in the local reader. Its candidate
works, artist filters and existing playback journeys remain available. Preferred
model choices are withheld while the source correspondence defects below are
corrected. Historical trials remain intact. The completed expanded first-100
v4 trial retained 82 selections and 18 abstentions, with $0.7914525 accounted from
reported usage. The cohort is a development prefix, not a random sample or an
accuracy evaluation.

The trial still selected Lincoln Chase's work for Mike And The Moonpies’
“Such A Night.” Its explanation attributed the familiar Dr. John/Last Waltz song
to Chase even though the supplied Dr. John candidate retained actual Last Waltz
release memberships and forward recording-to-work relationships. The evidence
survives in the input; the explanation is an unsupported model inference.
A program tribute does not make its audio the same recording as the original.

The later v5 trial stopped with 24 durable rows. V6 completed the same 100 targets
with 75 selections and 25 abstentions, accounting for $1.06163225. V6 chose the
Dr. John work for the Last Waltz example, but selected the unrelated Chase work
for Dogs in a Pile without a distinguishing target connection. These are
reported development outcomes, not accuracy measurements.

## Original performer credits need a recording-to-work connection

Three retained Go-Set pages supply explicit Dr. John cover credits for Dogs in
a Pile’s “Such a Night,” including two provider song-history occurrences. A new
context generation preserves all 100 original targets and adds those two cited
witnesses. Their attachment is an explicitly unaccepted development proposal,
with full setlist, venue and guest evidence. The original 2023 recording notes
say April 5 while the catalog and Go-Set say April 12; neither claim is rewritten.
See [the operator guide](../../../../tools/RelistenNormalization/go-set-composition-sources.md)
for acquisition and supplementation contracts.

The v7 trial exposed two defects and was stopped after 44 durable output rows.
It correctly connected the Moonpies tribute to the Dr. John work. For Dogs it
cited the Dr. John annotation but chose a Chase-work path whose recording was
credited to Elvis Presley. Checking that a target annotation and an external
path both exist does not establish that they connect. The performer named by
the target must correspond to the recording performer on the cited path. The
work's composer can be a different person.

V7 also made source addresses difficult for the model to use: witness texts
showed global text-pool IDs, but result references required witness-local array
positions. Failed responses used the displayed pool IDs or renumbered only the
recording views. These references were rejected. Their full HTTP response
bodies, rationale, usage and request identities remain in
`normalization.extraction_attempts.failed_response_body`; failed responses do
not enter the reusable result cache. Do not reinterpret old indices silently.

The interrupted job accounted for $0.4974739, including $0.3500055 in reported
usage and retained reservations for two cancelled requests without usage. No
attempt remains running or uncertain. The 44-row prefix is incomplete and is
not served. The v8 request representation now labels each witness, local claim and local
text explicitly. It connects cover attribution to recording performers before
permitting a preferred proposal. Its source contracts pass; its next live trial
is waiting for local disk capacity and is not an accuracy result.

## Context-enriched request limits and accounting

The Luna composition selector admits complete context-enriched requests up to
2,000,000 UTF-8 bytes. Requests without target context retain their 512,000-byte
limit. Neither path clips candidates or source text to fit. The larger local
byte limit does not establish that a request fits the provider's token window;
provider acceptance of the largest retained request must be checked through a
bounded live trial within the authorized spending limit.

For new context attempts, reservation uses the rendered byte count plus 16,384
as a conservative input-token bound and includes the full output allowance.
When that bound exceeds 272,000, reservation uses $0.20 per million input tokens
and $0.75 per million output tokens. Known usage selects the tier using the
reported input-token count: $0.10/$0.50 through 272,000 and $0.20/$0.75 above it,
for the full request. These are undiscounted accounting estimates from the
[official model card](https://developers.openai.com/api/docs/models/gpt-6-luna),
not an invoice reconciliation. Missing usage leaves the reservation charged;
resume does not authorize another paid attempt.

The local byte-limit change alone does not change prompts or cache keys.
Changes to enriched evidence, reasoning or source-reference contracts create a
new enriched request identity;
no-context v2 request bytes and caches remain unchanged. Historical v3/v4
outputs and ledger charges remain intact. Generate a fresh preflight and use a
new execution directory and capped job for each changed representation; retain earlier receipts as written.
