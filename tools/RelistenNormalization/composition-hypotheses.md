# Retained composition hypotheses

The composition hypothesis pass retrieves external works for every unique proposed artist-song target in a completed [music consolidation](music-consolidation.md). It uses retained MusicBrainz responses and makes no provider or model requests. The output keeps candidate works separate from existing supported composition links.

A song title or an explicit MusicBrainz work alias can retrieve a candidate. Retrieval uses the shared title normalization and preserves every qualified work ID that matches. A sole retained candidate does not establish uniqueness: the retained pool can miss a same-title work. No candidate produces a catalog composition assignment, canonical writer identity or complete-authorship assertion.

## Inputs and output

`CompositionHypothesisBatch.RunAsync(requestPath, outputDirectory, requestSha256, resume, cancellationToken)` consumes an externally pinned `composition-hypothesis-input-v1` request. The request pins the original music input manifest, completed music output manifest and each retained provider input file by SHA-256, bytes and row count. Provider input formats are explicit:

- `provider_wrappers_jsonl`: the existing ordinary JSONL/gzip `provider_input` envelopes.
- `input_revision_json`: one original `NormalizationInputRevision`, including its original UUID, resource, payload and payload hash. This permits reuse of newly retained acquisition responses without a synthetic capture or copied wrapper stream.

An optional artist-context manifest supplies the actual display name from the same frozen catalogue cell. Its own hash, music-manifest links and catalogue hash are checked. A display name does not assert authorship or an external artist ID. Missing context remains null.

The pass authenticates the complete original correspondence population before collecting proposed targets. Multiple original titles can contribute to one scoped artist-song target. Compound and variant relations remain visible. A review-only target does not become a candidate-bearing song through this pass.

The completed output has three result domains:

- `retained_work_candidates`: one record per qualified MusicBrainz work ID, including observed titles, explicit aliases, ISWCs, external writer roles, lookup/search assertions and associated recording assertions.
- `retained_work_search_pages`: all supplied work-search responses, including zero hits, unknown counts and decoded malformed/error responses. Hit order and reported scores remain source observations.
- `composition_hypotheses`: one record per unique proposed scoped artist-song target. It lists candidate IDs with exact matching title citations and shared matcher diagnostics, or explicitly reports `missing_retained_candidates`.

External artist references retain their provider identity and observed Person/Group type. Recording performer credits do not become composer assertions. The shared matcher receives no canonical writer IDs from these external references; incomplete authorship remains unknown.

## Completion and recovery

A new output directory is required unless `resume` is explicit. Checkpoints bind the exact request and decoder/index/projector versions. Resume rebuilds the same compact candidate and song context, verifies retained acknowledged rows, and continues its original population. It does not refresh source inputs. The pinned completed music manifests retain the original witness provenance; this pass does not require an old upstream catalogue to remain the current catalogue. Exact repeated rows from an interrupted chunk flush are harmless; missing or conflicting acknowledged output prevents completion.

The top `manifest.json` is written last. Cancellation, a changed input, an unsupported response or a capacity refusal leaves retained chunks and an interrupted checkpoint without a completion manifest. Consumers must require the complete top manifest and its pinned artifacts before replacing a browse projection.

Retained inputs have per-response and aggregate byte bounds. The batch also bounds decoded lines, original correspondence counts, output size and free disk space. Crossing a bound rejects completion; it never returns a truncated candidate pool or an empty substitute for unavailable evidence.

The request may set explicit local resource limits:

```json
{
  "limits": {
    "minimumFreeBytes": 4294967296,
    "maximumOutputBytes": 268435456
  }
}
```

This example preserves a 4 GiB host reserve and permits 256 MiB of retained
output. Both limits must be positive; the output ceiling cannot exceed 4 GiB.
When `limits` is absent, the existing 5.5 GiB reserve and 4 GiB output ceiling
remain in effect. Absent limits add no serialized field to older requests.
Resource admission happens before the large retained images are decoded. The
same limits then apply to each output row, checkpoints and final completion.
The exact request hash binds these values: changing a limit requires a new
request and output directory, rather than resuming under different bounds.

## Later inference and browsing

`ICompositionHypothesisSelector` receives the supplied work facts. Its result can select a supplied ID, retain supplied alternatives or abstain. An invented ID is rejected. Inference metadata, including cache and usage receipts, is separate from raw provider assertions. No selector is invoked by the pure hypothesis pass.

The browse consumer can join a hypothesis work to its scoped song groups and use the existing song-to-performance/file routes. Pages must identify these relationships as hypotheses, preserve alternative work IDs and keep supported composition links distinct. Neither search rank nor absence of rivals grants acceptance authority.

A later bounded acquisition can add missing retained work-search responses through the existing shared provider service. Evaluation and authenticated publication remain separate requirements for accepted catalog-to-work correspondence.

## Retrieve missing works

The `composition-work-searches` command retrieves MusicBrainz works for shared
song titles that lack retained candidates. Its queue contains literal dictionary
titles and their original song witnesses. The command authenticates the complete
dictionary and queue before consulting the provider service. It searches works
directly; performer names do not restrict the writer or composition.

```sh
dotnet RelistenNormalization.dll composition-work-searches \
  <request.json> <new-output-directory> <request-sha256>
```

Use the dedicated local normalization database through
`RELISTEN_NORMALIZATION_DATABASE_URL`. The command reuses the existing provider
cache, rate control and evidence capture. It does not import recordings, change
catalog assignments or start schedules.

