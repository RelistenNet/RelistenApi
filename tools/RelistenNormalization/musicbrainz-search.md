# Search retained MusicBrainz candidates locally

`musicbrainz-search` freezes a bounded plan, drives at most one page acquisition, and exports the original evidence. It uses the public MusicBrainz acquisition service and the same database admission row as lookups. Search hits remain candidates. This mode performs no hydration, correspondence acceptance, or catalog writes.

Use the dedicated local `relisten_normalization_*` database configured by `RELISTEN_NORMALIZATION_DATABASE_URL`, on `localhost` or `127.0.0.1`. Apply migrations **18, 43, and 47** for the normalization foundation and MusicBrainz acquisition/search schema before running. The mode does not initialize a database or start the API/worker. Use one database for the experiment so lookup and search traffic share admission and cooldown.

Create a JSON request from exact retained evidence. Replace the illustrative UUID/resource with an existing retained input whose `/title` string is exactly the supplied value:

```json
{
  "seed": {
    "kind": "work",
    "text": {
      "value": "Retained title",
      "evidence": {
        "inputRevisionUuid": "11111111-1111-4111-8111-111111111111",
        "jsonPointer": "/title"
      }
    },
    "relatedArtists": []
  },
  "inputs": [
    {
      "inputRevisionUuid": "11111111-1111-4111-8111-111111111111",
      "resource": {
        "provider": "example-provider",
        "resourceType": "recording",
        "providerKey": "original-key"
      }
    }
  ],
  "cacheMode": "refresh"
}
```

`kind` accepts `work`, `recording`, or `artist`. A missing title/name uses `text: null` and returns `MissingSearchText` without HTTP. Supply `inputs: []` when there are no seed citations. Otherwise, `inputs` must list exactly the revision UUIDs and original resource keys supporting every text, context name, external identity, and role citation. The batch uses one primary repeatable-read evidence read: at most 100 inputs, 32 MiB per input, and 128 MiB combined. It validates pointers and optional Unicode spans/quotes against those original bytes before admission. An exceeded bound or unavailable/mismatched reference fails the whole preflight.

Optional `relatedArtists` entries use `recordingPerformer` for recordings or `workAuthor` for works. Supply a cited `name`, a cited qualified MusicBrainz artist `identity`, or both; an identity supplies the query constraint when both exist. Work-author context also requires a cited `role` whose exact value is writer, composer, or lyricist, ignoring case. Artist searches have no related-artist constraint. Literal validation checks what the retained source says. Caller-supplied role/context interpretation remains unaccepted, recorded as `seedContextAccepted: false`; a later resolver must establish its semantic authority.

The pure query builder emits the broad literal query first, then at most two distinct context variants in deterministic order. Every original context assertion remains in `request.json`, including repeated assertions and context excluded by `QueryVariantLimit`. Titles, aliases, filenames, and performer names are never guessed. Queries quote and escape literal values; requests cannot supply raw query syntax, URLs, includes, credentials, or arbitrary pagination. `reuseFresh` requires an explicit positive `maximumCacheAge` in .NET TimeSpan format; `refresh` requires it absent or null. The request file is at most 64 KiB, with strict field names, string enums, and no duplicate or unknown properties.

```sh
dotnet run --project tools/RelistenNormalization -- musicbrainz-search \
  /path/to/plan-request.json /path/to/new-output-directory <plan-uuid>
```

Use a new nonempty plan UUID for a new retrieval. All page commands and descriptors are written to `plan.json` before calling the acquisition service. Child UUIDs derive deterministically from the plan UUID, variant ordinal, and offset. Every child request also retains the full request-file SHA-256, so changed context or even changed request bytes cannot silently reuse an existing plan's commands. Each variant has page size 50 and offsets 0, 50, 100, and 150. The frozen plan permits at most three variants, 600 returned slots, and 36 possible sends under the acquisition service's three-attempt budget per page. It does not automatically exhaust those ceilings.

