# Prepare bounded Jerry and Aadam acceptance samples

`prepare.py` retains public Archive metadata for review. It makes no database writes, approves no performer and starts no import. Run publication only after the performance contract tests and live Phish verification pass.

Use an output directory outside this repository. Raw metadata can contain full descriptions, account fields and unrelated collection labels; keep those files out of committed acceptance evidence.

```sh
python3 docs/plans/active/2026-10-17-show-performances/live-samples/prepare.py jerry \
  --count 500 \
  --jerry-inventory /tmp/relisten-jerry-20261017/inventory.json \
  --output /tmp/relisten-acceptance-20261017/jerry-expanded

python3 docs/plans/active/2026-10-17-show-performances/live-samples/prepare.py aadam \
  --count 320 \
  --output /tmp/relisten-acceptance-20261017/aadam
```

The Jerry command takes a retained search inventory rather than silently choosing a different creator query. It prioritizes explicit early/late, partial-date and combined cases, then samples ordinary identifiers across the inventory. The Aadam command requests an identifier-sorted bounded search for `collection:aadamjacobs AND mediatype:audio`. Both commands retain exact selected identifiers, raw metadata hashes and current metadata. Existing raw files are reused; use a new output directory for a fresh upstream fetch.

`manifest.json` contains proposed exact project-name spans with .NET UTF-16 offsets. A name in a description can refer to another recording. Review the actual billing and contents before approving any proposed span. Several matched project names, combined acoustic/electric contents and compilation wording require further review. The helper's word matches do not establish performance identity or publication eligibility. The normal evaluator remains responsible for eligibility after review.

Generate the batch plans and billing context without approving them:

```sh
python3 docs/plans/active/2026-10-17-show-performances/live-samples/propose.py \
  /tmp/relisten-acceptance-20261017/jerry-expanded
python3 docs/plans/active/2026-10-17-show-performances/live-samples/propose.py \
  /tmp/relisten-acceptance-20261017/aadam
```

Each directory receives `proposed-review-plan.json`, `billing-audit.json` and `held-for-review.json`. Every artist and item approval defaults to false. Copy the proposed plan to `reviewed-plan.json`, review the exact billing and project vocabulary, supply a specific review basis and approve only the supported entries. Remove unresolved projects/items from the runnable plan. Jerry proposals exclude project mentions later than the first 200 UTF-16 code units of a description and preserve the already-approved project vocabulary. Aadam proposals hold named event bills, unresolved joint billing and the alternate `Breeders` label; they do not create a second Breeders artist. The ordinary `Wire` identity is suggested as an explicitly labeled existing-artist fixture for its five selected recordings.

## Batch operator commands

The checked-in `operator/LiveSamples.csproj` is outside the product solution. Build it after the consolidated solution build passes; do not build product projects concurrently:

```sh
dotnet build docs/plans/active/2026-10-17-show-performances/live-samples/operator/LiveSamples.csproj \
  -p:BuildProjectReferences=false
```

Load the isolated environment without printing credentials. Start its current worker and API using the retained launch scripts, after migration and Phish verification. Then use the batch runner:

```sh
source /tmp/relisten-jerry-20261017/env.sh
sample_operator=docs/plans/active/2026-10-17-show-performances/live-samples/operator/bin/Debug/net10.0/LiveSamples.dll

dotnet "$sample_operator" review-plan \
  /tmp/relisten-acceptance-20261017/jerry-expanded/reviewed-plan.json \
  /tmp/relisten-acceptance-20261017/jerry-expanded/raw \
  /tmp/relisten-acceptance-20261017/jerry-expanded/bindings.json
dotnet "$sample_operator" refresh \
  /tmp/relisten-acceptance-20261017/jerry-expanded/bindings.json \
  /tmp/relisten-acceptance-20261017/jerry-expanded/first-results.json
```

Repeat the commands with the Aadam directory after verifying Jerry. Run `refresh` again with a different result filename for the repeat pass. The report records every admitted workflow/run before waiting, plus each typed result or workflow failure type. Use `results ADMISSIONS_FILE` after interrupting the runner to read those same executions without starting new workflows. Inspect failures before admitting another pass.

