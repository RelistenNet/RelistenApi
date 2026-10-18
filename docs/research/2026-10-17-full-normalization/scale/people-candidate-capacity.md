# Scale people matching by distinct facts

The current candidate reader caps each person's raw actor evidence at 5,000
contexts and 4 MiB. Any omission marks the whole candidate snapshot incomplete,
so automatic matching stops. Retrying the same input at the same limits cannot
resolve that condition. Repeated supporting accounts also spend assertion
budgets before deduplication. These limits must change before automatic matching
can cover prolific contributors.

The [read-only capacity probe](evidence/people-candidate-capacity-v1.json) confirms
that the catalog contains credit metadata at this scale. One exact stored label
appears in 5,671 taper/transferrer field entries across 2,995 recordings and 489
artists; another appears 5,483 times. Credit fields contain a literal Charlie
Miller name reference on 6,788 recordings across 86 artists. Exact labels and name
references do not establish canonical identities. They can contain compound
credits, projects or namesakes. Raw field sizes likewise do not measure serialized
accepted actor facts. The probe supports a capacity test, not an identity decision
or a claim of production normalization failures.

## Revised contract

People candidate facts must contain complete distinct matching facts: credited
names and aliases, contact tuples, profile tuples including relationship, context
presence, attachment uncertainty and kind-conflict information. Keep canonical
kind, qualified identifiers and exact artist membership separately. Source kind
assertions cannot replace canonical kind.

Return bounded original evidence examples alongside those complete facts. Repeated
accounts must not exhaust a distinct-fact budget. Genuine overflow of semantic
sets or aggregate output remains explicit and blocks automatic acceptance. Record
total context count, witness count and witness truncation separately so operators
can distinguish missing matching facts from omitted repeated evidence.

Retain every active-context owner observation, the original snapshot clocks,
accepted-credit/support joins, redirects and correction/retraction behavior.
Bounding raw witnesses cannot discard freshness checks. The global people epoch
alone does not prove that owner-only editorial changes are covered.

The intended people-only candidate-facts version is v4; other domains keep v3.
New people policies must identify the new contract. Old attestations cannot
silently authorize it. No policy is active.

## Implementation and verification

The first implementation will read active accounts sequentially, validate each
original payload hash, and accumulate distinct facts, complete owner stamps and
bounded witnesses. This preserves existing integrity checks without adding a
second SQL canonical-hashing protocol. Streaming avoids retaining every raw
account at once; it does not establish bounded total database bytes, work or
owner-stamp output. Measure those costs before rollout.

Tests must cover repeated accounts beyond old count and byte bounds, a unique late
alias/contact/conflict, changes to an owner outside the witness prefix, retraction,
and genuine distinct-fact overflow. Compare a small complete aggregate with the
existing raw projection. Implementation and those verification results remain
pending; the [people actor contract](../../../plans/active/2026-10-17-full-normalization/people-actor-evidence.md)
still describes the current reader below its existing limits.