Each invocation calls `AcquireSearchAsync` at most once. It returns immediately on pending admission and never sleeps or loops over provider requests. Resume with the exact request file, same plan UUID, explicit prior receipt, and a new output directory:

```sh
dotnet run --project tools/RelistenNormalization -- musicbrainz-search \
  /path/to/plan-request.json /path/to/another-new-output-directory <plan-uuid> \
  /path/to/previous-output/plan-receipt.json
```

After interruption, `plan.json` can serve as the prior file when no completed receipt exists. Prior files must match the exact frozen plan and current builder/batch version; incompatible versions require inspection and a new explicit retrieval plan. Progress always comes from saved public acquisition commands and their original captures. A prior file's claimed page success never supplies provider evidence. Stable child UUIDs also preserve the original commands if the prior output is unavailable. Saved eligible responses can recover without another GET. There is no job registry, scheduler, notification watermark, or automatic refresh interval.

Stdout reports `state`, `planUuid`, `notBefore`, and `outputDirectory`. `completed` means all executed query windows reached their observed ends without coverage issues. It does not imply hydrated candidates or accepted matches. `pending` means an eligible page remains; invoke again at or after its reported `notBefore` when present. `completed_partial` includes failed pages, malformed members, duplicate hits, count drift, short pages inconsistent with count, offset gaps, unexpected later commands, or query/page bounds. Exit code 0 is reserved for `completed`; pending and partial results return 1. Earlier successful pages and every decoded hit/source position remain in receipts after later failure or pending admission.

Every receipt sets `externalUniverseComplete: false`. `observedEnd` describes the returned query window, whose offset pages have no provider snapshot fence. A zero-result capture is retained evidence for that query at that observation time. It does not establish permanent absence or prohibit a future evaluated positive correspondence. Scores and order are retrieval metadata. This mode lists all distinct qualified entities as `unhydratedEntities`, sets `hydrationImplemented: false`, and reports `HydrationLimit` above the advisory ceiling of 20. It selects and acquires no hydration targets.

Output uses a CreateNew ownership file and never overwrites an existing directory. `manifest.json` is written last after a completed local invocation, including pending or partial acquisition outcomes. It records assembly hashes, batch/query/acquisition/decoder versions, and each artifact's exact byte length and SHA-256:

- `request.json` and optional `prior-plan-receipt.json`: exact supplied bytes.
- `seed-inputs/<revision>/`: the original retained source bytes and immutable revision metadata.
- `plan.json`: every frozen query, page request, descriptor, and child command UUID.
- `pages/<variant>-<offset>-<command>/command.json`: public durable acquisition state.
- `attempts/<ordinal>-<attempt>/response.json` and `body.bin`: original response metadata and exact bounded body, including failures; no fabricated body is written for incomplete transport.
- `input.bin`, `capture.json`, and `decoded.json` beside each available capture: original bytes, fetch/revision provenance, and the current pure decoder result. Cache references are exported under `cached/` within their page directory.
- `plan-receipt.json`: page observations, retained candidates, variant coverage, unexecuted/blocked limits, and explicit authority/hydration flags. Receipts are bounded to 16 MiB; no completed manifest is published if this bound is exceeded.

Cancellation or unexpected failure leaves no completed manifest. A best-effort audit runs for up to ten seconds with an independent token: `interruption.json` retains the plan and exception type; `recovery/` exports available saved commands, responses, and captures. Audit output can remain partial if the filesystem or database is unavailable. Preserve original output directories and resume from the frozen plan in a new one. No exactly-once HTTP delivery is claimed.

The [candidate search contract](../../docs/plans/active/2026-10-17-full-normalization/musicbrainz-candidate-search.md) describes query semantics, retrieval limits, and the separate evaluated-correspondence work. The [lookup acquisition guide](musicbrainz-acquire.md) explains the shared transport behavior.