`collection COLLECTION_PLAN OUTPUT` accepts `definition` (UUID, slug, name, description, publication state), `reviewBasis`, `artistUuids` and optional `selectorUuid`, `selectorIdentifier`, `selectedItems`. Jerry's local fixture supplies its explicit artist list. Aadam's fixture supplies selector `aadamjacobs` and individual item identifiers. These provider memberships preserve real collection evidence without creating an artist/provider mapping.

`discover SELECTOR_UUID MAX_PAGES OUTPUT` uses ordinary collection admission and resumes a persisted checkpoint. A page limit is not an item limit: the initial page requests 100 identifiers, while a continuation can contain up to 5,000. Inspect the current checkpoint and account for that scope before running another page. Do not claim a 320-item discovery bound merely because publication is limited to the reviewed sample.

`native-item ARTIST_UUID ITEM OUTPUT` uses ordinary single-item admission. It still requires a legitimate native artist/provider relationship; the runner creates none.

For the earlier Phish acceptance, `provider-refresh phish-in ARTIST_UUID FROM_YEAR OUTPUT` runs ordinary Phish.in admission and waits for its typed result. Phish.in's year is a lower bound, so account for every later year in its selection. Then `provider-refresh phish-net ARTIST_UUID YEAR OUTPUT` enriches that exact year. Use Phish's isolated environment rather than the Jerry environment for those commands. `import-result ADMISSION_FILE` resumes a saved Phish or native-item execution without starting another workflow. Inspect the typed outcome; completion alone does not prove a successful import.

## Verify listener behavior over HTTP

`verify.py` reads the default artist list, artist details and normalized shows, each selected show UUID, the direct-array date-group route and the existing v2 date destination. It checks the performance fields, source/set/track relationships, ordered playback and absence of internal assignment keys. Collection verification also traverses paginated lists, compares artist filters to membership, and checks distinct source/show/artist counts. These acceptance samples require their artists to be default-visible. Explicit collection selection of a native artist that retains `AutoCreated` is covered by service contract tests; this helper does not verify that separate visibility case.

```sh
python3 -B docs/plans/active/2026-10-17-show-performances/live-samples/verify.py \
  --base http://127.0.0.1:13825 \
  --collection-slug jerry-projects-smoke --minimum-recordings 200 \
  --output-directory /tmp/relisten-acceptance-20261017/jerry-http-first

# After a repeat import, compare complete identities and media references.
python3 -B docs/plans/active/2026-10-17-show-performances/live-samples/verify.py \
  --base http://127.0.0.1:13825 \
  --collection-slug jerry-projects-smoke --minimum-recordings 200 \
  --output-directory /tmp/relisten-acceptance-20261017/jerry-http-repeat \
  --compare /tmp/relisten-acceptance-20261017/jerry-http-first --media-range
```

Use `--artist-uuid UUID` instead of `--collection-slug` for Phish or a single performer. Identity manifests contain complete media URLs and stay outside Git. `summary.json` contains counts and a digest; `requests.json` retains request paths, statuses and response hashes. Comparison excludes ratings and update timestamps, while preserving show/source/set/track UUIDs, relationships, dates, titles, ordering and complete media URLs. The optional media check reads at most 4,096 bytes and requires HTTP 206. Collection counts describe selected recordings; `sources_read` can also include another recording returned by one of their complete show responses.

## Review an explicit early/late split

After the artist import settles, `review-performance PLAN RAW_DIR OUTPUT` applies supported description-heading assignments through `ShowPerformanceService.AssignSourceAsync`, then admits a normal artist rebuild. A plan supplies `artistIdOrSlug`, a review basis, and ordered `items`: exact item identifier, retained metadata SHA-256, fixed `performanceKey`, display date, performance type/label, exact description evidence spans, and optional expected source UUID. Run the intended survivor's items first. Preserve the stored keys when later correcting dates or labels.

