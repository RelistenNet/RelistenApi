# Native observations in the venue directory

The existing venue directory can include a small native overlay alongside its
legacy projection. The legacy database keeps its 216,332 venue rows and 292,523
recording homes. Native observations have separate identifiers and coverage;
they do not become canonical venues or join catalog records by matching names,
provider numeric IDs or Local numeric IDs.

This addition is implemented and tested against synthetic retained outcomes.
The first actual 34,139-file projection reached the 32 MiB staging cap and
published nothing. The interned retry completed with 670 observations, 670 input
parents and 34,139 files in 0.773 seconds. Its 9,814,016-byte overlay fits the
unchanged 32 MiB cap. Reader integration and cutover remain separate verification;
this document does not claim that cutover has happened.

## Project and serve

Build the native overlay into its own new file:

```sh
python3 tools/RelistenNormalization/venue_directory.py native \
  --run local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-v2 \
  --database local-dev/normalization-data/2026-10-18-venue-directory-v1/native.sqlite3
```

Only completed `venues` and `file_contents` chunks are read. The builder retains
compact search fields, file keys and original chunk/line/ordinal locators. It
copies neither the legacy database nor provider bodies. Exact recording origins
are interned once per provider input/account; file rows reference that private
integer key. The join still compares the complete original canonical origin and
parent UUID, including original evidence coordinates and quotes. Interning does
not collapse accounts by name, resource key or catalog ID. Bounds are 10,000 input
results per domain, 10,000 venue observations, 100,000 files and 32 MiB of staging
database/journal, with a 4.5 GiB free-space reserve. The current 670 venue inputs
and 34,139 files fit the count bounds; the interned actual retry also fits the
size bound. These are not full-catalog limits.

An update writes an exclusive `.building` file and replaces the native overlay
atomically only after completion and budget checks. The interned representation is
`native-venue-overlay-v2`; rebuild an older native overlay before serving it with
this reader. The native command can atomically replace its earlier v1 format. An
ordinary failure removes that staging file and preserves the previous overlay. Latest logical parent
counts per provider/domain must match the completed manifest. A missing domain,
chunk or parent is an incomplete input, not a withdrawal. A process crash can leave
the staging file; inspect it before removing it and retrying. The command refuses
to replace a database that is not already a native overlay. Later results for the
same provider input replace its earlier observations and file inventory, including
retractions. No automatic retries or live writes are introduced.

Start the existing directory with its additional optional arguments:

```sh
python3 tools/RelistenNormalization/venue_directory.py serve \
  --database local-dev/normalization-data/2026-10-18-venue-directory-v1/directory.sqlite3 \
  --native-database local-dev/normalization-data/2026-10-18-venue-directory-v1/native.sqlite3 \
  --native-url http://alecs-macbook-pro.tail09dbf.ts.net:18771/ \
  --core-url http://alecs-macbook-pro.tail09dbf.ts.net:18765/ \
  --files-url http://alecs-macbook-pro.tail09dbf.ts.net:18767/ \
  --bind 100.73.204.24 --port 18770
```

Both databases are opened read-only when serving. Omitting the native arguments
preserves the legacy-only behavior. The native filter stays hidden and disabled
until stats declares a published native run; native-only links explain when the
backend has not published those observations. This also avoids misleading
results while an old running Python process serves the updated HTML. Each request attaches the published native
file, so an atomic replacement becomes visible without rebuilding the legacy
projection. This does not restart or alter any currently running reader.

## Search, detail and evidence

Existing GET routes remain `/api/stats`, `/api/venues`, `/api/venue?id=…` and
`/api/evidence?id=…`. Search includes both record kinds by default. `kind` can be
`legacy_catalog` or `native_observation`; existing name, artist, locality, scope,
outcome and pagination filters still apply. Native search covers all original
names, venue prose and alternative locality assertions. It does not parse prose
into accepted historical intervals or borrow another account's locality.

Legacy UUIDs remain unchanged. A native identifier includes the provider-input
UUID and the original observation key. This keeps a repeated root observation
separate in each provider input's context. Native detail and evidence verify the
original parent UUID and observation key at the retained chunk/line locator.
Detail shows the original venue scope, unparsed prose, locality assertions,
recording/setlist origins, optional authenticated catalog UUID anchors and
protected unknowns. No same-name related-record group is manufactured.

Native file retrieval requires both the provider-input UUID and exact recording
origin from a typed venue recording link. It returns files of that associated
recording account, not independent file-scoped venue assertions. Missing catalog
anchors do not remove these native files. Conversely, a venue or root account
without a recording link cannot acquire a file list through name or numeric-ID
matches. `fileOffset` and `fileLimit` paginate this list independently from the
recording list; each file link uses its stable `native_file` entity key in the
native reader. Rebuildable reader cache IDs are not persisted in the overlay.

Native recording source-file links stay in the same native reader and require
both the enclosing input UUID (`q`) and authenticated `source:<uuid>` key. Even
when a Local snapshot UUID coincides with a legacy source UUID, it cannot route
to the production-captured file namespace. The exact native-file page remains
available independently of catalog anchors. Legacy recording links retain their
existing full-file reader. Native numeric IDs never join legacy recording rows.

`/api/stats` preserves the legacy coverage fields and adds `nativeCoverage`
(observations, provider-input outcomes and retained files), `nativeRun`,
`nativeUrl` and `directoryRecords`. Native counts are observation counts, not
accepted physical venues. Retained file coverage counts the input's file
inventory; a particular venue's file list can be smaller because it requires an
exact recording-origin relationship.

## Verification

Twenty-one focused Python tests pass. New cases cover shared search without numeric
joins, original root citations, history prose and alternative localities, exact
recording-origin file links through the actual `ShadowIndex` child API, native
file pagination, a root without recording links, catalog UUID anchors, retraction,
failed-update atomicity after partial projection, 5,000 files sharing one long
original recording origin within the unchanged storage cap, and
refusal to overwrite the legacy database. The original twelve legacy cases
remain unchanged in behavior. Run them with:

```sh
cd tools/RelistenNormalization
python3 -m unittest test_venue_directory -v
```

The current retained native run and its scope limits are described in
[native provider waves](native-provider-wave-v1.md). The existing legacy reader
and its measured coverage are documented in the
[venue directory plan](../../../plans/active/2026-10-17-full-normalization/venue-directory.md).
