# Scale people matching by distinct facts

The people candidate reader now separates complete distinct matching facts from
bounded raw evidence examples. Repeated supporting accounts no longer consume
the distinct-fact budget. The previous v3 reader capped raw evidence at 5,000
contexts and 4 MiB, making the whole snapshot incomplete when either limit was
exceeded. Retrying unchanged input could not resolve that condition.

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

People candidate facts contain complete distinct matching facts: credited
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

The people-only candidate-facts version is v4; other domains keep v3.
New people policies must identify the new contract. Old attestations cannot
silently authorize it. No policy is active.

## Implementation and verification

The implementation reads active accounts sequentially, validates each
original payload hash, and accumulates distinct facts, complete owner stamps and
bounded witnesses. This preserves existing integrity checks without adding a
second SQL canonical-hashing protocol. Streaming avoids retaining every raw
account at once; it does not establish bounded total database bytes, work or
owner-stamp output. Measure those costs before rollout.

Focused verification passes 126 matching/policy cases and 152 database cases.
The database fixture reads 5,002 supporting accounts beyond the old count and
byte limits while preserving complete matching facts. Tests also cover unique
unwitnessed aliases/contacts, owner-only changes with an unchanged people epoch,
retraction and genuine semantic overflow. Small complete aggregates preserve
the raw projection's comparisons. Nonpeople version and serialization contracts
remain unchanged. These checks establish mechanics, not identity accuracy or
production throughput.

The [people actor contract](../../../plans/active/2026-10-17-full-normalization/people-actor-evidence.md)
defines the current reader. The [implementation receipt](evidence/people-candidate-aggregation-v1.json) retains the executed
source, binary and test-log hashes alongside the read-only volume probe.