The command checks every item before moving any source. It fetches current metadata, checks each whole supporting field against the retained field, verifies the exact span and stated category, and requires a current approved performer review and the published artist/date/source identity. Unrelated date or file metadata does not invalidate the reviewed heading. Evidence records the current observation UUID/content hash, retained raw-file hash, exact spans and review basis. `reviewed_fields` retains a SHA-256 of the entire UTF-8 description so ordinary refresh can hold changed billing without discarding the accepted assignment.

Each source assignment commits independently and progress is saved after each. If assignment or rebuild fails, inspect the report and rerun the same reviewed plan; accepted keys preserve the identities already assigned. Do not run a competing artist import during this operator action. Verify the resulting date array and moved source relationships over HTTP, then repeat the ordinary artist import to check that the reviewed description assignment survives.

## Publish through the existing contracts

The batch operator calls these supported contracts. Its guards require an isolated `relisten_*` database other than the shared development catalog, plus localhost database, Temporal, Redis and optional payload-Redis endpoints. The Temporal guard also prevents a local database setting from admitting imports to an inherited remote cluster.

1. Retain each selected metadata projection with `ArchiveIntakeService.RetainObservationAsync`.
2. Propose reviewed project identities with `ProposeProjectAsync`. For an existing artist, inspect the current registry and use `BindExistingArtistAsync` with the exact artist UUID and review basis. Do not create an alias or provider mapping to bypass an ambiguous match.
3. Apply approved item billing through `ArchivePerformerReviewService.ReviewAsync`, including the supporting observation and exact evidence. Keep alternatives unresolved where the item contains several performing projects.
4. Admit each artist with `TemporalIngestionClient.RefreshTapersSectionArtistAsync`. Await its execution-bound result and record item outcomes and rebuild completion. Do not call the recording writer directly.
5. Add explicit local collection membership. Jerry uses the reviewed performing-project list. Aadam selects the actual individual item identifiers; it must not include every recording by each artist.
6. Read the running v3 API and collection pages, then repeat admission and compare source, set, track and show assignments and complete playback URLs. Count published recordings separately from observations, distinct performances and files.

Keep historical Jerry Garcia Band separate from the existing Melvin Seals `JGB` identity. Generic `Jerry Garcia` billing belongs to each reviewed item; it is not a shared project alias.

## Existing-artist and native-reuse coverage

The small local seed has few artists. A local fixture may create an ordinary catalog artist through `ArtistService.Save` before binding its intake candidate. Label that fixture in the evidence and verify its chosen billing against the selected items. This proves publication to an existing identity; it does not demonstrate that the artist was already present in production.

Exact native-source reuse requires a source produced by its native importer. Use normal single-item admission, `TemporalIngestionClient.ImportArchiveItemAsync`, only with a legitimate artist/provider relationship. A broad `aadamjacobs` collection must not be installed as an artist-specific provider mapping. If no selected item has a qualified native source or legitimate native mapping, report that limitation and retain the synthetic native-ownership contract tests. Do not report a newly intake-owned source as native reuse.

The existing 100-item Aadam selector checkpoint can be resumed to exercise configured discovery. Preserve the discovered membership even when some selected items remain held. Discovery alone does not satisfy the 200-playable-recording outcome.

## Local environment

The prior isolated environment uses PostgreSQL `127.0.0.1:15432`, database `relisten_jerry_20261017`, Redis `127.0.0.1:16383`, Temporal `127.0.0.1:17236` and API `127.0.0.1:13825`. Credentials stay in mode-600 `/tmp/relisten-jerry-20261017/env.sh`. Do not copy them into committed evidence. Confirm current processes before starting services; the old API and worker need not still be running.

Apply current migrations with the standalone worker's `--migrate` command using that environment. Rebuild the solution and helper once before starting the worker and API from the retained launch scripts. Register no schedules. Retain configuration names, code revision, selected identifiers, execution handles, outcomes and sanitized HTTP evidence. Preserve the previous three-recording proof instead of replacing its historical counts.
