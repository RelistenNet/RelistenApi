# Shared venue lookup

Browse the retained full-catalog venue proposals at
[Venue directory](http://alecs-macbook-pro.tail09dbf.ts.net:18770/).
The lookup searches across artists, with locality, artist, scope and outcome
filters. A detail page shows the original location account, history citations,
protected unknowns, rival interpretations, and captured recording/show links.

The current projection contains **216,332 catalog venue records**, **207,304
existing place hypotheses**, and **4,362 artists**. It connects **292,523 captured
source UUIDs** to the original recording and show venue fields; 180,070 venue
records have at least one such association. The SQLite projection occupies
192,622,592 bytes (184 MiB). Full proposals, alternatives and native JSON remain
in the original gzip outputs and load on demand.

## Interpret the results

The complete input run uses `shadow-venue-physical-history-v1`. The lookup does
not rerun today's v2 adapter, add gazetteer evidence, accept physical identities,
or merge records by name. Each original venue UUID and `proposedPlaceKey` stays
unchanged. Records sharing an existing place key are linked separately from
same-name or otherwise competing interpretations. The latter retain their
original count and 20-example bound; searching the population is independent of
that bound.

A recording list belongs to the **selected legacy venue record**, not every
member of its place hypothesis. It preserves `source.venue_id` and
`show_context.venue_id` separately. When those differ, either record can lead to
the same recording, and the page explains which field supplied the association.
Recording counts count distinct source UUIDs; show counts count distinct captured
show UUIDs. Neither count establishes a number of physical performances.

Dates remain captured literals, including partial or unknown dates. File links
use the shared reader's `entityKey=source:<uuid>` contract; they preserve file,
source, show and MP3 ownership. The file reader's current indexing progress can
affect which files are available through those links.

The largest limitation is the retained physical/history evidence. Most place
hypotheses still represent separate catalog rows. This particular run contains
**zero parsed historical-name assertions**; that does not establish that the
venues had no earlier names. Unknown locality, room parents, relocation,
coordinate precision and name-validity dates remain visible. See
[venue resolution](venue-resolution.md) for matching and accepted-publication
contracts; this lookup grants no additional authority.

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
The directory above still indexes the retained legacy run; native venue outcomes
are not automatically included in its counts or recording lists.

## Useful examples

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

The new files are
[`venue_directory.py`](../../../../tools/RelistenNormalization/venue_directory.py),
[`venue-directory.html`](../../../../tools/RelistenNormalization/venue-directory.html)
and [`test_venue_directory.py`](../../../../tools/RelistenNormalization/test_venue_directory.py).
They do not change the shared reader, people directory, production API or database.

```sh
python3 tools/RelistenNormalization/venue_directory.py build \
  --run local-dev/normalization-data/2026-10-18-shadow-complete-core-ranges-v1 \
  --raw local-dev/normalization-data/2026-10-18-shadow-full-raw-v1 \
  --database local-dev/normalization-data/2026-10-18-venue-directory-v1/directory.sqlite3
```

Build into a new database. Chunk commits and input identities support `--resume`
after an interrupted or storage-paused build. The 4.5 GiB free-space floor stops
new chunk work; it preserves completed chunks. A partial lookup labels recording
coverage as incomplete and leaves per-venue counts pending until the final count
refresh. A changed immutable input is refused. The builder copies only searchable
fields, compact catalog links and original chunk/line pointers.

```sh
python3 tools/RelistenNormalization/venue_directory.py serve \
  --database local-dev/normalization-data/2026-10-18-venue-directory-v1/directory.sqlite3 \
  --core-url http://alecs-macbook-pro.tail09dbf.ts.net:18765/ \
  --files-url http://alecs-macbook-pro.tail09dbf.ts.net:18767/ \
  --bind 100.73.204.24 --port 18770
```

GET contracts are `/api/stats`, `/api/venues`, `/api/venue?id=<native UUID>` and
`/api/evidence?id=<native UUID>`. List queries support `q`, `locality`, `artist`
(name substring or UUID), `scope`, `outcome`, `offset` and `limit` (maximum 100).
Search uses literal, case-folded words from retained names, history and location
fields. It implements retrieval, not a new normalization policy.

Twelve focused Python contract cases pass, including namesakes, Unicode and
punctuation, history, replacement, source/show provenance, pause/resume and
partial-count readiness. Generated links were also passed through the actual
`ShadowIndex` query contract with two distinct source homes. All 292,523 captured
source/show contexts passed the strict join check. Live HTTP and Chrome search,
detail, rival/evidence links and playback navigation were checked. Verification
artifacts remain under the ignored local `2026-10-18-venue-directory-v1` folder.
