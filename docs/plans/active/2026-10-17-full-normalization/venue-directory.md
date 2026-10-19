# Shared venue lookup

Browse shared venue groups at
[Venue directory](http://alecs-macbook-pro.tail09dbf.ts.net:18776/?q=MSG).
The lookup searches across artists, with locality, artist, scope and outcome
filters. A detail page shows the original location account, history citations,
protected unknowns, rival interpretations, and captured recording/show links.
The original [Furthur “MSG / NY, NY” entry](http://alecs-macbook-pro.tail09dbf.ts.net:18776/?id=c7999ad2-04fa-42ab-ba9f-59649e679ff8)
now opens **Madison Square Garden · New York City, New York, US**, with **950
recordings from 34 artists**. The same rules handle the John Butler Trio and John
Mayer MSG variants.

The completed projection retains **216,332 catalog venue records** across **4,362
artists**. The C# normalizer selects **34,726 shared hypotheses** containing
**121,085 original accounts**. It connects **292,523 captured source UUIDs** to the
original recording and show venue fields; 180,070 venue records have at least one
such association. A full-corpus refresh changed 28,417 accounts and retained
187,915 unchanged accounts. Its correction files occupy 7,173,367 bytes; the
139,194,368-byte correction database supplies current search fields and counts.
The original catalog projection occupies 281,640,960 bytes. A separate
9,814,016-byte native overlay retains 670 venue observations and 34,139 associated
files. Full proposals and native evidence remain in the original gzip outputs.
The [original baseline](http://alecs-macbook-pro.tail09dbf.ts.net:18770/) remains
available for comparison.

## Interpret the results

The current correction uses `shadow-venue-physical-history-v3/corpus-hypotheses-v2`
with the original pinned geography evidence. Selected hypotheses supply preferred names,
normalized locality and shared membership. Every original venue UUID remains
valid; its evidence retains the raw wording. Physical identity remains unaccepted.
Competing interpretations retain their original counts and bounded examples;
searching the population is independent of those example bounds.

A bare UUID API request opens the shared recording list when the account has a
current selected hypothesis. Use `recordings=selected` for the **selected legacy
venue record** alone. Both preserve `source.venue_id` and
`show_context.venue_id` separately. When those differ, either record can lead to
the same recording, and the page explains which field supplied the association.
Recording counts count distinct source UUIDs; show counts count distinct captured
show UUIDs. Neither count establishes a number of physical performances.

Dates remain captured literals, including partial or unknown dates. File links
use the shared reader's `entityKey=source:<uuid>` contract; they preserve file,
source, show and MP3 ownership. The file reader's current indexing progress can
affect which files are available through those links.

The largest limitation is the retained physical/history evidence. This run contains
**zero parsed historical-name assertions**; that does not establish that the
venues had no earlier names. Unknown locality, room parents, relocation,
coordinate precision and name-validity dates remain visible. See
[venue resolution](venue-resolution.md) for matching and accepted-publication
contracts; this lookup grants no additional authority.

## Normalized group view

A new projection can consume the C# adapter's `normalization` proposal. Its selected
hypothesis supplies the shared key, preferred name, display locality, membership
basis and support counts. The directory does not match names or choose among rival
places. When C# selects one hypothesis for “MSG” and “Madison Square Garden,” the
list shows one **Madison Square Garden** result with the proposed locality and
member counts. Unresolved accounts, distinct rooms and native observations remain
separate unless their own producer supplies a supported shared hypothesis.

Search includes the original name and location of every emitted member, the
preferred name, normalized locality and retained historical names. It is independent
of C#'s bounded member/name examples. Thus a raw “MSG” or “NY, NY” query can retrieve
the same group under another artist's filter. Raw wording remains visible on the
original account and in its immutable evidence.

Normalized result links open the group’s recordings across its supported catalog
members. The artist filter and pagination apply to that recording list. A source
UUID appears once even when both its recording and show venue fields match group
members. Dates, source/show venue IDs, show UUIDs and file links retain their original
homes. The account links and “Selected account’s recordings” action return to the
original venue UUID’s narrower recording list. Grouping changes discovery, not
ownership or accepted physical identity.

## Whole-catalog normalization pass

`shadow-venues` runs only the C# venue adapter over retained catalog inputs. It
observes the original source/show associations during indexing, then emits venue
outcomes through the existing checkpointed runner. It does not construct people
or music adapters, invoke a model, or write production normalization state.

```sh
RELISTEN_SHADOW_VENUE_GEOGRAPHY='<geography-manifest>#sha256=<digest>' \
  dotnet <built-normalization-tool.dll> \
  shadow-venues <retained-capture-manifest.json> <new-output-directory>
```

The v3 adapter uses resolved geography, literal names with optional leading
“the,” and generic initialisms. A selected full-name record anchors each stable
`venue_hypothesis` key; neither an alias nor a candidate count becomes a canonical
UUID. The primary `proposedPlaceKey` and `displayName` carry the selected shared
hypothesis, so directory grouping and display use the actual result. Original
venue UUIDs, raw names, locality wording and evidence remain available.

A source-observed “MSG” account can select a full “Madison Square Garden” anchor
in the resolved New York locality. An exact same-artist, same-day source/show
association provides additional interpretation support. Multiple incompatible
expansions stay ambiguous. Different known addresses split premises; unknown
addresses cannot bridge those alternatives without their own compatible context.
Contradictory source/show accounts stay separate. Rooms and stages without parent
evidence, explicit historical-name accounts, unresolved geography and missing
source context do not gain a corpus identity.

Membership, artist and distinct-source counts cover the complete selected group.
Only eight member, name and source-context examples are repeated in each outcome;
the directory recovers complete membership from all emitted venue rows. These
are captured observations, not independent confirmations. Recording-day coverage
is not an opening date or name-validity interval. `premisesContinuityVerified`
remains false: unknown historical moves still need evidence. All identities in
this pass are experimental proposals, with accepted identity and playback unchanged.

## Native provider venue observations

[`ShadowNativeVenueAdapter`](../../../../tools/RelistenNormalization/ShadowNativeVenueAdapter.cs)
projects retained provider inputs through the shared authenticated decoder context.
Its `proposal.venues` entries preserve native provider identities, original venue
names, structured locality, coordinate kind and historical-name prose with their
original revision citations. Local directory inputs can use only the exact
selected account in the authenticated supporting root. Missing roots and catalog
bindings remain explicit; no legacy venue or source UUID is fabricated.

Each entry has a scalar `displayName` for reader search, its own
`native_venue_observation` key, and separate name/locality/provider-reference
browse keys. Equal browse keys retrieve hypotheses across artists; they do not
join physical identities. City coordinates remain city observations, room parents
and name-validity dates remain unknown, and history prose is not converted into
accepted intervals. Recording and setlist links retain their original account and
literal date. Optional catalog source/show links require the retained binding.

These native outcomes are `proposed`, `unresolved` or `not_applicable`, with
`acceptedIdentity=false`. They can be inspected in the generic shadow reader.
The directory includes the completed native wave v3 as a separate overlay. Native
accounts retain their original revision, root/recording/file scope and exact file
associations. They are not merged into catalog hypotheses by coincident names or
UUIDs. Global native-to-catalog venue normalization remains unimplemented; its
coverage is separate from the shared catalog groups.

## Useful examples

[MSG](http://alecs-macbook-pro.tail09dbf.ts.net:18776/?q=MSG&locality=New+York+City)
opens **Madison Square Garden · New York City, New York, US**: 88 original accounts
across 41 catalog artists, with 950 recordings and 211 captured show UUIDs.
Of those catalog artists, 34 have recordings in the captured group. The group artist
filter returns 96 Phish recordings or 712 Grateful Dead recordings. The original
MSG UUID `bcdf830e-9543-27fd-f851-db137f524991` retains its own 16-recording
account through `recordings=selected` and its exact “MSG” / “NY, NY” evidence.

A 390-pixel browser check started at the original Furthur MSG UUID, filtered the
shared group to Grateful Dead, and played “Sugar Magnolia” from 1994-10-19 inline.
Playback advanced past 33 seconds with no media error, then was paused. The page
fit the viewport without horizontal overflow. Its source UUID remains
`4d9c0ea8-4710-c281-f58e-cd45b85f33ee`. “Brokedown Palace” in that recording failed
to load in the browser, although a separate HTTP range request returned 206.
The page reported the failure and kept the original source link. The venue layer
does not rewrite provider media URLs.

The following counts describe the unchanged baseline on port 18770.

The current literal-field searches return 136 records for
[Madison Square](http://alecs-macbook-pro.tail09dbf.ts.net:18770/?q=Madison+Square),
978 for [Fillmore](http://alecs-macbook-pro.tail09dbf.ts.net:18770/?q=Fillmore),
191 for [Greek Theatre](http://alecs-macbook-pro.tail09dbf.ts.net:18770/?q=Greek+Theatre),
and 514 for [Red Rocks](http://alecs-macbook-pro.tail09dbf.ts.net:18770/?q=Red+Rocks).
These are retrieval counts, not counts of physical premises. Artist and locality
filters help compare namesakes without borrowing one record's location for another.

[The Old Waldorf](http://alecs-macbook-pro.tail09dbf.ts.net:18770/?q=Old+Waldorf)
returns 12 records. The Bob Weir record at San Francisco, California links three
recordings through the captured show venue. A live boundary check followed one
source to six indexed file results and verified the selected file's exact source,
show and MP3 home.

## Run and recover

The directory implementation is
[`venue_directory.py`](../../../../tools/RelistenNormalization/venue_directory.py),
[`venue-directory.html`](../../../../tools/RelistenNormalization/venue-directory.html)
and [`test_venue_directory.py`](../../../../tools/RelistenNormalization/test_venue_directory.py).
Building or serving this projection does not alter the shared reader, people
directory, production API or database.

```sh
python3 tools/RelistenNormalization/venue_directory.py build \
  --run local-dev/normalization-data/2026-10-18-venue-normalization-v3/run \
  --raw local-dev/normalization-data/2026-10-18-shadow-full-raw-v1 \
  --database local-dev/normalization-data/2026-10-18-venue-directory-normalized-v2/directory.sqlite3
```

Build into a new database. Chunk commits and input identities support `--resume`
after an interrupted or storage-paused build. The 4.5 GiB free-space floor stops
new chunk work; it preserves completed chunks. A partial lookup labels recording
coverage as incomplete and leaves per-venue counts pending until the final count
refresh. A changed immutable input is refused. The builder copies only searchable
fields, compact catalog links and original chunk/line pointers.
The v2 projection requires a new database; older immutable databases remain readable
and are not upgraded in place. To obtain normalized groups, supply a completed C#
run that includes `normalization`. Rebuilding the older v1 input retains
its original per-account interpretations.

Build the independent native overlay before serving it:

```sh
python3 tools/RelistenNormalization/venue_directory.py native \
  --run local-dev/normalization-data/2026-10-18-shadow-native-domain-wave-v3 \
  --database local-dev/normalization-data/2026-10-18-venue-directory-normalized-v2/native.sqlite3
```

```sh
python3 tools/RelistenNormalization/venue_directory.py serve \
  --database local-dev/normalization-data/2026-10-18-venue-directory-normalized-v2/directory.sqlite3 \
  --core-url http://alecs-macbook-pro.tail09dbf.ts.net:18765/ \
  --files-url http://alecs-macbook-pro.tail09dbf.ts.net:18767/ \
  --native-database local-dev/normalization-data/2026-10-18-venue-directory-normalized-v2/native.sqlite3 \
  --native-url http://alecs-macbook-pro.tail09dbf.ts.net:18774/ \
  --music-url http://alecs-macbook-pro.tail09dbf.ts.net:18769/ \
  --people-url http://alecs-macbook-pro.tail09dbf.ts.net:18777/ \
  --corrections-database local-dev/normalization-data/2026-10-18-venue-directory-corrections-v5/corrections.sqlite3 \
  --bind 100.73.204.24 --port 18776
```

## Inline recording playback

Venue and people pages share the bounded, read-only
[`RecordingPlayback`](../../../../tools/RelistenNormalization/recording_playback.py)
adapter. **Show tracks** lists the selected recording's files; **Play** opens the
original audio on the same page. The configured file reader owns membership and
playback URLs. The adapter uses exact source UUIDs and verifies the selected
file's source home. It never joins recordings by date, venue or title.

Both servers expose `GET /api/recording-files?source_uuid=<uuid>&offset=<n>` and
`GET /api/recording-file?source_uuid=<uuid>&id=<reader record id>`. Pages contain
at most 30 files. Upstream requests have a 20-second timeout and an 8 MiB response
limit. Invalid ownership or an unavailable reader returns an error with the
source-evidence fallback. Native recording parents are not treated as individual
tracks; accounts without a catalog source UUID retain their original file and
provider links.

`/api/stats` advertises `capabilities.recordingPlayback` and optional
`navigation: {songs, compositions, venues, tapers}` links. Venue `serve` accepts
`--music-url` and `--people-url`; people `serve` accepts `--music-url` and
`--venue-url`. Missing navigation targets stay hidden. Existing launch arguments
remain valid. Updated HTML requests the playback script and endpoints only when
the server advertises support, so an older running server keeps its source links.

Restarting a reader loads the adapter and navigation options. Its SQLite
projection, normalization decisions and original playback identities stay intact.
To roll back the interface, run the earlier server code with its original launch
arguments and the same projection; no catalog migration is involved.

## Venue API

GET contracts are `/api/stats`, `/api/venues`, `/api/venue?id=<native UUID>` and
`/api/evidence?id=<native UUID>`. List queries support `q`, `locality`, `artist`
(name substring or UUID), `scope`, `outcome`, `offset` and `limit` (maximum 100).
Search uses literal, case-folded words from retained names, history and location
fields. A selected C# hypothesis produces one list row; its `uuid` is an original
matching member, including under an artist filter. Old proposals without selected
normalization and native accounts keep their individual result rows.

`GET /api/venue?id=<original UUID>` lists associations through every catalog member
of its current selected normalization hypothesis. `recordings=group` remains valid;
`recordings=selected` requests only the original account. An account without a
selected hypothesis keeps its own list. The response identifies the effective scope
with `recordingsScope: shared_normalization_hypothesis` or
`selected_legacy_venue_record`. Group rows include their original
`source_venue_member_uuid` and `show_venue_member_uuid` when the corresponding field
points to a member. `artist`, `offset` and `limit` apply in both scopes.
`/api/evidence` always returns the selected UUID's exact retained outcome.

## Changed-only venue corrections

The current reader serves the full-corpus correction in
`local-dev/normalization-data/2026-10-18-venue-normalization-corrections-v4`.
Its completed manifest SHA-256 is
`8315e09bac1f48fbbf628e1de989a3ccb2ede7cf632b5fea5db0a4fbd8e7e8a3`.
The projection and its execution receipt are in
`local-dev/normalization-data/2026-10-18-venue-directory-corrections-v5`.
That directory also holds the API results, phone screenshot and
the exact previous server command for rollback. The original database's pinned
digest is unchanged.

The completed C# pass took 52.25 seconds with a 1 GiB managed-heap limit and a
1.25 GiB observed resident-memory stop. Its sampled peak resident memory was
1,028,079,616 bytes; observed free disk stayed above 4,792,512,512 bytes.
The reader projection took 25.56 seconds with a 192 MiB storage limit and a
4 GiB free-space floor. These are explicit limits for this run. Earlier failed
attempts retain their receipts; they did not replace the reader's base data.

The optional correction projection applies a completed, pinned C#
`venue-normalization-corrections-v1` manifest to one exact base database. It stores
changed searchable fields, current C# groups and evidence locators. It does not copy
the catalog database or rewrite its original venue and recording rows. Build it with:

```sh
python3 tools/RelistenNormalization/venue_directory.py corrections \
  --manifest <completed-correction-manifest.json> \
  --manifest-sha256 <manifest-sha256> \
  --base-database <original-directory.sqlite3> \
  --database <new-corrections.sqlite3> \
  --minimum-free-bytes 4294967296 \
  --maximum-bytes 201326592
```

The builder verifies the original UUID, prior tuple and pinned original outcome
before publishing the replacement. A current `normalizationKey: null` removes the
old membership. C# supplies the preferred name, locality and membership decision;
Python recomputes affected recording and show counts through the original source
and show venue IDs. One source UUID still counts once when both fields match.
Native accounts remain separate.

Serve the same base with `--corrections-database <corrections.sqlite3>`. Search and
detail use current fields, while `/api/evidence` keeps the original outcome.
Changed detail rows add `correctionEvidenceUrl`; `GET /api/correction?id=<UUID>`
returns the exact retained transition and its manifest pin. `/api/stats` exposes
correction provenance and current normalized membership counts separately from the
baseline capture. A changed base or sidecar generation is refused until restart.

The builder defaults to a 64 MiB output limit and a 5.5 GiB free-space floor.
Operators can set explicit job limits with `--maximum-bytes` and
`--minimum-free-bytes`. The example uses the completed run's 4 GiB reserve and
192 MiB output limit. These limits are independent of the matching policy.
Failed or interrupted replacement leaves a previously published sidecar intact. Roll back by
omitting `--corrections-database`; the base and original playback homes are unchanged.

Focused Python contracts cover shared preferred names, complete member-alias search,
normalized localities, group and selected-account recording scopes, artist filters,
old database compatibility, distinct rooms/native accounts, Unicode and punctuation,
history, replacement, source/show provenance, pause/resume and partial-count readiness.
Generated links were also passed through the actual
`ShadowIndex` query contract with two distinct source homes. All 292,523 captured
source/show contexts passed the strict join check. Live HTTP and Chrome search,
detail, rival/evidence links and playback navigation were checked. Baseline
verification artifacts remain under the ignored local
`2026-10-18-venue-directory-v1` folder. The normalized projection's build/serve logs
and `phone-msg.png` are under `2026-10-18-venue-directory-normalized-v2`.
