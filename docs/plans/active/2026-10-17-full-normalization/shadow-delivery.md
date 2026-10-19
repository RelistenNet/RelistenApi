# Deliver a usable shadow catalog across all imports

## Purpose and current decision

Build something Alec can browse and exercise: proposed normalized venues, songs/compositions, credited people, performances, setlists and playable file relationships across the catalog. The first completed run processes every raw record in the retained sample covering 4,379 artists. The full raw capture is now complete: 292,523 recordings and 5,064,097 files across 118 shards. Each shard has its own read-only snapshot; these counts are not one simultaneous historical snapshot. Preserve the exact manifests and reconcile different snapshots. All seven configured importers and Archive intake/collections must appear in the provider ledger, with applicability and available evidence stated.

A shadow catalog stores proposed results in separate local artifacts or a disposable database. It activates no production acceptance policy. This permits complete computation and inspection while publication performance and release accuracy work continue. Missing source facts remain unresolved; a missing implementation is an engineering gap to finish.

This execution sequence supersedes the evaluation-first milestone. The [standing goal](goal.md) preserves the outcome and [implementation/writing standards](goal.md#implementation-and-writing-standards) across turns. Existing domain contracts remain the basis for accepted production data. Use those contracts in shadow computation where practical without requiring production authority or a complete publication lifecycle to inspect proposed decisions.

## Progress

- [x] 2026-10-18: Alec directed full-catalog shadow delivery, parallel domain integration, larger model jobs and consolidated testing; the plan and standing objective are amended.
- [x] Complete the retained-sample pass and retain its command, input hashes, method versions and output location.
- [x] Expose the eight integrated domains in a reader with proposals, native evidence, related records and playback links.
- [x] Capture the full recording/file population and reconcile it against the earlier inventory.
- [x] Compute outcomes for every captured file with the complete retained song/setlist dictionaries and frozen provider context.
- [x] Independently audit every captured file's ownership, playback, literal evidence and sibling domain results; reconcile the complete outcome inventory.
- [x] Correct the empty-title matching failure in all affected file results without changing playback or valid neighboring mentions.
- [x] Finish full-file reader indexing with no pending or failed chunks.
- [ ] Integrate the revised native credit/alias generation into every shared projection.
- [x] Rehearse a changed file title, compute interruption/resume and reader restart locally.
- [ ] Extend refresh/recovery qualification to the full catalog, then prepare production acceptance and backfill.

## Current results and remaining work

The full raw capture is complete: 292,523 recordings and 5,064,097 files across
118 shards. Each shard has its own read-only snapshot. Reconciliation found no
missing expected rows or UUID/parent-coordinate drift; 21 files were added after
the earlier metadata inventory. Keep both inventories and their capture times.

The [recording/venue reader](http://alecs-macbook-pro.tail09dbf.ts.net:18765/)
contains the completed core pass: 292,523 recordings, 216,332 venues, 16,605
repertoire songs and 38,475 legacy setlists, plus one ordered provider input.
It exposes 1,242,539 outcomes with no processing failures. These outcomes include
proposals and unresolved cases; row processing does not establish identity
accuracy. The reader reports index lag separately from runner progress.

The corrected full-file producer completed all 5,064,097 files, with all 16,605
songs, 38,475 legacy setlists and 1,861 frozen provider inputs. It wrote
20,410,606 domain outcomes with zero failed outcomes in 606 seconds. Independent
manifest and filesystem checks verified all 105 selected capture entries and
footers, exact population counts, and 10,622 completed gzip chunks. The
[full-file producer receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-full-files-producer-v3.json)
pins the selector, command, assemblies, output manifest and storage measurements.
The independent contract audit checked every file in all four file domains
and all 575,103 stored memberships. It accounted for all 1,861 provider inputs
and reconciled 20,410,606 outcomes without a coordinate or sibling-result
mismatch. It also verified 36,319,601 catalog literal/span citation instances.
The 157,656 files without captured source homes retain explicit null
coordinates. These checks establish preservation, not identity accuracy.
The [full-file reader](http://alecs-macbook-pro.tail09dbf.ts.net:18767/?domain=file_contents)
is complete: all 20,410,606 outcomes and 5,064,097 file-content results are indexed
across 10,626 immutable chunks, with no pending or failed chunks. This count
includes the four correction chunks in the browsing view. Its coverage panel
still distinguishes computation from indexing. Known semantic issues remain
visible; provider coverage and identity accuracy remain open.
A full-size restart exposed a coverage-query timeout.
Coverage now uses counts committed with each chunk; the existing-cache migration
took 20 seconds and the actual HTTP stats request returned in 0.58 seconds.

The completed run used repertoire v5. A title audit found a trailing `#` matched
through a blank dictionary entry, producing a false proposal. An exhaustive
repertoire scan identified 1,619 affected files across 34 artists; no provider
input has this defect. The v6 correction passed 173 focused repertoire,
composition and performance tests. A targeted replay uses the original file
records and complete dictionary/provider evidence. The checked correction
contains 6,476 outcomes: the same 1,619 files in four domains. It preserves every
file's ownership, playback, title, extraction spans, valid neighboring song/work
links and performance result. Repertoire and file-content each change 1,357
proposals to unresolved; previously unresolved results remain unresolved.
The browsing view retains the immutable v5 base and identifies the filtered v6
correction separately. No dictionary or provider context result from the smaller
replay replaces a full-population result.
The [integrated delivery receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-full-file-delivery-v1.json)
links the complete audit, affected partition, checked correction and public
reader contracts.

### Cross-artist taper and transferrer lookup

The [people directory](http://alecs-macbook-pro.tail09dbf.ts.net:18777/) now opens shared person pages with names, aliases, recordings, artist and role filters, original credit quotes and playback links. [Tonedeaf](http://alecs-macbook-pro.tail09dbf.ts.net:18777/?person=shared-person%3Apeople_facts%3A9de2bd6e8a4f53298d78c7f3c56299b9) combines 2,370 recordings across 403 artists; [Timothy Brown](http://alecs-macbook-pro.tail09dbf.ts.net:18777/?person=shared-person%3Apeople_facts%3Ad40f1b4fab9c7492c3cd9146fbd5fcc5) combines 424 across 105. Direct support covers 18,120 recordings across 1,536 artists. The complete source pass supplies the memberships through the shared C# matching policy and exact original credit IDs. Earlier context-only pages remain addressable. Each recording retains its own roles, source and playable files. See the [shared people interface](../../../../tools/RelistenNormalization/shared-people-directory.md) for data ownership and refresh behavior.

The directory contains 1,472 shared person hypotheses from surviving contextual comparisons. Membership requires direct support against an anchor credit. Namesakes, conflicting evidence and overlapping alternatives remain distinguishable; neither matching names nor transitive chains add recordings. Raw name groups remain searchable for observations without a shared assignment. The next person pass will use the existing C# domain policy to include all directly supported occurrences, rather than only the selected contextual cohort.

The refreshed source generation contains 638,887 credits across all 292,523 captured source homes. It preserves 571,458 exact old credits, inserts 67,429 and retires 23,367 changed credits. All 2,296 retained Luna credit mentions remain. Another 408 source actor observations on 406 recordings across 143 artists retain explicit aliases without inventing recording roles.

Of the original 5,000 contextual questions, 4,224 decisions still match their exact credit witnesses: 2,026 same-person, 158 different-person and 2,040 insufficient-evidence proposals. Changed credits invalidate 453 earlier comparisons; another 323 were missing or rejected. The original questions, responses and costs remain retained. Combined known token cost is $1.3464393; the ledger retains $1.3971328 including uncertain-call reservations. These are estimates at configured rates, not invoices.

The native-v3 overlay supplies 882 actor observations, 1,007 role mentions and three role-free aliases across 670 provider inputs. These counts can overlap catalog observations and must not be added as unique people or recordings. The [native reader](http://alecs-macbook-pro.tail09dbf.ts.net:18774/) retains the original scopes, 34,139 files and 3,060 Local playback anchors. “Fil Pate” now has `Other / engineered by`, preserving the engineering wording. Earlier readers remain available for their historical evidence locators.

See the [directory interface and operations](../../../../tools/RelistenNormalization/people-directory.md) and [continuation contract](../../../../tools/RelistenNormalization/people-directory.md#pending-only-continuation).

### Shared venue and music lookup

[Search MSG](http://alecs-macbook-pro.tail09dbf.ts.net:18776/?q=MSG) to open **Madison Square Garden, New York City, New York**. Its page combines 923 captured recordings and 199 show UUIDs across 63 retained venue records and 33 artists. Artist filters reach 96 Phish recordings or 712 Grateful Dead recordings. Original source, show and file ownership remains intact. The original MSG UUID still opens its own raw `MSG / NY, NY` account; normalized result links select the shared recording list.

The venue directory covers all 216,332 captured venue rows and 292,523 recording/show homes. The current method groups 100,078 venue members into 35,271 named-place hypotheses using resolved localities, observed names, initials and captured source/show associations. Conflicting addresses, distinct rooms and unresolved names remain separate and browsable. Historical physical continuity is not yet verified; observed recording dates are not opening, closing or name-validity dates. Another 670 native venue accounts retain their original scopes and links to 34,139 files, without a global native-place assignment.

Venue search reuses matching alias groups when counting and listing results.
On the current read-only projections, searching `MSG` fell from 9.18 seconds to
0.18 seconds with the same 13 results. Artist filters still select an original
venue UUID, and an empty page retains the full result count. These measurements
describe this captured catalog on the local host; they are not a production
latency guarantee. Search materializes only matching identities and sort fields,
then loads full records for the requested page.

Venue and taper recording cards now open their tracks and play audio inline.
Shared navigation connects all discovery pages. A 390-pixel browser check
followed MSG to the Grateful Dead's 1994-10-19 recording and played “Brokedown
Palace”; playback advanced without a media error. Some original audio URLs return
upstream errors. The page explains the failure and retains the source link;
provider refresh owns URL corrections. See the [venue interface and
operations](venue-directory.md#inline-recording-playback).

[Music discovery](http://alecs-macbook-pro.tail09dbf.ts.net:18769/) now includes all 963,490 current full-reader repertoire keys, for 971,127 scoped entries in total. It preserves every earlier song, artist, setlist and work membership. Catalog, Local and native scopes stay separate. Thirty-three supported work proposals have 41 work/song links; 38,517 setlist accounts retain their existing ordered or unresolved structure. Forty-three repertoire references have no retained title.

The full C# interpretation now connects title variants to shared artist-song pages. [Scarlet Begonias](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#song-group?scope=catalogue&artist=77a58ff9-2e01-c59c-b8eb-cff106049b72&key=repertoire%3Af6d191c085ac0d91475fa88f2f434707) brings together 61 original entries, including `09.Scarlet Begonias`, and reaches 329 catalog shows, 29 unbound recording accounts and 3,192 distinct matching files. `Scarlet Begonias – Fire on the Mountain` retains both song links. The phone journey reaches an original Archive MP3; the service returned audio for a bounded one-byte request. These are title-based proposals and retained catalog homes, not independent verification of the audio's song or performance date.

The same pass covers all 971,127 original scoped keys. Known dictionary titles take priority; inferred compounds, jams, teases, reprises and partial interpretations retain their labels. Review alternatives stay accessible without making every rival a file's default song. File results preserve original ownership and group performances only by actual show UUID. The first screen prompts for a title or artist rather than fetching the million-account list. See the [consolidated music interface](../../../../tools/RelistenNormalization/music-consolidation.md) and [original music interfaces](../../../../tools/RelistenNormalization/music-discovery.md).

### Composition discovery across artists

[Possible versions](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#compositions)
now connects retained works to artist-song pages and their original recordings.
Search by title, alias, writer or work ID. The current projection includes all
1,024 qualified retained works, 3,034 candidate song groups and 27,555 alternative
relationships. It preserves the 33 earlier supported work pages and their routes.

[Crazy Fingers](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#possible-version?key=composition%3Amusicbrainz%3A8973193c-be9e-450d-9e26-2cced570c5aa)
shows the Garcia/Hunter work separately from Alan Hawkshaw's work. A 390-pixel
browser journey followed the Garcia/Hunter page to Alex Jordan, two distinct show
UUIDs, the 2023-07-25 recording and its original “Crazy Fingers >” Archive MP3.
These song-to-work relationships remain unconfirmed title or alias matches.
Model suggestions appear beside the processed songs; every alternative stays
accessible.

The C# pass covers every one of the 896,216 proposed artist-song targets. It
explicitly records 893,182 targets without candidates in the supplied work pool.
The browse projection uses only 15.6 MB beside the unchanged song catalog and
does not copy recording files. This generation uses the earlier retained provider
pool; newly acquired work searches, including Watchtower, need a subsequent C#
pass before they appear here. Broader lookup stopped at the host disk reserve and
retained its completed inputs. See the [composition interface and
operations](../../../../tools/RelistenNormalization/composition-discovery.md).

The composition selector can process all 3,034 candidate-bearing targets with
four concurrent Luna requests, durable cache recovery and explicit spending
bounds. An optional reader projection can display each model choice, reason and
alternatives without changing title matches or accepted identities. The full
selection run started on 2026-10-19 at 01:30 UTC with a $100 job cap and a
two-hour duration. It reserves space before each batch and stops near the
5.5 GiB disk reserve; completed responses survive interruption. Whole-run cache
growth does not need to fit upfront. Its checkpoint is in
`local-dev/normalization-data/2026-10-18-composition-selection-full-output-v1/`.
The live reader shows the first 100 completed song results: 82 selected work
hypotheses and 18 insufficient-evidence results, alongside all original title
candidates. This is 100 of the 3,034 candidate-bearing song groups, not a measure
of accuracy. The small completed projection reuses cached responses without
another paid request while the full run continues. For example,
[Furthur's Crazy Fingers](http://alecs-macbook-pro.tail09dbf.ts.net:18769/#song-group?scope=catalogue&artist=04735701-ff23-9ec4-93fa-f503246310ec&key=repertoire%3Ac48ac0a67fbe6098db592357bb5ab04d)
shows a proposed Garcia/Hunter match, recording context, the model's reason and
the separate Alan Hawkshaw alternative. No model choice changes an accepted
identity or recording assignment.

An independent read of the first 40 results found useful abstentions and
namesake distinctions, together with unsupported cover and genre assumptions.
Some rationales use an unrelated work's recording count or ISWC as though it
established the target song's identity. Keep target-specific evidence separate
from background knowledge when evaluating these choices. This nonrandom prefix
does not estimate accuracy or establish a wrong-match label. The pinned cases
are retained in the execution directory's `semantic-audit.json`; independent
target credits or listening are the next adjudication step.

The single largest-request pilot
established transport and cost behavior, but its unsupported cover assumption
remains a [candidate-coverage review case](../../../../tools/RelistenNormalization/composition-hypotheses.md#select-a-likely-work).
Inspect the broad results before qualifying automatic acceptance.

Dictionary citation compaction retains one actual witness per distinct literal
spelling while keeping every membership in its original setlist result. The
earlier 50,000-file replay passed independent ownership, title and citation
checks with all 575,103 memberships intact. See the
[dictionary-context receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-dictionary-context-v1.json).

### Jerry and Aadam Jacobs

The [Jerry/Aadam reader](http://alecs-macbook-pro.tail09dbf.ts.net:18766/)
processes 701 local recordings and 7,826 files in a separate catalog namespace.
It includes 348 recordings from six Jerry projects and 299 selected Aadam Jacobs
recordings across 252 artists. All 39,906 outcomes are indexed, including 5,583
separate provider-context results. The independent audit verified every file's
playback and ownership coordinates, all 236 dictionary songs and 404 unordered memberships. Local numeric IDs must never
be combined with production IDs.

The audit exposed unsupported title-derived identities. The corrected run
removes 211 such file proposals while retaining known songs, supported musical
segments and original audio. File cards show their literal titles. The one
ordered Jerry provider page concerns a Grateful Dead performance; it does not
establish ordered setlist coverage for the Jerry projects or Aadam collection.
The [integration receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-native-local-integration-v1.json)
records exact inputs, method versions, tests, audits and limits.

### Matching and provider evidence

The [native-provider reader](http://alecs-macbook-pro.tail09dbf.ts.net:18771/?domain=performances)
now includes 300 Archive, 150 Phish and 220 Local retained inputs. All 670
performance results bind to their existing recording/show homes. Local's
220 directories resolve to 150 recordings through 3,060 exact media URL anchors;
the root and directory revisions remain separate, jointly required evidence.
The current native-only run observes the full retained song/setlist dictionaries
and source/show context, then emits only provider outcomes. It produces 4,690
outcomes across seven domains for the same 670 native inputs. All 670 historical
performance and credit payloads and all 3,060 Local media anchors remain unchanged.
The result retains 799 existing credit-bearing actor observations and 867 credit
claims. Two additional alias actors retain the explicit names Andrew S./bazillion
and Pat Myers/DATPAT with their original Local scope. They add no recording role,
attendance claim or accepted identity.

The [native file view](http://alecs-macbook-pro.tail09dbf.ts.net:18771/?view=files)
searches all 34,139 decoded files individually: 27,967 Archive files, 3,112 Phish
files and 3,060 Local files. These are provider file observations, including
untitled files and non-audio metadata. They are not distinct performances or a
count of playable catalog tracks. All original file objects and their recording
origins match the prior native accounts. The file results include 13,162 proposed,
20,726 unresolved and 251 not-applicable interpretations, with no failed files.

File title mentions use the same artist-scoped repertoire resolver as the catalog
and setlist paths. Known dictionary identities, provisional title-derived songs
and competing interpretations stay distinct. Original title citations retain
Unicode code-point offsets. File positions and title transitions supply no audio
times, ordered setlist occurrences or performance assignments.

Native aliases now retain assertions, scope coverage and unresolved wording from
recording/file prose and the selected Local root account. Native venues retain
identifiers, coordinate kinds, locality alternatives and history prose. Adjacent
Local accounts cannot contribute facts. The shared people, venue and music pages
now include these native observations while retaining separate namespaces,
counts and evidence. Equal names do not create canonical identity merges, and
native UUID anchors never route into a different captured catalog.

The reader adds a compact child-file index while retaining full bodies in the
original provider chunks. Its complete cache is 14,659,584 bytes, including the
4,690 parent outcomes and all 34,139 file pointers. Existing catalog file rows
have no duplicate child projection. Exact source, file and song-key filters work
alongside title/filename search. Only an exact catalog anchor supplies a catalog
MP3 control; an absolute native playback locator appears separately as a provider
file link. Missing bindings leave original files inspectable. No filename becomes
a guessed URL or catalog track UUID.

The actual SIGINT/resume check interrupted finalization after all input rows and
outcomes were written, then completed from the same pins. It does not establish
midstream native recovery. A separate focused test interrupts after the first
native input, resumes across a skipped dictionary row and compares the complete
result with an uninterrupted run. The native batch tests also verify complete
dictionary context, same-mode replay and changed-scope refusal.

The [native-wave guide](../../../research/2026-10-17-full-normalization/scale/native-provider-wave-v1.md)
records actual runs and coverage. The [native credit guide](../../../research/2026-10-17-full-normalization/scale/native-provider-credits.md)
defines people, aliases and credit scopes. The [native file contract](../../../../tools/RelistenNormalization/native-files.md)
defines file identity, interpretation and optional playback. The
[Local admission guide](../../../research/2026-10-17-full-normalization/scale/retained-local-shadow-inputs.md)
defines exact root selection and catalog binding.

The people adapter now retains direct source-anchor identity hypotheses with
exact observed membership counts and bounded citations. It can also compare
against byte-pinned canonical candidate snapshots when those are supplied.
The directory above provides the cross-artist browsing path. Full identity
grouping still needs to connect fragmented names and distinguish namesakes;
source-witness rankings alone do not finish that feature.
The [people-bridge receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-people-identity-bridge-v1.json)
records the implementation, checks and limits. Accepted-candidate coverage and
independent identity evaluation remain necessary before automatic acceptance.
Physical-venue and composition rules are implemented and tested; broader
location/history evidence and independent qualification remain open.

The MusicBrainz acquisition stopped at its original 45-minute deadline. Its
plan contains 33,496 commands and remains incomplete. The 1,860 retained responses
are frozen in provider wave v2 alongside the separately identified original Jerry
page. They include 1,318 recording lookups and 126
work lookups. The retained recording relationships name 127 work IDs; one target
has no full lookup. Search results and unhydrated rivals remain evidence, without
an accepted catalog assignment. Later responses require a new replay selection.
That follow-on closed at its original deadline with 3,073 retained inputs.
A new bounded acquisition reuses completed captures and durable resource history
before issuing new requests. Two subsequent attempts retained 20 more responses,
for 3,093 total, then closed at the disk reserve. Acquisition remains incomplete;
the deadline-limited attempts stay closed. Its request retains a 2,000-invocation,
45-minute limit and a 128 MiB actual retained-payload limit. The separate 16,777,216,000-byte invocation
allowance reserves a conservative 8 MiB per attempted call; it is not a disk
allocation. The monitor stops at 4.6 GiB free space. New captures remain separate
from the frozen provider wave until a new replay selection admits them.

The newer cached-composition replay admits all 3,093 retained MusicBrainz inputs
without another provider or model call. It preserves the original revisions,
payload hashes and primary proof packet. All 575,103 embedded song memberships
in the captured sets exactly match the complete 16,605-song dictionary, so the
existing song-shard command supplies the same repertoire context without
re-emitting setlists. The replay produced 54,861 outcomes and 32 linked song
rows across 15 artists, naming 31 distinct works. These remain supported
composition proposals, not accepted identities. There are 125 song results
with artist-repertoire ambiguity.

The [cached-composition reader](http://alecs-macbook-pro.tail09dbf.ts.net:18773/?domain=compositions)
indexes the complete replay. Music discovery incorporates its supported links
alongside the earlier source contributions: 31 works and 35 scoped work/song
associations. “Crazy Fingers” links the separate Grateful Dead and Dark Star
Orchestra repertoire identities through the same cited work. Source-specific
relationships remain inspectable; a local Grateful Dead identity retains its
own namespace. The larger external acquisition plan remains incomplete.

The [provider-wave receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-provider-wave-v2.json)
pins the exact frozen manifest and input bytes. The phone reader now includes
this wave. The new replay changed 11 catalog composition results from unresolved
to proposed for two Grateful Dead works, “Crazy Fingers” and “Queen Jane
Approximately.” The independent audit traced every new catalog link through its
performer, recording and work evidence. Every file retained its original title,
ownership and playback coordinates. See the
[replay receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-local-provider-wave-v2-replay-v1.json).

A separate 2,000-record Luna wave completed in 425 seconds with $0.278385
accounted in its ledger: 1,952 validated extraction results and 48 unresolved
responses. The [sample reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/)
contains those model interpretations and 2,296 positive credit mentions.
See the [core/model receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-core-and-luna2000-v1.json).
Extraction proposals still require identity matching and accuracy evaluation.

The latest consolidated normalization checks passed 1,186 tests with 39
environment-dependent skips; 97 database integration tests passed separately.
The later title/ordinary-row checks passed 163 tests. The current file reader
passes 33 behavioral tests. Native aliases, files, binding and batch behavior
passed 129 focused checks. The later native-file suite passed 11 cases, including
partial checkpoint recovery; contextual people changes passed 21 C# and 16 Python
checks. HTTP checks follow real native credits and venues through their exact catalog homes
to playable files. These suites overlap; their counts are not a distinct-test total. The
[integration receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-native-local-integration-v1.json)
pins the exact reports and source versions.

### Changed input and restart

A local rehearsal interrupted and resumed normalization over 250 retained files
with the complete song/setlist context. It reconciled 149,635 outcomes. A
simulated title correction changed only that file's repertoire, composition and
file-content results. Its performance result, ownership and MP3 URL stayed the
same; every unrelated outcome remained identical. Two actual reader processes
served the corrected file result from the existing cache, with its stale song
membership removed. The simulated title is test input, not a correction asserted
about the audio.

Changed bytes cannot resume the original run. The rehearsal found that this
expected refusal escaped the CLI's error handler; the tool now returns exit 1
with the input-mismatch message and preserves previous output. Seventeen batch
and inline contract tests pass. The
[refresh rehearsal receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-refresh-rehearsal-v1.json)
records the actual process and HTTP checks. Full-catalog automatic refresh and
production recovery still need qualification.

### Storage and next actions

The approved cleanup of 21 temporary build folders recovered 1.61 MiB of actual
host capacity. A separate approved failed-output cleanup recovered 8.46 GiB
while preserving retained raw data in its verified gzip archive and keeping
the existing readers available. The full-file producer then wrote 3,499,102,132 compressed bytes and
finished with 9,104,211,968 bytes free, above its 4 GiB reserve. These are actual
host measurements in the full-file producer receipt. A measured reader
benchmark projects 4.00–4.12 GB for the file population and song/setlist context;
that range is not a final capacity measurement. The full index uses
a separate view that hard-links every immutable base chunk and the four
corrected chunks. Additional host capacity is now available: the full-file
reader resumed with about 14 GiB free and completed its existing cache. It uses
a 5 GiB reserve plus 128 MiB for a chunk write. The complete music-title build
uses a separate 5.5 GiB reserve and a measured 1 GiB replacement cap. These limits leave
room for the smaller context jobs. Indexing pauses at the reserve while existing
results remain browsable. Measure current free space before each new job.

Keep the current results available while the complete title projection builds,
then continue distinct-person
resolution, physical-location evidence, composition investigation and native
provider coverage. Extend the bounded refresh rehearsal to full-catalog automatic
change processing and recovery.

The shared byline and actor-attachment grammar is revised. The existing directory
retains credit grammar v4 and source-alias grammar v3; the replacement must apply
credit v5 and source-alias v4 across all 292,523 captured recordings. It reuses
the 2,000 authenticated cached Luna inputs without new API requests.

The first refresh exposed a responsibility problem in the generic people adapter:
it repeated cross-person comparison diagnostics for every recording. One source
emitted 466 KB of relationship reports beside 558 bytes of actor observations.
The job stopped cleanly with 61,171 sources checkpointed and 1.95 GB retained;
no directory was published. Export source observations separately from identity
comparison, under an explicit method and processing scope. Reuse the same credit,
alias and actor extraction policy. Preserve the existing contextual comparisons
and the default matching behavior; this extraction phase must report matching as
not run rather than an empty completed match result.
Rerunning the old grammar would retain names polluted by action phrases, contacts
and surrounding prose. Overlapping candidate scans identify 4,218 recordings
across 658 artists for these classes; these are investigation counts, not an
estimated error rate. Preserve original wording and spans, legitimate handles
and ambiguous attachments. Refresh into a separate generation after behavioral
verification, including role-free actor observations. Keep the current contextual
cohort frozen; changed or split actors require new scoped comparisons.
The revised grammar is committed at `be7bde8`; 259 focused extraction and
compatibility tests and 29 public publication/recovery tests pass. Historical
accepted reads retain exact v2/v3 alias grammar and known decoder profiles;
new publication requires current evidence. The next source refresh must retain
the old directory and reuse comparison witnesses only when their complete
evidence is unchanged.
The new observation-only pass is running over all seven captured source shards.
It computes actor, credit and performance observations without building global
person comparisons. Completion, cached-input replay and decoded chunk bounds
must pass before creating a replacement directory. Existing readers stay live.
Automatic production acceptance, full backfill and API compatibility still
require independent qualification. Earlier bounded checks remain in the
[inline range receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-inline-first-range-v1.json)
and [relationship-enrichment receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-relationship-enrichment-v1.json).

## Reproduce the earlier sample

Open the [earlier sample reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/).
The retained run at `local-dev/normalization-data/2026-10-18-shadow-catalog-v1/`
contains **258,987 domain outcomes and zero failures**. It processes all 32,593
recordings, 34,861 files, 17,073 venues, 1,696 repertoire songs and 432 legacy
setlists in the raw sample, plus one retained Jerry Garcia provider input.
An input can contribute several domain outcomes. Counts do not represent distinct
canonical identities or completion of the full catalog. The checked-in
[first-run receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-catalog-first-run-v1.json)
retains the run's population and verification summary.

From the repository root, reproduce the input/evidence settings in a new output
directory:

```sh
RELISTEN_SHADOW_PROVIDER_INPUTS="$PWD/local-dev/normalization-data/2026-10-18-shadow-provider-inputs-v1/inputs.jsonl" \
RELISTEN_SHADOW_COMPOSITION_EVIDENCE="$PWD/local-dev/normalization-data/2026-10-18-composition120-primary-adjudication-v1/manifest.json#sha256=c27c3135c51ff3364bf18425e435ce2ae46cbd8e08eb561e5b5c951ee023799f" \
dotnet run --project tools/RelistenNormalization -- shadow \
  local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl \
  local-dev/normalization-data/2026-10-18-shadow-catalog-extraction-v1/extractions.jsonl \
  local-dev/normalization-data/shadow-catalog-replay
```

The run manifest stores the snapshot/extraction/provider input hashes, adapter
versions and provider/domain accounting. The execution request at
`local-dev/normalization-data/2026-10-18-shadow-catalog-execution-v1/request.json`
records the actual command, assembly hashes and evidence settings. Computation
uses retained data and local code, with no database or paid-model calls.

Completed per-domain gzip chunks are immutable. Checkpoints advance after chunks
are flushed; `--resume` requires the same input hashes and method versions. A
replayed prefix can remain on disk, but the latest `(domain, inputKey)` result
defines the logical outcome. The reader ignores unfinished `.writing` files. Its
rebuildable SQLite index holds summaries and chunk/line pointers; full proposal
and evidence bodies remain in their original chunks. See the [tool guide](../../../../tools/RelistenNormalization/README.md#browse-and-reproduce-the-shadow-catalog)
for reader startup and HTTP routes.

Use the reader's filters and detail view to inspect these relationships:

- `venues`: open [Bowery Ballroom across White Denim and Ted Leo and the Pharmacists](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=venues&entityKey=venue%3A8cd9a5eaf90ebe8add9f89d95c6e47db)
  and inspect the shared `venue:` key.
  Proposed metadata and shared references still require physical-premises,
  locality and historical-interval resolution.
- `repertoire`: follow a song's keys into `file_contents` and `performances` to
  inspect its catalog home and available playback URL.
- `people` and `credits`: search a name and inspect its original taper/transferrer
  evidence and competing identity proposals.
- `setlists`: open [Jerry Garcia's ordered account](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=setlists&provider=jerrygarcia.com)
  for three ordered sets and 18 passages.
  The 432 legacy setlists retain unresolved ordering.
- `compositions`: open the [three cited proposals](http://alecs-macbook-pro.tail09dbf.ts.net:18764/?domain=compositions&outcome=proposed). Broader
  cross-artist composition matching remains incomplete.

The coverage panel separates runner progress from indexed visibility. Provider
labels preserve unattributed catalog rows and playback hosts; configured artist
providers alone do not establish importer provenance. Full-file computation and
the preservation audit are complete. Corrected indexed visibility, remaining
provider/domain evidence and independent identity evaluation are still open.

## Execution and ownership

The main thread owns integration, the shared output contract, run accounting and the reader. Assign independent agents these four responsibilities: credits/global people; physical venues/locality/history; repertoire/cross-artist compositions; performances/setlists/file contents. Give each agent an explicit file boundary and require runnable outputs, observed coverage and unresolved implementation gaps. Cross-domain identity and playback assumptions must agree at integration. Production publication cost can have separate ownership without taking over the main sequence.

Use retained catalog captures and provider inputs immediately. Do not reacquire the whole catalog or start provider imports just to run normalization. Discover and reuse existing entrypoints in `tools/RelistenNormalization/Program.cs` and the services under `RelistenApi/Services/Normalization/`. Add the smallest streaming shadow entrypoint where current commands only export candidate pairs. Record the exact executable command in this document and run it; a command specification alone is not delivery. Keep SQL acquisition outside pure resolvers and external requests outside database transactions.

Start deterministic extraction and indexed retrieval across the complete input population now. Stream partitions by provider/artist/domain and resume from durable local progress. Do not construct an all-pairs comparison or hold five million file outputs in memory. A slow or failed partition records its outcome and does not stop independent domains. Use current working methods; add missing adapters while already supported domains run. Preserve independently selected audit families and exposure records before examining their results.

Run model-assisted processing in parallel bounded jobs sized from observed throughput, upstream limits, context size, memory and cost. Plan for thousands of applicable decisions per wave when the measured envelope supports it; a 100/600-case pilot is not a project limit. Deduplicate identical requests, reuse versioned caches and avoid paying per duplicate file when shared evidence suffices. Each job records its spend ceiling, model/prompt, concurrency, attempt limits and usage. Existing authorized spending applies; existing jobs retain their original caps and follow-on jobs get new manifests. If a stronger available model addresses a demonstrated reasoning gap, compare it on that gap rather than assuming the smallest model must handle every task.

Use rules where they work and models where they add value. Do not spend another cycle training a local classifier merely to satisfy a checklist. Preserve exact source citations, conflicting candidates and original IDs in every proposed relationship. A model can interpret evidence; it cannot create absent writer credits, dates, physical premises, song boundaries or setlist order.

## Output and reader contract

Reuse existing typed proposals/results and domain read models. The shadow adapter adds only run accounting and the projection needed for inspection; do not create a second generic normalization framework. At minimum, the retained result identifies the run/snapshot and method versions, original input/provider/domain, outcome, proposed entities/relations, evidence references, alternatives and unresolved/error reason. Use outcome distinctions equivalent to proposed, unresolved, failed and not applicable. An unimplemented adapter is reported as an implementation failure, never as a successful unresolved inference.

Write a compact run manifest and indexed per-domain results outside Git, under the existing ignored normalization-data area. Keep plans and readable reports in the repository's existing `docs/` folders, linked to this plan; do not create a `personal/alecgorge/` tree. Keep private source prose local. The manifest reconciles eligible, processed, proposed, unresolved and failed inputs per provider/domain and records duplicates, skipped/inapplicable rows and remaining work. Preserve stable shadow references so audits and reruns can identify the same proposed relationship without changing legacy UUIDs. This is an adapter output contract to implement, not a new production schema requirement.

Build a lightweight local reader over those actual outputs using existing UI/API patterns where possible. A static HTML/JSON/SQLite-backed reader is sufficient. Provide search/filter by provider, artist, domain and outcome; show the normalized proposal beside its original evidence and alternatives. Link source/show/file identities and existing playback references. Report independent identity counts separately from rows and repeated recordings. Show growing results before the full run finishes. No standalone website deployment, production policy or new editorial system is needed.

The reader must demonstrate cross-artist venue lookup, song-to-show/file lookup, composition relationships and taper/transferrer lookup, plus supported ordered setlists and unresolved cases. Show known difficult examples and ordinary sparse artists. A candidate-list browser alone does not demonstrate resolved relationships.

## Audit while delivering

Independently inspect stratified samples of proposals, negative decisions, unknowns and failures from actual outputs. Include each applicable provider/domain, small artists, different formats/geographies, namesakes, historical premises, repeated songs, compound files and conflicting source accounts. Use the [coverage ledger](evaluation-milestone.md#coverage-ledger) to find gaps. Obtain supporting source evidence where it can resolve uncertainty. Separate inaccessible evidence from investigation not yet done.

Measure candidate retrieval, conditional matching, final end-to-end relationships, unresolved/error rates and cost/throughput with their own denominators. Preserve untouched audit samples for release decisions; exposed development audits guide fixes. Derive sample sizes from observed failure rates, independent families and the decision being made. Do not wait to pre-label the population or finish every disagreement before running independent partitions. Investigate common errors, fix their shared cause and replay affected partitions rather than repeatedly tuning individual cases.

## Verification cadence

For a meaningful code change, start with the fastest behavioral check through a stable public contract. Add tests for the demonstrated failure class. Consolidate affected regression checks when a domain is integrated and run the broad suite at shared integration checkpoints and before rollout qualification. Use `./test-local.sh --filter ...` for local behavioral checks and `dotnet build RelistenApi.sln` for compilation where relevant. Run `dotnet csharpier check .` before every commit/push, as required by repository policy.

Do not rerun an unchanged failed command or repeat broad suites for documentation or unrelated code. Do not build a fresh full checkout and binary archive for every tiny edit when an existing frozen run plus source revision proves the needed boundary. Keep existing evidence, but add new provenance machinery only for a concrete reproducibility need. Historical compatibility and stress checks remain required for affected production changes; isolate them so they do not block shadow extraction in unrelated domains.

## Validation and acceptance

The next substantial update must include the exact runnable command and run location, a reader Alec can open, actual processed/remaining counts by provider/domain, representative working normalized relationships and the largest observed failure classes. If the full pass is running, show its real outputs and progress rather than waiting to finish another labeling packet.

Complete the shadow milestone only when input/outcome accounting reconciles the chosen complete snapshot; every applicable provider/domain has a working adapter; the reader shows actual integrated relationships and evidence; independent audits and fixes cover observed systemic failures; and a local restart plus changed-input replay preserves source/playback references. No domain is complete when its outputs are only errors, placeholders, candidates or blanket abstention. No minimum invented resolution percentage is imposed: report measured coverage, and continue fixing missing implementations and resolvable evidence gaps.

This milestone does not finish the overall goal. Use independent accuracy evidence to qualify production acceptance and routing, resolve publication performance, rehearse full backfill and API compatibility in a restored catalog, then prepare rollout for approval. Preserve original inputs and reviewed decisions. A run can process all rows and still require substantial quality work; name and perform that work.

## Surprises and discoveries

Completed evaluation found format-specific credit gains, people features that cannot separate some true and false matches, and missing venue/composition context. Those findings guide implementation. Actual catalog output is the unit of progress; method comparisons address observed failure classes in that output.

## Decision log

- 2026-10-18, Alec: update plans, messages and goal after rejecting the pace of small incremental experiments. Start full-catalog shadow computation, integrate domains in parallel, scale model processing within authorized spend and audit outputs while delivery proceeds.
- Production mutations/deployment and the agreed domain scope are unchanged. Shadow computation is distinct from automatic acceptance into production.

## Outcomes and retrospective

The retained-sample pass, full raw capture, core recording/venue pass, local
Jerry/Aadam rehearsal and full-file computation are complete. The full-file
reader indexes every outcome and serves the verified correction. The
people directory supports cross-artist credit lookup and contextual Luna
hypotheses. Shared venue and music lookup now include native observations and
the cached composition replay. Next, complete broader title discovery, then improve physical-venue
and composition evidence using the visible results. Physical venue
history, legacy setlist order, importer provenance and independent accuracy
qualification remain open; production acceptance follows that work.
