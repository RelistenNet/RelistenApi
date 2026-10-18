# Native provider shadow waves, 2026-10-18

The [native file reader](http://alecs-macbook-pro.tail09dbf.ts.net:18771/?view=files)
serves the current retained run directly: 4,690 outcomes across seven domains for
670 recording responses: 300 Archive.org, 150 Phish.in and 220 Local directories.
It includes all 34,139 decoded file observations: 27,967 Archive.org, 3,112
Phish.in and 3,060 Local. These include provider attachments and files without
titles, so the inventory count is not a playable-track or identified-song count.
No parent or child processing result failed.

| Domain | Proposed results | Unresolved results | Not applicable |
| --- | ---: | ---: | ---: |
| People | 433 | 237 | 0 |
| Credits | 432 | 238 | 0 |
| Venues | 670 | 0 | 0 |
| File contents | 293 | 377 | 0 |
| Performances | 670 | 0 | 0 |
| Compositions | 0 | 0 | 670 |
| Setlists | 0 | 0 | 670 |

File parents retain every child even when one title needs review. The individual
file outcomes are 13,162 proposed, 20,726 unresolved and 251 not applicable.
Original decoded file objects and recording origins match the prior recording
accounts in their original order. File ordinals address observations; they do
not establish musical occurrences, audio boundaries or performance order.

Native people now include two original, scoped alias observations: Andrew S.
with “bazillion” and Pat Myers with “DATPAT,” both from Local captures. The
previous 799 role-bearing actors, 867 credit claims, all 670 performance proposal
payloads and all 3,060 Local media anchors remain unchanged. There are 801 actor
observations in total, not 801 accepted people. Alias-only actors do not create
recording roles or merge people. The [native people and credit guide](native-provider-credits.md)
explains evidence scopes and parser coverage; [native file contents](../../../../tools/RelistenNormalization/native-files.md)
explains title interpretation, catalog links and playback.

All 670 venue observations remain unaccepted. Their 503 name and 504 name/locality
browse hypotheses support retrieval without establishing premises, rooms or
relocations. People and credit observations also disable automatic acceptance.
Title proposals, equal names and browse keys do not establish canonical identity
or semantic accuracy.

## Input and execution

`shadow-native` reused the immutable original full input: 61,330 ordinary derived
rows containing 4,379 artists, 16,605 songs, 38,475 legacy setlists, 600 source/show
pairs, one joint Local root and 670 provider responses. All dictionary, source,
show and root context was observed. The recorded processing scope is
`provider_inputs_only`: only provider rows emitted results. The run needs no
separate context-output copy or provider-only projection. Its retained production
capture namespace is unchanged; the derived input makes no new snapshot claim.

Archive.org and Phish.in retain their historical catalog source/show bindings.
Local retains its exact root/directory account and media anchors. All 3,060 Local
file outcomes preserve the exact track, source-set and playback associations.
Archive.org and Phish.in file observations remain inspectable without guessed
legacy track UUIDs. Literal provider locators remain separate from catalog audio.
These bindings do not certify current provider freshness.

The bounded run and one resume completed in 16.09 seconds. SIGINT caught the child
running after its first completed file chunk, during finalization after all
61,330 inputs and 4,690 outcomes. The interrupted checkpoint retained those
counts; one resume with identical input and method pins completed with exactly
4,690 raw and logical results, seven per provider input. This verifies
finalization recovery, not recovery from a partially processed native prefix. A
separate focused partial-processing test was pending when this run was recorded.

Input and runtime hashes stayed unchanged. Optional model, global people,
composition and geography references were unset. No build, paid call, provider
fetch or database access was required. Compute artifacts used 10,930,846 new bytes
before reader indexing, below the 128 MiB cap. Minimum observed free space was
5,006,696,448 bytes, above the 4.5 GiB reserve. The runtime lease was released.

## Reader and evidence

The completed run is
`local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-v2/`.
Reader QA indexed its actual outputs directly: 4,690 parent outcomes and 34,139
children from 670 file parents, seven completed chunks, no pending or failed
chunks. At QA shutdown, the SQLite cache occupied 14,659,584 bytes; cutover
reused that cache. Private port 18772 API and 390-pixel browser checks covered file search,
late file titles, exact song/home filters, provider links and catalog audio.
The QA reader and guard were stopped cleanly; that port is closed.

The controlled cutover verified and stopped only the owned six-domain reader
PID 488. PID 6508 now serves the new run and the same completed cache on private
port 18771, with the full-file reader at port 18767 retained for catalog links.
Post-cutover HTTP checks confirmed 4,690 outcomes, 34,139 children, 670 file
parents and no index lag. A source/title query found “I Am the Walrus”; an exact
repertoire-key query included that child. Its detail retained the original parent
revision, file ordinal, native key, catalog track UUID and captured MP3 reference.
No audio was fetched. The old run, view and cache remain unchanged.

The compact execution evidence is in
`local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-execution-v2/`:
`request.json`, `result.json`, `native-domain-receipt.json` and
`reader-health.json`. The output manifest SHA-256 is `9576cc04a0ef9608b1fe06297b999e1a765e504792735b5643375b4c90ebeedb`.
The receipt checks all original revisions and artists, all seven outcomes per
input, the complete decoded file inventory, unchanged historical payloads and
scoped alias growth. It does not certify title interpretation or identity truth.

## Retained earlier waves

The original `2026-10-18-shadow-native-provider-wave-v1` produced 152,445 outcomes
from the same full input in 9.64 seconds. Its separate reader projection served
2,010 native results: 670 proposed performances and 1,340 not-applicable
composition/setlist results. The 150,435 context outputs stayed outside that
view. People, credits, venues and files were not standalone native domains yet.

The first `2026-10-18-shadow-native-domain-wave-v1` used a minimal 6,250-row input
without songs or legacy setlists. It produced 5,820 outcomes in 7.24 seconds;
its six-domain reader served 4,020 native results and excluded 1,800 context
outputs. That wave introduced native people, credits and venues. Pure role-free
aliases and standalone file contents were still absent. Its searches and source
links reached playable records through the full-file reader at port 18767.
Both earlier full runs, projections and execution receipts remain retained.
