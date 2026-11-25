# Playlist sharing API

Sharing is served by the User Service. Playlists start private. A published
playlist has a stable random code for the link `relisten.net/playlists/<code>`.
The web page and mobile integration are separate releases.

## Requests

Use the accounts host and the existing library write policy for mutations.
The public snapshot needs no credentials.

| Method and path | Request and result |
| --- | --- |
| `PUT /v1/playlists/{playlist_id}/publication` | Owner only. Send `{"contract_version": 1, "published": true}` or `false`. The request sets the state and needs no `command_id`; a retry returns the current state with `changed: false`. A publish returns `public_code`, and later publishes reuse it. Publishing a taken-down or archived playlist returns `409 playlist_taken_down` or `409 playlist_archived`. |
| `GET /v1/public-playlists/{public_code}` | Returns the current authored structure, public owner ID and username, revision, and history generation. Missing and unavailable playlists return the same 404. |
| `PUT /v1/public-playlists/{public_code}/follow` | Returns 204 for a new or existing follow while the playlist is public. An account can follow up to 500 playlists; the 501st returns `422 quota_exceeded`. Following your own playlist returns `422 own_playlist`, since it is already in `playlists`. |
| `DELETE /v1/playlists/{playlist_id}/follow` | Returns 204 even if the follow is already gone. Works for unavailable or deleted sources. |
| `POST /v1/public-playlists/{public_code}/copy` | Send `contract_version: 1` and `command_id`. Returns 201 with the private copy's ID and revision. Retries return 200 with the same copy, including after takedown or deletion of the source. |
| `POST /v1/public-playlists/{public_code}/reports` | Send a `reason` of 1–300 characters. Returns 204 once a report is open for this account and playlist. A new report beyond 10 per minute per account returns 429. |

Library changes carry `playlist_followed` and `playlist_unfollowed` with
`followed_playlist_id`. A snapshot and the last page of a pull carry every
`followed_playlists` head, even when the library revision is unchanged.
Unavailable heads carry only `playlist_id` and `available: false`. Treat a
snapshot as the full membership list. Apply changes in cursor order and use
heads to update metadata without changing membership.

On a generation change, refetch the public snapshot even if its revision
matches the stored one. Apply it only when its generation matches the new
library generation. If the edge still serves an old generation, retry after
the cached response expires.

## Listing public playlists

`GET /v1/public-playlists` lists published playlists that are not archived or
taken down and whose owner is active. It needs no credentials and is cached at
the edge like the public snapshot.

| Query | Meaning |
| --- | --- |
| `sort` | `recent` (default) orders by `updated_at`, which publication, unpublication and content edits move. `followers` orders by follower count. `plays` orders by plays started from the playlist on the last 30 UTC days, counting each day once it is materialized (see Play attribution). Ties fall back to playlist ID, all descending. |
| `artist` | An `artist_` ID or a bare catalog artist UUID. Keeps playlists with at least one live entry for one of that artist's tracks. |
| `limit` | 1–50, default 25. |
| `cursor` | The previous page's `next_cursor`. It is valid only with the `sort` that produced it. |

Each item carries `playlist_id`, `public_code`, `name`, `description` (the
author's Markdown, or null), `owner` (`id`,
`username`), `track_count`, `duration_seconds`, `updated_at`,
`follower_count`, `recent_play_count` and `artist_ids` (the typed IDs of the
artists its tracks belong to, the artist with the most tracks first; tracks
missing from the catalog add none). A null `next_cursor` ends the
listing. Counts can change between pages, so a playlist may move across the
cursor and be skipped or repeated. An invalid query returns
`422 invalid_query`.

`duration_seconds` and the artist filter come from catalog facts the User
Service rebuilds from `public.source_tracks` in the same transaction as every
playlist revision. A track missing from the catalog counts in `track_count`
but adds no duration or artist. A catalog change to a track's artist or
duration reaches the playlist at its next revision.

### Play attribution