The request binds a fresh plan UUID, dictionary manifest, queue artifact and
execution limits. Limits cover service calls, newly received response bytes,
output bytes, free space and duration. A zero-call request authenticates the real
queue without making provider requests. Each search is the initial result page;
the command does not automatically fetch later pages, recording searches or work
details. Retained searches can therefore remain incomplete.

The checkpoint records completed, pending, failed and unexecuted tasks. A bound
or partial result exits with status 1 and retains its state; inspect the manifest
instead of treating every nonzero exit as a crash. Add `--resume` to continue the
same request, output directory and original deadline. A new plan is required for
a fresh deadline. Recorded inputs keep their original capture IDs and hashes.

Feed newly retained input revisions into a new hypothesis request alongside the
earlier evidence. The pure pass then proposes candidate works for all supplied
artist-song targets. Acquisition expands the available evidence; it does not
accept title matches or select a work for a song.

## Use album and concert context to retrieve works

A title can identify several unrelated compositions. When recording notes name
an album, concert program or tribute, use that context to retrieve the program's
recordings and their work relationships. A reference to a program does not mean
that a later tribute recording is the original commercial recording.

The existing `shadow-compositions-acquire` request accepts optional
`programSources`: a new nonempty `planUuid`, a pinned `selectionPlan`, and a
pinned `references` JSON file. Each pin contains `path` and `sha256`. References
use `CompositionProgramReference`: the scoped song target, proposed program
title and relationship kind, original source coordinates and text hash, and two
Unicode code-point citations. One citation quotes the title; the other quotes
the enclosing wording that supports the relationship. The batch authenticates
the original target context and both spans before consulting the provider
client. Explicitly selected songs can supply context even when they have no
existing work candidates. Development references may be proposed by inspection;
the planner does not itself extract program references from prose.

`CompositionProgramQueryPlanner` emits literal recording-title searches with a
program release-title constraint and retains every contributing reference.
Unknown relationships remain unresolved without a request. Search pages retain
their plan identity through pagination and replay; recording and work lookups
continue to share the existing acquisition cache. Optional
`MusicBrainzSearchQuery.Program` accepts exactly one literal `releaseTitle` or
qualified MusicBrainz release identity, and only for recording searches. Queries
without this constraint keep their existing serialization and resource keys.

The decoder retains the actual release, medium and singular `track` memberships
returned by recording search. The retained candidate index attaches those search
facts to a work only through an observed qualified, forward recording-to-work
performance relationship. Matching names or a search score cannot create that
edge. Missing relationships remain missing; all rival works remain available to
the selector. These changes use decoder version `provider-observations-v9` and
candidate index version `retained-composition-candidates-v2`. Existing raw
captures and accepted catalog identities stay intact; a future automatic policy
must qualify the new decoder version before publication.

## Select a likely work

`composition-select` uses Luna to select among the retained candidates for an
explicit list of scoped artist-song keys. A plan pins the completed hypothesis
manifest, job UUID, mode, selected keys, spending and attempt caps, duration,
output size and free-space reserve. Missing candidates and skipped inputs remain
in the outcome census.

The optional `concurrency` setting defaults to one and permits up to four
requests at a time. The caller reserves space for the whole batch before
admission. All requests settle in the shared cache before the ordered writer
advances its checkpoint. If one request interrupts a batch, successful siblings
can replay from cache without another paid request.

```sh
dotnet RelistenNormalization.dll composition-select \
  <plan.json> <new-preflight-directory> <plan-sha256> --preflight

dotnet RelistenNormalization.dll composition-select \
  <plan.json> <new-execution-directory> <plan-sha256>
```

Preflight renders the actual requests without reading a key or database. It
reports the complete candidate-bearing population, selected inputs, request
sizes, skip reasons and conservative cost reservations. Execution uses
`OPENAI_API_KEY` and the dedicated local normalization database. The shared cache
reserves cost before a request and retains uncertain charges. Cached responses
need no key or additional paid attempt.

The compact v2 prompt retains every supplied work ID and every distinct work,
writer and recording relationship fact. Equivalent observations share one fact;
each fact includes up to two representative citations. Counts describe retained
source positions, not independent confirmations. The complete typed input hash
binds the omitted citations, which remain in the original evidence. The local
admission cap is 512,000 rendered bytes; it is not a claim about the provider's
context limit. Cost reservation still uses the complete rendered request.

Add `--resume` to continue the same plan and verified output prefix. It replays
cached successes and does not automatically retry failed or uncertain requests.
An explicit new plan with mode `retry_failed` permits a second request only after
a terminal failed attempt. It preserves prior charges and respects both job caps
and the request-wide two-attempt limit. An expired or uncertain attempt does not
become retryable through resume.

Completion means the selected input census was processed. Inspect selection
outcomes and skip reasons for actual matching coverage. Model selections and
rationales remain hypotheses; they do not change provider assertions or accepted
catalog identities. Updated source evidence or a new method requires a new plan.

A real “Saturday Night” request illustrates the remaining matching risk. For
G. Love and Special Sauce, Luna selected the supplied Bill Martin/Phil Coulter
work associated with Bay City Rollers recordings. The retained facts contained no
explicit G. Love correspondence. [Album credits](https://www.qobuz.com/us-en/album/sugar-g-love-special-sauce/u6ek4q9omtzcb)
also identify a G. Love song of that title written by Garrett Dutton. Those album
credits do not identify the captured live file, but they establish a competing
explanation that the candidate pool did not represent. Keep this case
unconfirmed. Evaluate missing candidates and unsupported cover assumptions
alongside selected-ID validity before qualifying automatic acceptance.
