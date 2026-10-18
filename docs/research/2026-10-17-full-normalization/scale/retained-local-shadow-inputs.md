# Admit retained Local root and directory inputs

The shadow runner can consume the original Local metadata root and directory
responses without fetching them again. Admission preserves both inputs and
associates a directory with its captured catalog recording through exact playable
file URLs. It creates offline provider inputs and proposals. It writes no catalog
rows, accepted normalization decisions, input heads, or publication clocks.

This path follows the [joint Local binding contract](../../../plans/active/2026-10-17-full-normalization/local-binding.md).
The existing `retained-http-inputs` command continues to handle the source-selected
Archive and Phish capture formats.

## Prepare and replay

Run the following command with a new output directory:

```sh
dotnet run --project tools/RelistenNormalization --no-build -- \
  admit-retained-local \
  local-dev/normalization-data/2026-10-18-shadow-full-raw-v1/manifest.json \
  local-dev/normalization-data/2026-10-18-native-credit-contexts-v1 \
  local-dev/normalization-data/2026-10-18-native-credit-local-fields-v1 \
  local-dev/normalization-data/2026-10-18-shadow-local-provider-inputs-v2
```

`inputs.jsonl` contains one `local_root_input` followed by one `provider_input`
for each retained directory response. Directory inputs refer to the same retained
root revision. They do not repeat the root payload. `descriptor.json` records the
input hash, original capture artifacts, bindings, and directory/source counts.
Reuse this completed stream for replay; running admission again creates new input
revision UUIDs and admission timestamps. A new admission requires a new directory.

The native capture has 220 directory responses sampled from 150 stored date
families. Several directories may describe one catalog recording. The root holds
many more accounts; only the exact directory tuple can supply the account used by
a directory proposal. Other root dates and taper notes cannot supply its context.

To include the completed stream in a shadow run, set
`RELISTEN_SHADOW_PROVIDER_INPUTS` to its absolute `inputs.jsonl` path. The regular
raw catalog must still supply the source and show inventory. Do not replay a
directory alone: the supporting root is observed during the runner's first pass.
The directory decoder retains its file positions and titles as recording facts;
this path does not turn those positions into a provider setlist.

## What establishes a binding

The decoder and resource constructors are the same ones used by the Local
recording processor. A directory must be a unique member of its retained root.
The captured artist inventory must map that root to exactly one artist, and that
artist must agree with the directory capture's artist.

The command constructs the writer's exact URL:
`https://audio.relisten.net/{root}/{directory}/{mp3}`. It checks matching raw file
rows against the source, artist and source-set coordinates. Orphaned files and
conflicting ownership do not establish a binding. Valid anchors must identify one
recording. Missing or conflicting individual files remain visible alongside valid
siblings. Anchors spanning several recordings leave the directory unresolved.

The directory capture's date-selected source UUID is a sampling hint. A unique,
valid media association can identify a different source. Admission retains both
UUIDs and sets `SelectionMismatch`; it does not replace an existing source or
change any file's playback home. Withholding that association would treat a date
selection as stronger evidence than the catalog's exact media ownership.

Root dates and venue text remain cited evidence. A date conflict is reported while
the captured source/show association remains intact. A date cannot supply a source
when URL anchors are missing. Local review links are also insufficient.

## Evidence, limits and failure

Admission verifies the declared root, selection, receipt and response hashes. It
requires the selection plan's `inputManifestSha256` to identify the exact supplied
root manifest, and that manifest's `planSha256` to identify its inventory plan.
A different authentic capture of the same root and artist cannot replace the
selected capture's dates, venue text or membership. Admission
then authenticates and decodes each source/file shard once, checking original
framing and row counts. It keeps only matching media rows and their required
source rows. It does not copy or index the five-million-file raw catalog.

The command allows up to 10,000 directory responses, 10,000 file assertions per
directory, 100,000 distinct playable URL assertions, and 100,000 matching catalog
file rows. Retained root/directory payloads together may occupy 128 MiB; each input
is subject to the shared 64 MiB artifact read bound. The existing root selector
bounds the complete decoded root at 100,000 account entries. Exceeding a bound
fails admission; it cannot produce a truncated successful input stream.

Every unresolved directory retains its original body, exact supporting root,
capture artifacts and reason. No unresolved result can select another show by
date. Cancellation or failed authentication produces no completed descriptor;
retain any partial output and start a new attempt in a new directory.

These are historical observations from separate original capture transactions.
Their hashes and original coordinates support replay. They do not establish a
single catalog epoch, current freshness, or automatic acceptance authority.

The focused fixture `TestRetainedLocalProviderInputs` verifies shared recordings
across directories, Unicode directory tuples, incorrect sampling hints, missing
anchors, root/mapping ambiguity, source-set conflicts, multiple recording anchors,
date conflicts, and changed root/directory bytes through the admission and adapter
contracts.

## Retained-data verification

The command above completed against the existing captures on October 18, 2026.
All **220 directories** bound to **150 existing recordings**: 70 recordings had
two directories and 80 had one. All **3,060 file anchors** were bound. No directory
was unresolved, and no sampling hint differed from its media-backed source.

The completed stream contains 221 rows and occupies 11,371,836 bytes. Its SHA-256
is `80193ce07bb7cac5e011a2532084d95f8fa21e48eb178ac33842a4d31089a53f`.
The descriptor SHA-256 is
`7fd6b59e6ee2dbe99f9c53eb9f982f197e3285db7bb38eb1e23482a3408f5db8`.
It records raw catalog manifest SHA-256
`04eff1257182e525fc8df213ff25966f16ee6aa28ae924cd1dce81452c2d0745`.
Original HTTP bodies and all raw catalog shards remain in their original locations.
This run made no provider requests and fetched no media bytes.

All 22 focused Local and existing Archive/Phish admission contract cases passed.
Their deliberate missing and conflicting cases verify behavior absent from this
220-directory cohort, including an authentic replacement root that differs from
the selection's pinned manifest. The retained run establishes admission coverage
for these captured inputs; integrated reader/materialization verification remains
separate.
