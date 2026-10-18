# Deliver a usable shadow catalog across all imports

## Purpose and current decision

Build something Alec can browse and exercise: proposed normalized venues, songs/compositions, credited people, performances, setlists and playable file relationships across the complete captured catalog. Start with the retained snapshot covering 4,379 artists, 292,522 recordings and 5,064,076 files; preserve the exact manifests and reconcile any different snapshot used. All seven configured importers and Archive intake/collections must appear in the provider ledger, with applicability and available evidence stated.

A shadow catalog stores proposed results in separate local artifacts or a disposable database. It activates no production acceptance policy. This permits complete computation and inspection while publication performance and release accuracy work continue. Missing source facts remain unresolved; a missing implementation is an engineering gap to finish.

This execution sequence supersedes the evaluation-first milestone. The [standing goal](goal.md) preserves the outcome and [implementation/writing standards](goal.md#implementation-and-writing-standards) across turns. Existing domain contracts remain the basis for accepted production data. Use those contracts in shadow computation where practical without requiring production authority or a complete publication lifecycle to inspect proposed decisions.

## Progress

- [x] 2026-10-18: Alec directed full-catalog shadow delivery, parallel domain integration, larger model jobs and consolidated testing; the plan and standing objective are amended.
- [ ] Start the runnable population pass and retain its command, source snapshot and run location.
- [ ] Expose browsable real outputs from each domain and applicable provider while the pass continues.
- [ ] Complete population accounting, independent output audits and fixes for systemic failures.
- [ ] Rehearse incremental input changes and restart locally, then continue production qualification and backfill preparation.

Keep one current integration step active. Domain agents can work concurrently. Record actual run IDs, counts and reader location as they become available; no shadow command or completed run is claimed by this plan.

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

The delivery plan is amended. The full shadow pass, integrated reader and acceptance evidence remain to be implemented/run by the active normalization thread. Record completed outputs and the next integration gap here as execution proceeds.
