# Native provider shadow wave, 2026-10-18

The [native provider reader](http://100.73.204.24:18771/?domain=performances) serves
2,010 actual outcomes from 670 retained recording responses: 300 Archive.org,
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

## Current routing limit

Native recording accounts retain 27,967 Archive.org, 3,112 Phish.in and 3,060
Local file observations, with provider text, file and venue evidence. They create
no standalone native `people`, `credits`, `venues` or `file_contents` outcomes in
this run. The people adapter still processes `source_sample`, so supporting
catalog credit outputs cannot establish native-credit integration. The next
routing step must consume the typed decoded observations with their original
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
