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

## What implementation needs next

Build a broader development set of catalog-song-to-composition correspondences,
with retained writer or work evidence and complete plausible alternatives.
Include covers, equal-title different works, medleys and insufficient evidence.
These two questions can exercise evidence acquisition and correspondence
contracts, but cannot train or evaluate an identity matcher by themselves.

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