Plays started from a playlist record its `playlist_id` in
`public.source_track_plays`. Signed-in plays get it from the cloud play
history upload. Anonymous clients pass `playlist_id` to
`POST /api/v4/live/play` (JSON) or `POST /api/v2/live/play` (query). The
catalog checks only that the value is a canonical `playlist_` ID and never
returns it. The `source_track_plays_playlist_daily` continuous aggregate
totals plays per playlist and UTC day, and the `plays` sort reads only its
materialized days. A day is materialized an hour after it closes (and re-closed
for three days to pick up late uploads), so a play counts one to 25 hours after
it starts.

Anonymous attribution is unauthenticated, like the track play counts it sits
beside. Nothing checks that the track is in the playlist or limits plays per
playlist, so the `plays` ordering can be inflated. That is accepted for now;
add per-client limits before using it for anything beyond browse ordering.

## Moderation

Run these commands with the User Service image and its accounts migration
connection pointed at the writer. Set `Temporal__Address` and
`Temporal__Namespace` for takedowns and lifts to wake playlist indexing.
The commands exit without starting the HTTP service.

```sh
dotnet RelistenUserService.dll playlist-moderation list-reports
dotnet RelistenUserService.dll playlist-moderation close-report <report_id>
dotnet RelistenUserService.dll playlist-moderation take-down <playlist_id>
dotnet RelistenUserService.dll playlist-moderation lift-takedown <playlist_id>
```

List returns the 100 oldest open reports. Closing a report allows that listener
to report the playlist again, subject to the same rate limit. Takedown hides
public reads, blocks new follows and copies, and blocks the owner from
publishing. The owner can still edit privately. Lifting a takedown restores
public access if the playlist remains published and unarchived. Close the
reviewed reports separately. Existing copies keep their contents and state.
Takedown and lift send the owner a new summary but leave its `updated_at`
alone, so operator actions do not reorder the owner's list. An owner's publish
or unpublish does update it.

## Rollout and recovery

Sharing is available when this User Service build is deployed. Ship account
deletion before deploying sharing. Apply the accounts migration before running
the moderation commands.

This release is roll-forward only. The previous build reads stored library
summaries and revision envelopes with `UnmappedMemberHandling.Disallow`. Writes
that use no guide or sharing feature keep the old shape, but the first publish,
takedown, text block or description writes a summary or envelope that build
rejects. The guide migration's `Down` also refuses to run. No switch disables
guide or sharing writes, so fix forward instead of rolling back.

The accounts migration adds `playlists.track_count` with a default of 0 and
backfills it. Servers still running the previous build during the rollout
write new or edited playlists without updating it. After the rollout, repair
those rows on the writer:

```sql
UPDATE user_data.playlists AS playlist
SET track_count = live.track_count
FROM (
    SELECT p.id, count(e.id)::integer AS track_count
    FROM user_data.playlists AS p
    LEFT JOIN user_data.playlist_entries AS e
        ON e.playlist_id = p.id AND NOT e.deleted AND e.track_id IS NOT NULL
    GROUP BY p.id
) AS live
WHERE playlist.id = live.id AND playlist.track_count <> live.track_count;
```

The repair changes only the stored count. Library summaries already sent keep
their old `track_count` until the playlist's next summary-writing change.

Public playlist reads can omit credentials. A supplied invalid bearer token or
revoked native session returns HTTP 401, including on public routes.

Cloudflare needs a cache eligibility rule for the public snapshot GET route.
Bypass requests carrying cookies or authorization and honor the origin's
`CDN-Cache-Control: max-age=60`. The browser receives `Cache-Control: no-cache`.
Origin 404s, authenticated responses, and mutations use `private, no-store`.
Verify a cache hit for a public snapshot through Cloudflare, then take down
that playlist. Request the same URL without cache busting. It should return
404 within 60 seconds of the successful takedown.

