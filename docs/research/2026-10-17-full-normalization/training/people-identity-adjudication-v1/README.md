# Cross-artist credited-actor review

Two answer-blind agent reviews and separate adjudication now cover **55 questions
across 82 recordings and 66 artists**. Adjudication supports 54 same-actor links
and leaves one question unresolved. The positive links cover ten related-actor
hypotheses. These correlated questions expand development evidence for taper and
transferrer matching; they do not establish catalog-wide accuracy.

The [packet receipt](../../scale/evidence/people-identity-adjudication-packet-v1.json)
pins the original source-only questions, complete contexts and builder. The
[review receipt](../../scale/evidence/people-identity-adjudication-v1.json) pins
separate first answers, adjudication and final validation. The original packet
remains unanswered and unchanged; answer files remain outside it.

## What the reviews establish

The packet covers 11 hypotheses and 80 known recording/duplicate groups. Reviewers
independently read source contexts and froze their first answers. A third agent
then examined all 55 questions, including agreements. The first reviews disagree
on two questions; adjudication resolves both from retained credit and platform
context. A request to email an attachment to Paul McLendon remains insufficient
evidence of the recording's credited actor.

Repeated names, contacts and profiles select candidates. A matching contact alone
does not establish a person, and different wording does not establish two people.
Keep a person, studio, project and shared account distinct. Keep taper and
transferrer roles local to each recording. A supported same-actor link can coexist
with unresolved person/group kind. No canonical kind or identity is created here.

The separate [control packet](../../../../../tools/RelistenNormalization/people-identity-controls/README.md)
contains six questions from four complete source records and four artists.
Independent first reviews and adjudication agree on two same-actor and four
different-actor answers. Two negatives concern co-credited actors in one recording;
only two are cross-recording negatives. These controls provide direct distinctness
evidence, but do not supply a representative negative population.

## Verification and ownership

The [packet tool](../../../../../tools/RelistenNormalization/people-identity-packet/README.md)
authenticates eligible inputs before decoding raw records and checks all 979
discovery spans. Its 17 tests pass. The control tool's 12 tests and the
[review validator's](../../../../../tools/RelistenNormalization/people-identity-reviews/README.md)
31 tests also pass. Actual build and validation commands complete for both packet
formats. Final review validation checks 919 exact citations for the 55 questions
and 125 for the six controls, preserving links to original first-answer bytes.

The validator checks coverage, exact quotations, declared reviewer exposure and
adjudication links. It cannot certify the judgments or independently authenticate
reviewer identity. Versioned adapters support original recording locators and
retained primary text without rewriting answers or weakening the requirement for
both recording sources on resolved cross-recording questions.

Frozen packets, reviews, source copies and validation outputs stay under ignored
`local-dev/normalization-data/2026-10-17-cross-artist/`. Full fields and contact
values do not belong in tracked receipts. Preserve the original people,
actor-fact and credit-extraction labels. A later training adapter must version the
new targets separately and derive features from source passages, not answers.

These contexts already informed development. The reviewers are Codex agents;
their separate reviews do not make the data an untouched release test or a human
approval. Use the [matching evaluation strategy](../../../../plans/active/2026-10-17-full-normalization/evaluation-strategy.md)
for broader identities, supported negatives and independent evaluation. No accepted
identity, automatic policy or production catalog data changes in this work.
