# Review play reporting and public activity

The local host now implements `POST /api/v4/live/play` and
`GET /api/v4/live/history`. These share the existing anonymous telemetry table;
they do not write account history or favorites. No schema migration is required.

Open [Swagger](http://127.0.0.1:43823/api-docs-v4/index.html) and expand
`CatalogLive`. The request exposes `app_type` as the string enum
`ios`, `web`, `sonos`, `android`.

1. Inspect this real [playable track](http://127.0.0.1:43823/api/v4/source-tracks/track_7b6v6pgvzqfvhd0s7avr18bdw5).
2. Use Swagger's **Try it out** for `POST /api/v4/live/play` with:

   ```json
   {
     "track_id": "track_7b6v6pgvzqfvhd0s7avr18bdw5",
     "app_type": "web"
   }
   ```

   This writes one play to the local database. The 200 response contains
   `sequence`, `played_at`, `track_id`, and `app_type`. An optional `client_uuid`
   accepts a plain telemetry UUID, independent of account IDs. It is not echoed.
3. Open [recent activity](http://127.0.0.1:43823/api/v4/live/history?limit=20).
   Each event has the same v4 `track` resource and a `show` summary. The imported
   track title stays intact; `track.normalization` contains the interpretation.
   Source, set, show, and artist IDs remain on the track. Artist names come from
   the separately cached artist catalog.
4. Poll `/api/v4/live/history?after_sequence=<sequence>&limit=20`. With no newer
   events it returns `[]`. Submit another play, then poll with the first sequence
   to see the new event. Both routes use `no-store` and return no ETag.
5. Try a wrong ID prefix or unsupported app value: 422 ProblemDetails. Try a
   well-formed nonexistent track: 404. These requests record no play.

To review artist filtering, open [Grateful Dead activity](http://127.0.0.1:43823/api/v4/live/history?artist_ids=artist_3qmp7zjbg1rpebhtyfy43096vj&limit=20),
or combine it with [Phish activity](http://127.0.0.1:43823/api/v4/live/history?artist_ids=artist_3qmp7zjbg1rpebhtyfy43096vj&artist_ids=artist_6aaf982021nrst0m68ew1djerc&limit=20).
Repeat `artist_ids` for any selected artists. Omit it for the global feed. The
filter applies before the limit and cursor paging, including for imported-only
artists. Duplicates are harmless; valid IDs without recent plays yield no events;
empty or invalid values return 422. Local review plays expire after one hour.

Public activity covers the last hour. An initial request selects the latest
20 events; polling selects subsequent events. Both return ascending sequence.
Positive limits cap at 2000. Filtering unavailable recordings happens before the
limit so they cannot block a later valid event. No request loads every recording
for an artist or hydrates each event separately.

Mobile can append and deduplicate these events by sequence, and periodically
replace the recent window with a request without a cursor. This best-effort activity
window is separate from personal playback history and full artist snapshots.
Keep a separate window/cursor for each artist selection. When the selection
changes, load a fresh window without `after_sequence`.
Replica delay and overlapping commits can omit events from a cursor poll; this
endpoint makes no durable-delivery guarantee. Retrying a POST after a lost response
can record a duplicate play, matching existing telemetry behavior.

The preview starts no workers, so local review plays do not replace the saved
real aggregate ranking snapshot. API tests write to a disposable database and
clean up their event rows. Production telemetry is unchanged.
