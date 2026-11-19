# Private playlist contracts

Status: frozen 2026-11-16 by Alec, after a fresh-reader review; restore and
quota recovery amended the same day after two further reviews. Change it only
through the plan's Decision Log. This document freezes the wire contracts and
server invariants for delivery 1 (private playlists) of
[Deliver playlists using v4 catalog identities](2026-11-06-v4-playlists.md). The
plan owns product decisions and rationale; this document owns shapes, statuses and
ordering rules. Publication and collaboration add to these contracts later; nothing
here is dormant schema for them.

The API implementation is locally verified as of 2026-11-17. The plan's
2026-11-17 Decision Log entry records implementation clarifications for text,
validation, error precedence and generation on problems. See
[operations and recovery](../../api/private-playlists.md) before rollout.
The guide content amendment adds the fields and operations below at version 1.

The endpoint examples follow one playlist. Generate fresh TypeIDs for writes
against a shared service and use track IDs from your catalog.

## Conventions

These match the favorites contracts already served by `RelistenUserService`.

- Host and version: `https://accounts.relisten.net/v1/...`, authenticated with the
  existing library access policy. The playlist contract version starts at `1`.
- JSON is `snake_case`. Unknown members are rejected (`UnmappedMemberHandling.Disallow`).
  Absent and `null` differ only where a field says so.
- Errors are RFC 9457 problem bodies with a stable `code` extension, as
  `LibraryController.LibraryProblem` returns today. The framework's request-size
  `413` is the one exception and carries no `code`, as with favorites.
- A playlist the caller cannot access returns `404 playlist_not_found`, whether it
  is missing or owned by someone else.
- Every ID is a complete typed string. Client-created domain IDs are UUIDv7 and
  globally unique. `track_id` is a catalog `TrackId`, validated only syntactically.
