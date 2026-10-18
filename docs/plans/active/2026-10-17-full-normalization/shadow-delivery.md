# Deliver a usable shadow catalog across all imports

## Purpose and current decision

Build something Alec can browse and exercise: proposed normalized venues, songs/compositions, credited people, performances, setlists and playable file relationships across the catalog. The first completed run processes every raw record in the retained sample covering 4,379 artists. Raw capture of the full 292,522 recordings and 5,064,076 files has started and remains in progress. Preserve the exact manifests and reconcile different snapshots. All seven configured importers and Archive intake/collections must appear in the provider ledger, with applicability and available evidence stated.

A shadow catalog stores proposed results in separate local artifacts or a disposable database. It activates no production acceptance policy. This permits complete computation and inspection while publication performance and release accuracy work continue. Missing source facts remain unresolved; a missing implementation is an engineering gap to finish.

This execution sequence supersedes the evaluation-first milestone. The [standing goal](goal.md) preserves the outcome and [implementation/writing standards](goal.md#implementation-and-writing-standards) across turns. Existing domain contracts remain the basis for accepted production data. Use those contracts in shadow computation where practical without requiring production authority or a complete publication lifecycle to inspect proposed decisions.

## Progress

- [x] 2026-10-18: Alec directed full-catalog shadow delivery, parallel domain integration, larger model jobs and consolidated testing; the plan and standing objective are amended.
- [x] Complete the retained-sample pass and retain its command, input hashes, method versions and output location.
- [x] Expose the eight integrated domains in a reader with proposals, native evidence, related records and playback links.
- [ ] Capture and process the full recording/file population, reconcile provider coverage, and independently audit outputs and systemic failures.
- [ ] Rehearse incremental input changes and restart locally, then continue production qualification and backfill preparation.

The current integration step is full raw-population capture and replay. The first
complete 50,000-recording source range also completed inline normalization:
150,000 outcomes, no processing failures and all captured show UUID relationships
retained. See its [receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-inline-first-range-v1.json).
The expanded integration checkpoint passed 109 tests. The full file population
and independent matching audits remain incomplete. Domain quality and model-enrichment work can continue independently over the completed sample.

## Completed recording and venue ranges

The [full recording/venue reader](http://alecs-macbook-pro.tail09dbf.ts.net:18765/)
shows the completed 15-range core pass: 292,523 recordings, 216,332 venues,
16,605 repertoire songs and 38,475 legacy setlists, plus one ordered provider
input. It produced 1,242,539 outcomes with no processing failures. Audio-file
capture and normalization remain incomplete. The reader reports any index lag
separately from completed runner work.

A 2,000-record Luna wave completed in 425 seconds with $0.278385 accounted in
its ledger: 1,952 validated extraction results and 48 unresolved responses.
The [sample reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/) now includes
those separate model interpretations and 2,296 positive model credit mentions.
The offline replay processed every sample input without another paid request
or a processing failure. These are extraction proposals; matching accuracy
and production acceptance remain separate work.

The current C# checkpoint passed 121 behavior tests; the compact reader passed
20 and the capture operator passed 15. See the [combined run receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-core-and-luna2000-v1.json)
for exact counts and retained execution paths. Physical venue identity,
cross-artist compositions, native provider coverage and independent relationship
audits remain incomplete even when a raw range has no processing failures.

## Run and inspect the completed sample

Open the [live shadow reader](http://alecs-macbook-pro.tail09dbf.ts.net:18764/).
The retained run at `local-dev/normalization-data/2026-10-18-shadow-catalog-v1/`
contains **258,987 domain outcomes and zero failures**. It processes all 32,593
recordings, 34,861 files, 17,073 venues, 1,696 repertoire songs and 432 legacy
setlists in the raw sample, plus one retained Jerry Garcia provider input.
An input can contribute several domain outcomes. Counts do not represent distinct
canonical identities or completion of the full catalog. The shared integration
checkpoint passed 69 tests.
The checked-in [first-run receipt](../../../research/2026-10-17-full-normalization/scale/evidence/shadow-catalog-first-run-v1.json)
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
providers alone do not establish importer provenance. Full capture, remaining
provider/domain evidence and independent relationship audits are still open.

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

The retained-sample pass and integrated reader are complete. The next population deliverable is raw capture and shadow replay of all recordings and files. Composition evidence, physical venue history, legacy setlist order, importer provenance and independent audits remain active quality work; production acceptance qualification follows those results.
