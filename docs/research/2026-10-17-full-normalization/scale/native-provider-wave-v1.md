# Native provider shadow wave, 2026-10-18

The original native provider wave produced 2,010 actual outcomes from 670
retained recording responses: 300 Archive.org,
150 Phish.in and 220 Local directories. All 670 performance proposals preserve
the admitted historical source and show UUID. The remaining 1,340 outcomes are
`not_applicable`: 670 composition results without work claims and 670 setlist
results without ordered accounts. There were no processing failures.

Archive.org and Phish.in use `retained_historical_catalog_source` associations.
Local uses `retained_local_exact_media`; all 3,060 retained media anchors remain
bound to their original source/file/set UUIDs. The unchanged joint
`local_root_input` is retained before its directory inputs. Root evidence and
individual directory accounts keep separate scopes. These historical bindings
do not certify current provider freshness or accept canonical identities.

The shared `ShadowCatalogBatch` processed 61,330 ordinary derived input rows:
4,379 artists, 16,605 songs, 38,475 legacy setlists, 600 exact source/show pairs,
one supporting Local root and 670 native responses. Context UUID and numeric-ID
conflicts were rejected. All catalog context belongs to the existing retained
production capture namespace. The derived input makes no new snapshot claim.

The complete run produced 152,445 outcomes in 9.64 seconds. Its 150,435 context
outcomes remain outside the provider reader and do not replace the full catalog.
Only the 2,010 `provider_input` outcomes enter this view. Optional people-ranking,
composition and geography evidence was unset. The Local v5 side runtime and
repertoire v6 were unchanged during execution; no primary build, provider fetch,
paid call or database access was required. Inputs, output, reader cache and local
receipts occupied about 76 MiB after indexing, within the 100 MiB wave cap.

## Original wave routing limit

Native recording accounts retain 27,967 Archive.org, 3,112 Phish.in and 3,060
Local file observations, with provider text, file and venue evidence. They create
no standalone native `people`, `credits`, `venues` or `file_contents` outcomes in
that run. Its people adapter processed `source_sample`, so supporting catalog
credit outputs could not establish native-credit integration. The
follow-on below consumes the typed decoded observations with their original
input UUIDs, pointers and scopes.

Existing entrypoints are
[ProviderRecordingObservation](../../../../RelistenApi/Models/Normalization/NormalizationProviderObservations.cs),
[RetainedRecordingCreditExtractor.Extract](../../../../RelistenApi/Services/Normalization/RetainedRecordingCreditExtractor.cs)
and
[NormalizationRecordingExtractor.Extract](../../../../RelistenApi/Services/Normalization/NormalizationRecordingExtractor.cs).
The latter already traverses recording/file text and produces bound-file title
proposals without allocating canonical people or song identities.

## Local run evidence

The ignored execution directory is
`local-dev/normalization-data/2026-10-18-shadow-native-provider-wave-execution-v1/`.
Its compact `README.md` explains the scripts and single-use paths. `request.json`
records the command, explicit environment and runtime hashes; `result.json`
records completion and unchanged runtime pins; `native-wave-receipt.json` records
the counts and historical-binding checks. `reader-health.json` records successful
HTML, provider-filter, detail and complete 2,010-row index checks.

The full run is `2026-10-18-shadow-native-provider-wave-v1`; its output manifest
SHA-256 is `90982657dec701d6e5921bbbf006bcc93f0b0ded669bef9b22804ee3ad247216`.
The separate `2026-10-18-shadow-native-provider-reader-v1` projection preserves
actual provider-result line bytes. The runtime side-directory lease was released
after completion; any later replay must verify its recorded pins and use new
output paths.

## Native people, credits and venues follow-on

The [native reader](http://alecs-macbook-pro.tail09dbf.ts.net:18771/) now serves
4,020 actual results across six domains for the same 670 responses. It uses the
minimal 6,250-row input: 4,379 artists, 600 sources, 600 shows, the unchanged Local
root and 670 provider inputs. Songs and legacy setlists were omitted because the
native adapters make no ordered-setlist or authored-work assertions. The full
run produced 5,820 outcomes in 7.24 seconds; 1,800 catalog context outcomes stay
outside this reader. The original view and complete run remain retained.

| Domain | Proposed results | Unresolved results | Not applicable |
| --- | ---: | ---: | ---: |
| People | 432 | 238 | 0 |
| Credits | 432 | 238 | 0 |
| Venues | 670 | 0 | 0 |
| Performances | 670 | 0 | 0 |
| Compositions | 0 | 0 | 670 |
| Setlists | 0 | 0 | 670 |

The people results contain 799 actor observations and 867 credit claims, all
recording-scoped in this sample. These are observations, not distinct accepted
people. The extractor covers recording/file credit fields and aliases attached
to role-bearing actors. Pure role-free alias prose is still unrouted; the
existing `RetainedSourceActorAliasExtractor` and
`NormalizationRecordingSourceActorAliasBuilder` are not consumed by this native
adapter. This sample does not establish file-scope extraction coverage.

Venue results contain 670 observations with historical recording links, 503 name
browse hypotheses and 504 name/locality browse hypotheses. These keys support
retrieval; they do not establish physical venue, room or relocation identity.
Every native actor/credit disables automatic acceptance and every venue remains
unaccepted. Optional model, global people, composition and geography references
were unset. Native standalone `file_contents` outcomes remain absent.

Checks covered every original input revision and artist, all 670 unchanged
performance proposal payloads and all 3,060 unchanged Local media anchors. All
native source/show links preserve the admitted UUIDs. HTTP checks found the full
4,020-row index with no lag, people/credit search for Marc Millman and venue
search for The Marquee. The existing file reader returned 32 Archive.org files
and 16 Local files from representative source links, including playback URLs;
no audio or upstream metadata was fetched. The reader retains the full-file
reader at port 18767 for these links.

Execution evidence lives in
`local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-execution-v1/`:
`result.json`, `native-domain-receipt.json` and `reader-health.json`. The full
run is `2026-10-18-shadow-native-domain-wave-v1`; its manifest SHA-256 is
`0161c1f7b78e13d6a43f804729cd226b5064f6ca5bafd68015aa7fd75eee12e7`.
The provider projection `2026-10-18-shadow-native-domain-reader-v1` preserves
actual result line bytes. Runtime pins stayed unchanged. The process used
5,683,441 new artifact bytes before reader indexing and observed at least
5,034,799,104 free bytes, above its 4.5 GiB reserve. The side runtime lease was
released after execution.