- Playlist responses and library snapshot and changes responses carry
  `history_generation`. See [History generation](#history-generation).
- Receipts are scoped to the account that created them. Another account reusing an
  `operation_id` or `command_id` gets `409 idempotency_conflict`, never the stored
  result.

| Type | Prefix | Created by |
| --- | --- | --- |
| Playlist | `playlist_` | client |
| Segment | `playlist_segment_` | client |
| Entry | `playlist_entry_` | client |
| Operation | `playlist_operation_` | client |
| Command | `command_` (existing) | client |
| Track | `track_` (existing catalog ID) | catalog |

## Data model

A playlist holds an ordered list of **segments**. A segment holds an ordered list of
**entries**, and every entry belongs to exactly one segment. A lone track is a
one-track segment. An entry is one authored position: the same `track_id` may
appear in many entries. Segments have no `kind`; the plan's earlier `kind` field
and `initial_segments` name are dropped.

- An empty playlist is valid. A live segment needs a description, entries, or
  both. When an operation leaves it with neither, it tombstones the segment.
- A text block is an entry with no track ID and a nonblank description. Text
  blocks and tracks share ranks and entry quotas. Summary `entry_count` counts
  both, and `track_count` counts only entries that have a track ID.
- A prose-only segment has a description and zero live entries.
- Segments and entries keep their IDs across moves. An entry's `track_id` never
  changes; replacing a track is delete plus insert.
- Deleted segments and entries remain as tombstones for the playlist's lifetime. An
  ID that exists anywhere, live or tombstoned, can never be created again.
- Order is defined only by server-assigned ranks (see Ranks). Clients send anchors,
  never ranks.

### Limits

| Limit | Value | Error |
| --- | --- | --- |
| Playlists per user, including archived | 250 | `422 quota_exceeded` |
| Live entries per playlist | 5,000 | `422 quota_exceeded` |
| Live entries across all owned playlists, including archived | 25,000 | `422 quota_exceeded` |
| Operations per batch | 500 | `422 limit_exceeded` |
| Request body (batch or create) | 2 MiB | `413` |
| `name` | 1–100 characters after trimming | `422 invalid_playlist_operation` |
| Playlist metadata `description` | 0–1,000 characters | `422 invalid_playlist_operation` |
| Segment or entry `description` | 0–2,000 characters | `422 invalid_playlist_operation` |
| Segment or entry `description` content | No images, no raw HTML, and only `https:`, `#entry=…&t=…` or `#t=…` link destinations | `422 invalid_playlist_operation` |
| Prose-only segments per playlist | 100 | `422 quota_exceeded` |
| Total descriptions per playlist | 200,000 UTF-8 bytes | `422 quota_exceeded` |

Names and descriptions are trimmed and NFC-normalized. Character limits count
Unicode scalar values after normalization. Names need not be unique. An empty
description is stored as `null`; NUL and invalid Unicode are rejected.
The content rule parses the description as CommonMark, so image or HTML syntax
inside a code span is accepted. The server stores accepted text as sent and never
rewrites it; clients sanitize before rendering. See the
[guide content design](../../design/2026-11-18-playlist-guide-content.md#accepting-and-sanitizing-descriptions).
The byte limit covers all live descriptions, including playlist metadata. The
2 MiB request limit applies independently to the encoded JSON. Descriptions
that require heavy JSON escaping can still exceed the request limit.

The server accepts guide writes only when `Accounts__EnablePlaylistGuideContent=true`.
While it is off, new writes that supply segment or entry descriptions, create
text entries, or use either description update operation return
`422 playlist_guide_content_disabled`. Existing metadata and track-only writes
still work. Reads and exact receipt replays remain available.

## Endpoints

Start with a new account to reproduce the library revisions in these examples.

1. Create the playlist and read its snapshot at revision 1.
2. For the [mixed library feed example](#library-feed-additive-still-version-1),
   read the library snapshot and save its `next_cursor`. Then favorite a track.
3. [Apply the example insert](#apply-operations), then read playlist changes after
   revision 1. If you saved a library cursor, read library changes from that
   cursor. The list example shows the playlist after the insert.
4. Archive the playlist after reading the changes.

### Create a playlist

`POST /v1/playlists`

```json
{
  "contract_version": 1,
  "command_id": "command_01k8000000e00800000000000a",
  "playlist_id": "playlist_01k8000000e008000000000001",
  "metadata": { "name": "Cornell + friends", "description": null },
  "segments": [
    {
      "segment_id": "playlist_segment_01k8000000e008000000000002",
      "entries": [
        { "entry_id": "playlist_entry_01k8000000e008000000000004", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a" },
        { "entry_id": "playlist_entry_01k8000000e008000000000005", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4b" }
      ]
    }
  ]
}
```

`segments` may be empty. Array order determines initial rank order. A segment
with a nonblank `description` may send `entries: []`. Each entry accepts an
optional `description`. Omit `track_id` or send it as `null` to create a text block.

A segment can contain both tracks and text, for example:

```json
{
  "segment_id": "playlist_segment_01k8000000e008000000000002",
  "description": "## The opening run",
  "entries": [
    {
      "entry_id": "playlist_entry_01k8000000e008000000000004",
      "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a",
      "description": "Listen at 2:00."
    },
    {
      "entry_id": "playlist_entry_01k8000000e008000000000005",
      "description": "[Hear the transition](#entry=playlist_entry_01k8000000e008000000000004&t=2:00)"
    }
  ]
}
```

The server stores Markdown source. Clients resolve timestamp links using the
entry IDs and track durations.

Responses:

- `201` on first success and `200` on an exact replay of the same `command_id`.
  Both return the created playlist ID and revision with the current generation.

  ```json
  {
    "contract_version": 1,
    "history_generation": "019a0000-0000-7000-8000-000000000001",
    "playlist_id": "playlist_01k8000000e008000000000001",
    "revision": 1
  }
  ```

- `409 idempotency_conflict` if `command_id` was used with a different payload.
- `409 id_conflict` if the playlist, segment or entry IDs already exist. The body
  lists `conflicting_ids`.
- `422 quota_exceeded`, `422 invalid_playlist_operation`, `422 invalid_contract_version`,
  `422 playlist_guide_content_disabled`.

Creating a playlist produces revision `1`. Mobile sends dependent operations only
after this command is acknowledged; a lost response is retried with the identical
body.

### List playlists

`GET /v1/playlists?view=active` (or `view=archived`; default `active`)

For clients that want the current list without running library sync, such as a
web page or a one-off screen (Alec, 2026-11-16). Syncing clients use the library
feed instead.

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "playlists": [
    {
      "playlist_id": "playlist_01k8000000e008000000000001",
      "name": "Cornell + friends",
      "entry_count": 3,
      "track_count": 3,
      "archived": false,
      "revision": 2,
      "updated_at": "2026-11-16T19:40:03Z"
    }
  ]
}
```

Items use the same summary shape as the library feed, sorted by `updated_at`
descending. There is no paging, because the 250-playlist quota bounds the list.
The response is `Cache-Control: private, no-store`.

### Read a snapshot

`GET /v1/playlists/{playlist_id}/snapshot`

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "playlist_id": "playlist_01k8000000e008000000000001",
  "revision": 1,
  "archived": false,
  "role": "owner",
  "metadata": { "name": "Cornell + friends", "description": null },
  "segments": [
    {
      "segment_id": "playlist_segment_01k8000000e008000000000002",
      "rank": "a0",
      "entries": [
        { "entry_id": "playlist_entry_01k8000000e008000000000004", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a", "rank": "a0" },
        { "entry_id": "playlist_entry_01k8000000e008000000000005", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4b", "rank": "a1" }
      ]
    }
  ],
  "created_at": "2026-11-16T18:02:11Z",
  "updated_at": "2026-11-16T18:02:11Z"
}
```

Arrays are in rank order. The snapshot contains only live segments and entries,
and no catalog data. `role` is always `owner` in delivery 1. The owner can read an
archived playlist's snapshot. The response is `Cache-Control: private, no-store`.
Segments and entries carry nullable `description`, and text blocks return
`track_id: null`.

A device that has no local copy of a playlist always starts from its snapshot,
never from changes.

### Read changes

`GET /v1/playlists/{playlist_id}/changes?after_revision=1`

After the example insert, the delta adds the third entry between the two entries
in the snapshot.

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "playlist_id": "playlist_01k8000000e008000000000001",
  "head_revision": 2,
  "revisions": [
    {
      "revision": 2,
      "kind": "content",
      "changes": [
        {
          "type": "entry_upserted",
          "entry_id": "playlist_entry_01k8000000e008000000000006",
          "segment_id": "playlist_segment_01k8000000e008000000000002",
          "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4c",
          "rank": "a0V"
        }
      ]
    }
  ],
  "next_after_revision": 2,
  "has_more": false
}
```

Changes are state, not operations, so a client applies them without re-running
operation semantics:

| `type` | Fields | Meaning |
| --- | --- | --- |
| `metadata_updated` | `name`, `description` | Full current metadata. |
| `segment_upserted` | `segment_id`, `rank`, `description` | Full current segment state at this rank. |
| `segment_deleted` | `segment_id` | The segment and every entry still in it are gone. No separate `entry_deleted` is emitted for those entries. |
| `entry_upserted` | `entry_id`, `segment_id`, `track_id`, `rank`, `description` | Full current entry state. A text block has a null track ID. |
| `entry_deleted` | `entry_id` | The entry is gone. |

Within a revision, changes are listed in apply order: `segment_upserted` before
any `entry_upserted` that names that segment, and an entry's move out of a segment
before that segment's `segment_deleted`. Clients apply them in array order.
An absent or null `description` in an upsert means there is no description.

`kind` is `content` for edits and `rebalance` for server rank rewrites. A rebalance
changes only ranks and is complete on its own.

Paging: a page holds whole revisions only, never part of one. It is capped at 500
changes, except that a single revision larger than that is returned alone. Clients
continue from `next_after_revision` while `has_more` is true.

| `after_revision` (head is 2) | Result |
| --- | --- |
| 1 | Revision 2. |
| 2 | `200` with empty `revisions`. |
| 3 or more | `410 snapshot_required`. The server history is behind the client, for example after a backup restore. |
| 0 | `410 snapshot_required`. Revision 1 (create) has no change envelope; start from a snapshot. |

`410 snapshot_required` is also returned if `after_revision` is older than retained
history. Nothing is compacted in delivery 1, but clients must handle it (see Client
model). The plan's earlier `409 snapshot_required` is replaced by `410`, matching
the library feed's `410 sync_cursor_expired`.

### History generation

Revision numbers alone cannot detect a restore. Suppose device A holds revision 43
containing entry X, the server is restored to revision 40, and device B then makes
three edits that bring the server back to 43. A would pull after 43, get an empty
page, keep X and miss B's edits. Every later delta would apply to the wrong base.

So the User Service keeps one server-wide **history generation**: an opaque string
stored in a single-row `user_data.history_generation` table. It never changes in
normal operation.

- Every playlist response (create, snapshot, changes, batch, archive and list) and
  every library response (snapshot and changes) includes `history_generation`.
- Domain and body-validation problem bodies also carry the current generation.
  Framework authentication/authorization, content-type and request-size failures
  may omit it. A replay preserves its stored outcome while its response envelope
  carries the current generation, not the one from the original receipt.
- Mobile stores the generation with each playlist base and with its library cursor.
  Before applying any response, it compares the generation. On a mismatch it
  applies nothing from that response and runs restore recovery (see Client model),
  whatever the revision numbers say.
- The library cursor also embeds the generation, so the server answers a cursor
  from another generation with `410 sync_cursor_expired`. This also protects
  favorites, which share the same revision-reuse problem.
- **Restoring the User Service database from any backup or point in time must
  rotate the generation** (write a new UUIDv7) before the service takes traffic.
  This step belongs in the account recovery runbook. A restore that skips it is
  unsafe for every syncing client.

Test: revision reuse after a restore, before an offline device reconnects. The
device must replace its base by snapshot even though the revisions compare equal.

### Apply operations

`POST /v1/playlists/{playlist_id}/operations:batch`

```json
{
  "contract_version": 1,
  "operations": [
    {
      "operation_id": "playlist_operation_01k8000000e00800000000000b",
      "base_revision": 1,
      "operation_type": "insert_entries",
      "payload": {
        "segment_id": "playlist_segment_01k8000000e008000000000002",
        "after_entry_id": "playlist_entry_01k8000000e008000000000004",
        "before_entry_id": "playlist_entry_01k8000000e008000000000005",
        "entries": [
          { "entry_id": "playlist_entry_01k8000000e008000000000006", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4c" }
        ]
      }
    }
  ]
}
```

`base_revision` is required and must be a non-negative integer, but it is
diagnostic only. It records the revision the client had when it authored the
operation, and the server never rejects or reorders by it.

Response `200`, one result per operation in request order:

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "head_revision": 2,
  "results": [
    { "operation_id": "playlist_operation_01k8000000e00800000000000b", "status": "applied", "revision": 2 }
  ]
}
```

- `revision` is the content revision the operation produced, or `null` when it
  changed nothing.
- `head_revision` is the playlist's head after the request. It may exceed every
  result `revision` because of rebalances or other devices' edits. For a batch made
  entirely of replays, it is the current head.
- Results never carry structure. Clients learn the canonical state by pulling
  changes.

Whole-batch failures apply nothing:

| Status | `code` | When |
| --- | --- | --- |
| 404 | `playlist_not_found` | Missing or not accessible. |
| 409 | `playlist_archived` | The playlist is archived. Every operation type is refused, including `update_metadata`. Replays are still answered (see Transactions). |
| 409 | `idempotency_conflict` | An `operation_id` was used before with a different payload, playlist or account. Body lists `conflicting_operation_ids`. |
| 422 | `invalid_contract_version` | Unsupported version. |
| 422 | `invalid_playlist_operation` | Malformed envelope or payload, such as a wrong prefix, an unknown type or field, empty `entries`, or a duplicate `operation_id` or created ID within the request. |
| 422 | `quota_exceeded` | The state after the batch would exceed a limit (see Quota preflight). |
| 422 | `playlist_guide_content_disabled` | The write switch is off and the batch contains a new guide write. |
| 422 | `limit_exceeded` | More than 500 operations. |

## Operations

### Placement anchors

Inserts and moves take optional `after_*_id` and `before_*_id` anchors among the
destination parent's children: segments for segment placement, entries in the
destination segment for entry placement. The server temporarily removes the moving
object, resolves anchors against the state before this operation tombstones
anything, then applies the first matching rule. An anchor equal to the moving
object is `invalid_playlist_operation`.

Placement never fails (Alec, 2026-11-16). Concurrent edits from other devices can
move or delete an anchor, and an offline addition must still land somewhere:

- An anchor that is tombstoned, or that now sits in a different parent, counts as
  absent.
- If both anchors are present but `after` no longer comes before `before`, only
  `after` counts.

Then:

1. Both present: place immediately after `after`. They need not be adjacent; if
   another device inserted items between them, the new item goes right after
   `after`, ahead of those items.
2. One present: place immediately after `after`, or immediately before `before`.
3. None present (none sent, or none survive): append at the end of the parent.

### Operation types

| `operation_type` | Payload | Effect |
| --- | --- | --- |
| `update_metadata` | `name?`, `description?` | Absent = unchanged. `description: null` clears it. `name` cannot be null. |
| `insert_segment` | `segment_id`, `after_segment_id?`, `before_segment_id?`, `description?`, `entries` | New segment with entries in array order. Entries can be empty if the description is nonblank. |
| `update_segment` | `segment_id`, `description?` | Absent leaves it alone. Null or blank clears it. Clearing a prose-only segment emits `segment_deleted`. |
| `move_segment` | `segment_id`, `after_segment_id?`, `before_segment_id?` | Reposition a segment with its entries. |
| `delete_segment` | `segment_id` | Tombstone the segment and all its live entries. |
| `insert_entries` | `segment_id`, `after_entry_id?`, `before_entry_id?`, `entries` (1–5,000) | Add entries into an existing segment, contiguous, in array order. |
| `update_entry` | `entry_id`, `description?` | Absent leaves it alone. Null or blank clears it on a track and returns `422 invalid_playlist_operation` on a text block. |
| `move_entry` | `entry_id`, `destination` | Move one entry. See below. |
| `delete_entry` | `entry_id` | Tombstone the entry. If the segment has neither live entries nor a description, also tombstone the segment. |

Both insert operations take the same `{entry_id, track_id?, description?}` entry
shape as create.

For `move_entry`, choose one of these payloads. The first moves an entry into
an existing segment. The second creates a segment containing the moved entry.

```json
{
  "entry_id": "playlist_entry_01k8000000e008000000000004",
  "destination": {
    "segment_id": "playlist_segment_01k8000000e008000000000002",
    "after_entry_id": "playlist_entry_01k8000000e008000000000005"
  }
}
```

```json
{
  "entry_id": "playlist_entry_01k8000000e008000000000004",
  "destination": {
    "new_segment_id": "playlist_segment_01k8000000e008000000000003",
    "after_segment_id": "playlist_segment_01k8000000e008000000000002"
  }
}
```

If the entry leaves its old segment with neither entries nor a description,
the server tombstones that segment after resolving anchors. The old segment can
still anchor the new one.

A new segment is placed between whole segments. Splitting a block in place, with
the pulled-out track landing between the two halves, is not expressible in v1; it
would need a later `split_segment` operation.

### Results

| `status` | Accepted? | When |
| --- | --- | --- |
| `applied` | yes | State changed; `revision` is set. |
| `no_op` | yes | Valid but changed nothing, such as a move to the same place or metadata equal to the current value. |
| `target_deleted` | yes | The segment or entry being moved, updated or deleted is tombstoned. Nothing changes. |
| `target_missing` | yes | That segment or entry never existed, for example because the operation that would have created it was rejected. Nothing changes. |
| `parent_missing` | no | The destination segment is missing or tombstoned. The server never recreates it. |
| `id_conflict` | no | A created ID already exists, live or tombstoned, anywhere. |

A per-operation rejection such as `parent_missing` leaves later operations
free to run. A whole-batch error such as clearing a text block's description
commits nothing. Each applied operation produces exactly one content revision.
Accepted no-ops and per-operation rejections produce none.

An exact replay of an `operation_id` returns its original stored result and
`revision`, even when it arrives inside a batch with new operations.

**Client handling of rejections.** `parent_missing` and
`id_conflict` mean the operation's intent was not applied. Mobile removes it from
the outbox, drops its local overlay, and shows a non-blocking notice for added
tracks that did not land ("2 tracks couldn't be added to Cornell + friends") with
a one-tap retry that re-adds them at the end as new entries. Accepted statuses
need no notice.

### Quota preflight

Limits apply to the state a batch leaves behind, not to its intermediate steps
(2026-11-16 review). Before applying anything, the server dry-runs the batch's new
operations in submitted order against the current state, exactly as they would
apply. Exact replays count nothing. The server checks the final live entry
counts, the number of prose-only segments, and the description bytes against
their limits. If any limit is exceeded, the whole batch fails with
`422 quota_exceeded`. Intermediate states may exceed a limit because the batch
commits atomically.

This lets a correction travel with the edit it fixes. A full playlist has segment
S holding only entry E; the user's pending insert of X into S failed quota, and the
user then deletes E. Mobile appends `delete_entry E` to the outbox and resends
`[insert X into S, delete E]`. The insert runs first, so S never empties and is not
tombstoned, and the final count is back within the limit.

Mobile therefore keeps one ordered outbox per playlist and never reorders or
splits it for quota:

- After `quota_exceeded`, mobile stops resending that playlist's outbox unchanged.
  It resends the whole pending outbox, in order, when the user adds another edit
  to it or when a deletion in another playlist syncs (the account limit is shared).
- The UI shows the limit until a resend succeeds or the user discards pending
  additions. There is no playlist delete in v1, so freeing space means deleting
  entries.
- A paused outbox larger than one batch (500 operations or 2 MiB) cannot be
  resent atomically. This needs hundreds of offline edits after hitting the limit,
  so it signals unusual use rather than a normal recovery path (Alec, 2026-11-16).
  Mobile keeps every pending edit, does not prompt the user to discard anything,
  and shows an alert asking them to contact team@relisten.net so the team can
  review the use and decide whether to raise the limit. It also records a
  diagnostic event so the team can see it happened.

Test: on one device, insert X into a full playlist's single-entry segment S, get
`quota_exceeded`, delete S's existing entry, and resend. X must apply into S and
S must stay live.

## Ranks

Ranks are base-62 strings over `0-9A-Za-z` (ASCII order), using the algorithm from
the `fractional-indexing` npm package by Rocicorp (`generateKeyBetween`). The server
ports it to C# with fixtures generated from the npm package, so both sides agree on
every key they generate.

- Segment ranks order segments within a playlist. Entry ranks order entries within
  their segment. Only live items are ranked.
- PostgreSQL columns are `text COLLATE "C"` with a unique constraint on
  `(parent, rank)` among live rows, so order is plain byte order.
- Mobile compares ranks with plain JavaScript string comparison (`<`), which equals
  byte order for ASCII. Realm `sorted()` on a rank property gives the same order:
  on 2026-11-16, Realm 20.2.0 under Node sorted 20,000 keys over this alphabet
  (every two-character pair plus random keys up to 12 characters) identically to
  byte order. Screens may query Realm sorted by rank. Keep that comparison as a
  mobile regression test so a Realm upgrade cannot silently change it.
- Maximum rank length is 48 characters. If placing an item would need a longer
  rank, the server first rebalances that parent's live children to evenly spaced
  short keys as a `rebalance` revision, then applies the operation as the next
  content revision with a short rank. A stored rank never exceeds 48 characters.
  The operation's result `revision` is the content revision.
- Mobile uses the npm package locally for optimistic placement. Its local ranks are
  display-only and are replaced by the server's when the change arrives.

## Archive

`PUT /v1/playlists/{playlist_id}/archive-state`

```json
{ "contract_version": 1, "command_id": "command_01k8000000e00800000000000c", "archived": true }
```

Response `200`:

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "playlist_id": "playlist_01k8000000e008000000000001",
  "archived": true,
  "changed": true
}
```

- An exact replay returns the stored result. Changed reuse of `command_id` is
  `409 idempotency_conflict`.
- Archive is online-only and never queued offline.
- It does not change the content revision; it changes the library summary.
- Archived playlists keep counting toward quotas. There is no delete endpoint in v1.

While a playlist is archived, other devices' pending operations for it stay queued
and get `409 playlist_archived`. Mobile shows the playlist as archived, keeps the
pending edits, and sends them after an unarchive. A user who does not want them can
discard them.

## Library feed (additive, still version 1)

Playlist summaries are added to the existing library feed without a version bump
(Alec, 2026-11-16). Routes, the `contract_version: 1` value, favorite shapes and
the favorite mutation endpoint are unchanged. `library_revision` is the existing
per-user counter on `user_data.library_states`. Favorites are dev-alpha only;
current alpha builds reject unknown change types and must update (see below).

`GET /v1/library/snapshot` includes active and archived playlists. This example
is from a new account just after the create request.

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "library_revision": 1,
  "next_cursor": "opaque",
  "favorites": [],
  "playlists": [
    {
      "playlist_id": "playlist_01k8000000e008000000000001",
      "name": "Cornell + friends",
      "entry_count": 2,
      "track_count": 2,
      "archived": false,
      "revision": 1,
      "updated_at": "2026-11-16T18:02:11Z"
    }
  ]
}
```

After favoriting a track and applying the example insert, call
`GET /v1/library/changes?after=<next_cursor>` with the cursor returned by the
library snapshot. The playlist change carries a `playlist` object. The favorite
change carries `favorite_id` and `target`.

```json
{
  "contract_version": 1,
  "history_generation": "019a0000-0000-7000-8000-000000000001",
  "library_revision": 3,
  "changes": [
    {
      "change_id": "library_change_01k8000000e00800000000000d",
      "revision": 2,
      "change_type": "favorite_added",
      "favorite_id": "favorite_01k8000000e00800000000000e",
      "target": { "kind": "track", "id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a" },
      "changed_at": "2026-11-16T19:35:00Z"
    },
    {
      "change_id": "library_change_01k8000000e00800000000000f",
      "revision": 3,
      "change_type": "playlist_summary",
      "playlist": {
        "playlist_id": "playlist_01k8000000e008000000000001",
        "name": "Cornell + friends",
        "entry_count": 3,
        "track_count": 3,
        "archived": false,
        "revision": 2,
        "updated_at": "2026-11-16T19:40:03Z"
      },
      "changed_at": "2026-11-16T19:40:03Z"
    }
  ],
  "next_cursor": "opaque",
  "has_more": false
}
```

- In C#, `FavoriteLibraryChange` becomes a library change record with optional
  `favorite_id`/`target` and optional `playlist`, and the snapshot record gains
  `playlists`. The favorite types can be renamed to `Library*` in the same change.
- `playlist_summary` is the full current summary (an upsert). Every committed
  create, archive change or content batch that changes a playlist writes one
  summary change for it in the same transaction: one per playlist per transaction,
  not one per operation.
- Mobile compares a summary's `revision` with its local `applied_revision` for that
  playlist. An unknown playlist is fetched by snapshot; a known one that is behind
  pulls changes.
- `playlist_removed` is reserved for later deliveries (lost access, account
  deletion) and is not emitted in delivery 1. Clients must ignore unknown
  `change_type` values from now on. Mobile's `favorite_sync_contract.ts` currently
  throws on them and must change.
- The existing page size (500) and `410 sync_cursor_expired` behavior are unchanged.

## Transactions and locks

Every playlist write (create, batch, archive) runs in one transaction. Locks follow
the favorites order (`FavoriteMutationService`):

1. Transaction advisory locks on every submitted `operation_id` or `command_id` and
   every ID the request would create, in sorted order. This turns concurrent reuse
   of an ID into a clean `idempotency_conflict` or `id_conflict` rather than a
   unique-key violation.
2. The requesting account's `user_data.library_states` row, `FOR UPDATE`. This
   serializes that account's quota checks and library revision updates. Favorites
   take the same lock.
3. For batch and archive requests, select the playlist by ID and owner and lock
   the matching row with `FOR UPDATE`. A missing match returns
   `404 playlist_not_found`.

An edit batch then follows these steps:

1. Look up receipts. Conflicting reuse fails the batch with `409 idempotency_conflict`.
   If every operation is an exact replay, return the stored results now, even if the
   playlist is archived.
2. For a batch with new operations, check archive state (`409 playlist_archived`).
3. Evaluate new operations in request order on a working copy and check quotas
   against its final state.
4. Write the final state, revision changes, receipts and one library summary change
   for a changed playlist.
5. Commit. An infrastructure failure rolls back the whole request.

Collaboration later locks other members' library states after the playlist, in ID
order. Account deletion later adds its fence ahead of the library state lock. Both
attach to this single write entrypoint.

## Receipts and hashing

Each create command, archive command and operation stores a receipt: account, ID,
playlist, SHA-256 semantic hash and stored result. Receipts are kept for the
playlist's lifetime.

The hash is SHA-256 over UTF-8 lines joined by `\n`, like `FavoriteMutationHasher`.
Value lines use tags so free text stays distinct from absent and null fields.

The v1 field order is explicit in `PlaylistHasher` and stays fixed if C# records
are later reordered. `insert_segment` and `insert_entries` encode the parent ID,
entries, after anchor, before anchor; create encodes name, description, segments.

| Line | Content |
| --- | --- |
| 1 | Domain: `relisten-playlist-operation-v1`, `relisten-playlist-create-v1` or `relisten-playlist-archive-v1` |
| 2 | Contract version |
| 3 | Playlist ID |
| 4 | `operation_type`, or `create` / `archive` for commands |
| 5+ | Encoded payload fields in the order defined by `PlaylistHasher` |

Field lines are encoded as follows:

- An absent field is written `A`.
- A `null` field is written `N`.
- A value is written `V:` followed by its JSON encoding. Strings are first trimmed
  and NFC-normalized exactly as stored, then encoded as JSON string literals, so
  embedded newlines and `|` are escaped.
- An array is written as a line `L:<count>`, then its elements. An entry starts
  with `entry_id|track_id`, or `entry_id|N` for a text block. A create segment
  starts with `S:<segment_id>|<entry count>`, then its description (if present),
  then its entries.
- In create and insert requests, a present normalized entry description adds a
  `V:` line immediately after its entry. In `insert_segment`, a present
  normalized segment description adds `segment_description:<JSON string>`
  after the anchors. The distinct tag keeps a segment description from hashing
  like an anchor when the last entry also takes a description. An absent
  description adds no line, so track-only request hashes stay the same.
- `update_segment` and `update_entry` encode the target ID, then the description
  patch using the `A`, `N`, and `V:` field encoding.
- A `move_entry` destination is `D:segment|<segment_id>`, followed by its two anchor
  lines, or `D:new_segment|<new_segment_id>`, followed by its two anchor lines.
- `operation_id`, `command_id` and `base_revision` are excluded.

Clients never compute or send hashes. Fixed vectors cover the original create,
`insert_entries` and archive hashes and the new guide payloads. HTTP batch tests
exercise receipt replay, including both description updates.

## Client model

These are the server's promises that mobile relies on, and the client behavior
they assume.

- Mobile keeps, per playlist, a **canonical base** (the last fully applied server
  state and its `applied_revision`) and an ordered **pending overlay** of unsent or
  unacknowledged operations. The UI shows the base with the overlay replayed on
  top. The overlay is rebuilt after every pull.
- `applied_revision` advances only by applying changes. If a device at 41 gets
  result revision 43 for its own operation while another device made 42, it still
  pulls after 41.
- Every overlay operation records the `history_generation` of its result. Revision
  numbers are compared only within one generation: a revision from another
  generation proves nothing.
- An operation stays in the overlay until a pull in the same generation reaches its
  result `revision`.
  Operations that were accepted without a revision leave the overlay when their
  result arrives.
- Each changes page, including its cursor, is applied in one Realm transaction.
- After a snapshot at revision R, drop overlay operations whose result is from the
  snapshot's generation and whose result revision is at most R; they are already
  in the snapshot. Keep the rest and replay them.
- **Restore recovery** runs on a `history_generation` mismatch, or on a `410`
  showing the client ahead of the server.
  1. Discard the library cursor and every playlist base.
  2. Mark every operation still in the overlay as unsent, including ones
     acknowledged under the old generation. Do not prune any of them yet. A create
     command still held locally is resubmitted first, because the restored server
     may not have the playlist.
  3. Resubmit them with their original `operation_id`s, in order. A receipt that
     survived the restore returns its stored result. Otherwise the operation
     applies fresh, and any IDs that already exist return `id_conflict` and are
     dropped. Every result now carries the new generation.
  4. Fetch a fresh library snapshot and fresh playlist snapshots, then prune with
     the same-generation rule above.
- Restore recovery covers only operations still in the local overlay. An edit
  whose canonical change had already arrived was retired from the overlay, and
  mobile keeps no journal to rebuild it. Edits newer than the restored backup that
  no device still holds are lost; the backup's recovery point bounds that loss.
- Tracks missing from Realm are hydrated through `POST /api/v4/catalog/resolve` in
  chunks of 500. Hydration never blocks applying changes and never deletes entries.
