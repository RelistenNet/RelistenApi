# Mobile catalog loading and persistence

Use artist → years → selected year → show → source → playable track requests.
Load venues, songs, tours, reviews and song file matches on demand. There is no
combined artist-catalog endpoint and no eager fan-out to every artist collection.

The mobile `RelistenApiClient` returns typed API responses. Existing repositories
map them into Realm; the network executor owns freshness, retry and conditional
reads. One catalog implementation replaces the old transport switch. Catalog
authentication is not required; account sync is a separate protocol.

## Apply the endpoint's collection

[The endpoint map](endpoints.json) assigns the replacement policy to each route.
The request URL and normalized filters identify its query membership. Within
one Realm transaction, upsert returned fields, replace that query's ordered IDs
when authoritative, and save its applied ETag. A complete empty list clears its
own membership. Failed fetch/apply leaves records, membership and validator intact.

- `/artists?include_autocreated=true` owns artist-list membership and covers artists referenced by other collections.
- Artist year/venue/song/tour routes each own their requested list, independently.
- A year, venue, song or tour detail owns its complete `shows` membership.
- Full show detail replaces that show's sources and the fully returned source/set children. Full source detail replaces its own children, not siblings.
- Song `/tracks` owns only song-file membership; it has no show collection. On cold entry load song detail to establish parent show/year data first.
- Source reviews and artist eras replace their own complete ordered collections.
- Search/date/recent/limited results own only their exact query result.
- Resolver entities and shallow parents are field patches; they own no unreturned child lists.
- Popular/trending/momentum lists and live summary are transient online views; they do not overwrite ordinary offline membership or contextual scores.

An omitted shared record is not deleted. Keep records referenced by other
queries, downloads, queues, history or favorites. A compact song/parent patch
updates supplied fields without clearing richer aliases, counts, children or
reviews. Track normalization is one ordered replacement list within the track.
Canonical song membership lives in query IDs once, without duplicate show/track
lists on the song entity.

## Conditional reads and overlapping responses

Ordinary mobile reads use the existing 15-minute refresh interval and ETags.
304 avoids transfer/application but still performs the server read and
serialization. Manual refresh omits the validator and fetches a complete body.
Reconnect retries failed work. Superseded requests for the same query must not
replace that query's newer result.

Independent endpoints do not invalidate one another. Shared metadata applies in
response arrival order; an older overlapping response can overwrite a field.
An unchanged owning endpoint can then return 304, preserving that overwritten
field until a body refresh. This is the accepted simple contract. Use required
root/local-data checks; do not add a cross-query invalidation graph, ordered
catalog revision or a full source/set/track audit before cached reads.

Score-only and normalization-only changes alter payload ETags independently of
imported timestamps. Null popularity clears stored scores. Ranking results have
different comparison populations and remain online-only; opening one loads the
ordinary detail endpoint before catalog persistence.

## Preserve listening identity and display

Decode UUID-backed catalog IDs at the adapter and keep existing Realm UUID keys,
download filenames, queues and history. Short `venue_`, `song_`, `artist_venue_`
IDs stay opaque strings. Account identities/targets retain canonical typed IDs.

Display imported venue labels and track titles. Link to canonical venues when
available; keep canonical track parts for discovery/details. A medley remains
one playable file without inferred offsets. Browse lists contain canonical or
imported targets according to server evidence; follow each entry's kind rather
than selecting list routes from artist feature flags.

Global venue favorites span artists and have a separate My Library Venues
section. They never implicitly favorite shows. Favorites remain exact targets;
merged alias and survivor favorites may coexist. No catalog correction silently
rewrites personal membership.

The catalog schema additions have not shipped publicly. Keep the migration from
the last released schema and imported/download identities; do not add migrations
for discarded intermediate normalization pilot schemas. The account identity
upgrade has separate credential/cache rules in [accounts](accounts.md).

## Verify the journey

Use [HTTP examples](requests.http), [local services](../../local-dev/README.md)
and the sibling mobile repository's current tests. Check through the production
client/repository/Realm boundary:

1. Artist/year/show browsing, source switching and exact selected-file playback.
2. Raw, disabled, partial and fully interpreted artists/tracks with unchanged imported display.
3. Normalization-only corrections, complete empty collections, failed apply/retry and overlapping shallow/full responses.
4. A downloaded file after offline cold start, without changing its UUID filename or losing queue/history.
5. Mixed venue/song browsing, global venue favorites independent of show favorites, and lazy song file matches.

Large lists require device measurements. One local Drums response was 15.49 MB
JSON / 3.85 MB gzip; gzip is the chosen initial mitigation. Do not eagerly fetch
it, retain a diagnostic response copy or duplicate membership lists. If real
phone parse/apply memory requires pagination, first define that endpoint's cursor
and replacement contract; silently truncating a complete list is incorrect.
Physical release-signed callback and playback checks remain release work.