For an account database restore, stop origin traffic and let public snapshots
expire before reopening it. Rotate the history generation using the existing
[restore procedure](private-playlists.md#after-restoring-the-user-service-database). A copied playlist
and its receipt are restored together. If an older backup predates both, a
retry can create a copy only if the restored source is public.

### Public listing rollout

Deploy the catalog API first. Its migration 26 adds
`source_track_plays.playlist_id` with a five-second lock timeout and creates
the daily playlist aggregate with no data. The User Service's play upload
writes that column, so it fails until the migration has run.

Migration 25 makes the aggregate materialized-only, so listings never read
raw plays; play counts stay at zero until the first refresh after a day with
playlist plays closes.

The User Service migration adds `playlist_artists` and the
`catalog_duration_seconds` and `catalog_indexed_revision` playlist columns.
Existing playlists start unindexed. After start, each User Service process
rebuilds every playlist whose indexed revision differs from its revision.
That covers the backfill and, from the last new pod to start, playlists the
previous build edited during the rollout. If the query below finds rows
afterwards, restart a pod to repair them.
Check with:

```sql
SELECT count(*) FROM user_data.playlists WHERE catalog_indexed_revision <> revision;
```

Cloudflare's cache rule for public snapshots must also match the bare
`/v1/public-playlists` path, with the query string in the cache key.

## Search

`GET /v1/search` searches the catalog and playlists in one request. Use the
accounts host with the native app's bearer token or the relative path `/v1/search`
with the website's session cookie. Callers with library-read access can also
find their own private playlists. Guests see public playlists. Responses use
`Cache-Control: private, no-store`.

Requests with an invalid or expired bearer token receive HTTP 401. When
a request carries a bearer token, the service verifies the native session
before running either search backend. An unavailable native session store returns
HTTP 503. Browser session lookup runs within the playlist search budget. If it
times out, catalog results can still be returned with playlist availability
`unavailable`.

The User Service requires a search-only Typesense key in
`Search__Typesense__SearchKey` outside Development. The worker uses its separate
indexing credential.

Render `results` in response order. Each row has a `kind`, a stable
`key` and the corresponding v4 resources. Store the envelope's `request_id`
with the page and derive the one-based position from the row's index. An exact
owned or followed playlist title can lead Explore before indexing. A playlist
row has a nested `playlist` card with its current name and curator.

A performance includes the song and show identity when there is a catalog song,
and the exact `preview_track` in every case. Open its source setlist focused on
that track. Resolve that same track ID through the catalog before adding it.
When that file contains multiple songs, the performance appears once and
`songs` lists the songs in file order. The `performance` summary names the
matched song if known and the file's first song otherwise.
A show row with a `source` opens that recording. Without it, use the listener's
source preferences. A `sequence` includes a `passage` with its chosen
`source_id`, the ordered files, and the song and track ID for each part.

The row's `popularity` uses the v4 listening windows, including `windows["1y"]`.
It describes the row itself: starts across the show for a show row, on the song's
files at one show for a performance, and on the selected source for a recording.
Reviews include the v4 review and the source it describes. A review requires a
title or body match.

`counts` gives exact totals for the corresponding scope tabs.
`count_lower_bounds` gives minimum totals when the searches already used to
build the page cannot establish an exact count. A lower bound of zero means
the response cannot establish whether that tab has any matches.
`readings.applied.chips` describe constraints enforced on the results.
`readings.alternatives` includes inferred interpretations that can be selected
to narrow the search. Send
`reading_id` with the original text when the listener chooses one and clear it
when the text changes.

Scoped results include `next_page` while the index has more matches.
Visibility checks can leave a page short or empty. Explore All returns one page
of up to 24 rows, or up to 48 for a focused song. `more_scopes` identifies
additional versions or recordings. Add Tracks searches the catalog and returns
up to 48 rows in All, with playable versions first unless a stronger exact
match has a drill-in.

`availability.catalog` and `availability.playlists` are independent. If
one is `unavailable`, display the available results with a retry or offline
fallback. Playlist availability can be `partial` when one of the indexed
or exact library searches fails. A scope that omits one half sets its
availability to `not_requested`. An explicit `artist_id` also filters
playlists by the artists on their tracks.

MCP `find` uses the same search. A valid `library.read` grant adds the listener's
own playlists to its results. The `sequences` array includes the titles and
recording targets of matched passages.
