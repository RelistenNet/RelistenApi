# Retain Go-Set cover credits for composition matching

Use `go-set-composition-sources` to retain first-party song and setlist pages
before using their cover credits. Archive recording notes often contain the
setlist without the original performer. Go-Set can supply that missing claim.
A performer credit is not a composer credit; recording-to-work evidence must
still distinguish the composition.

The command makes bounded public HTTP requests. It requires no database or
OpenAI key and makes no identity or playback changes.

## Capture original evidence

Write a request containing literal canonical URLs and a storage floor:

```json
{
  "urls": [
    "https://go-set.net/song/such-a-night",
    "https://go-set.net/setlists/?date=2023-04-12",
    "https://go-set.net/setlists/?date=2026-04-30"
  ],
  "minimumFreeBytes": 4294967296
}
```

Hash the saved request with `shasum -a 256 request.json`, then pass its exact
hash and a new output directory:

```sh
dotnet tools/RelistenNormalization/bin/CompositionContextVerification/RelistenNormalization.dll \
  go-set-composition-sources request.json new-output-directory REQUEST_SHA256
```

Use the tool output path from your build. Acquisition permits at most eight
pages, one MiB per response and 32 MiB of output. Each page has a 30-second
deadline. Redirects, noncanonical URLs and automatic retries are excluded.

Each capture retains the original HTML, URL, response metadata, capture time,
SHA-256, input revision and decoded observations. `manifest.json` is written
last. Only a completed manifest can supply downstream evidence. A failed or
cancelled run leaves its written evidence intact; inspect it and use a fresh
directory for another acquisition.

## Keep attribution and correspondence separate

The decoder retains provider song and show identities, passage membership and
`cover_artist` annotations with source spans. Song-history pages supply actual
passage IDs. Show-page positions do not become invented occurrence IDs.

An external credit does not itself identify a Relisten song or recording. Check
existing provider identities first. Otherwise, retain a separate correspondence
proposal with its evidence, method and conflicts. Full setlist sequences, guests
and venue context can support development adjudication; a matching title and
date alone cannot establish the attachment.

The Dogs in a Pile example has a real discrepancy: a recording description says
April 5 while its catalog date and the matching provider show say April 12.
Preserve both claims. The new provider annotation must not rewrite the Archive
description, create a legacy song UUID or move a recording to another show.

Composition context carries the original provider annotation and the separate
`target_source_correspondence` proposal. Development proposals retain
`acceptedIdentity=false` and `reviewAuthority=false`. The selector's source
references establish ownership and structure; they do not establish matching
accuracy or authorize production acceptance. See
[composition discovery](composition-discovery.md) for preferred-suggestion and
playback behavior.

## Attach evidence to a frozen context bundle

Use `go-set-composition-supplement` after retaining the provider pages and
checking the proposed correspondence against original recording notes. Its
request pins the original selection plan and completed acquisition manifest.
Each correspondence names the scoped artist/song, supplied provider song and
passage IDs, exact source and provider text spans, rationale and conflicts.
It carries no expected composition answer.

```sh
dotnet tools/RelistenNormalization/bin/CompositionContextVerification/RelistenNormalization.dll \
  go-set-composition-supplement supplement-request.json new-context-directory REQUEST_SHA256
```

The command verifies the original context and source files, decodes the retained
provider inputs again, and appends cited provider passages to the named target.
It preserves every original target and witness. Provider credits and the
correspondence proposal remain separate claims, with no recording or show UUID
assigned to the added witness. It creates a new context manifest and a selection
plan with a fresh job UUID; completion publishes last.

The supplement makes no network or model calls. It allows at most eight
correspondences, 32 MiB of output and 64 MiB of logical data per exported file.
The request sets the free-space floor. A changed source, invalid citation,
unknown passage or incomplete acquisition fails before a completed manifest is
available. Use the new plan with the composition selector's keyless preflight
before running the model. Original artifacts remain available for comparison
and rollback.
