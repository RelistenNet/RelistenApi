# Relisten Playlists & User Accounts — Design Spec

**Date:** 2026-04-11
**Status:** Draft
**Prerequisite:** Realm → TanStack DB migration (separate spec)

## Overview

Add user accounts, playlists with shuffle-aware blocks, collaborative editing, sharing, favorites/history sync, and the foundation for future features (radio, AT Protocol, social) to Relisten.

Relisten playlists differ from Spotify-style playlists because of live music and segues. The smallest unit is still an individual track, but tracks can be grouped into **blocks** — a segue, a jam, a good run from a show. When shuffling, blocks stay together and play in internal order. This preserves the musical continuity that makes live recordings special.

### Goals

- Let users build curated collections of jams, segues, and show runs across artists
- Shuffle playlists while keeping segues and blocks intact
- Support collaborative curation (e.g., "best jams of a tour")
- Sync favorites and playback history across devices
- Share playlists via URL with web playback, rich embeds, and embeddable player
- Run cheaply on existing infrastructure (free app, no revenue)
- Design for future radio, AT Protocol, and social features without building them now

### Non-Goals (v1)

- Relisten Radio (designed for, not built)
- AT Protocol / Bluesky integration (designed for, not built)
- Push notifications
- Social features / friends / activity feeds
- Playlist discovery / search across all public playlists
- User-submitted reviews
- Full Realm → TanStack DB migration (separate spec)

---

## 1. Architecture

### System Topology

```
┌─────────────────────┐     ┌──────────────────────────┐
│   Existing API      │     │    User Service API       │
│   (.NET 10, Dapper) │     │    (.NET, Dapper)         │
│                     │     │                           │
│   Read-only         │     │   Auth, playlists,        │
│   catalog data      │     │   favorites, history,     │
│                     │     │   profiles, operations    │
└──────────┬──────────┘     └─────────────┬─────────────┘
           │                              │
      ┌────┴─────┐                  ┌─────┴──────┐
      │ Catalog  │                  │   User     │
      │ Postgres │                  │  Postgres  │
      │(existing)│                  │   (new)    │
      └──────────┘                  └────────────┘
           │                              │
           └────────────┬─────────────────┘
                        │
              ┌─────────┴──────────┐
              │   Mobile / Web     │
              │   TanStack DB      │
              │  (local SQLite)    │
              └────────────────────┘
```

### Why Separate Databases

The existing catalog Postgres is a read-heavy cache rebuilt by importers. User data is write-heavy and mutable. Separating them provides:

- **Independent scaling** — indexer writes are bursty (daily imports with large batch updates). User writes are steady. Bursty indexer writes won't impact user data latency.
- **Independent backups** — user data is irreplaceable and backed up aggressively. Catalog data can be rebuilt from upstream sources (archive.org, phish.in, etc.).
- **Clean separation of concerns** — catalog is disposable cached data; user data is precious.
- **Future flexibility** — can move user Postgres to a separate server if needed.

### Cross-Database References

User data references catalog data by UUID (no foreign key constraints). Trade-offs:

- **No cross-DB JOINs** — hydration requires two queries (fetch user data, batch-fetch catalog data by UUID). This matches the mobile app's existing pattern of assembling data from API responses.
- **Dangling references** — if a source/track is removed from the catalog (e.g., removed from archive.org), playlist entries become unresolvable. Display as "track no longer available" in the UI.
- **No cross-DB transactions** — not needed. No operation requires atomicity across both databases.
- **Playback attribution** — play records in the user DB include a nullable `playlist_id`. No FK enforcement, but the data is in one database so it's queryable.

### User Service Tech Stack

- **.NET** (same as existing API) — one language, shared patterns, small team
- **Dapper** for data access (consistent with existing API)
- **Separate Postgres instance** for user data
- **Redis** for rate limiting, session cache
- **Hangfire** for background jobs (batch aggregation, cleanup)

---

## 2. Authentication

### Auth Methods

Four sign-in options, all passwordless:

1. **Apple Sign-In** — one-tap on iOS, also works on web via Apple JS SDK
2. **Google Sign-In** — one-tap on Android, also works on web via Google Identity Services
3. **Email magic link** — enter email, receive a sign-in link. For users who prefer a provider-independent option. Uses AWS SES (~$1-2/month at scale).
4. **Passkeys (WebAuthn)** — modern passwordless auth, stored in device/browser. Provider-agnostic, aligns with open-source ethos.

### Auth Flow

All auth goes through a web-based flow. The mobile app opens a web view to `relisten.net/auth`. Web users authenticate directly.

```
Mobile app                    relisten.net/auth               User Service API
    │                              │                               │
    ├──opens webview──────────────>│                               │
    │                              ├──user picks method───────────>│
    │                              │  (Apple/Google/magic link/    │
    │                              │   passkey)                    │
    │                              │                               │
    │                              │<──JWT token pair──────────────┤
    │<──deep link with tokens──────┤                               │
    │                              │                               │
    ├──API calls with access token────────────────────────────────>│
    │                                                              │
    ├──refresh token when expired─────────────────────────────────>│
```

### Token Model

- **Access token** — JWT, 1 hour expiry. Contains user ID, username. Sent with every API request.
- **Refresh token** — opaque, 1 year expiry. Stored securely (Keychain on iOS, Keystore on Android, httpOnly cookie on web). Used to obtain new access tokens.

### First-Time Signup

1. User authenticates via chosen provider
2. Prompted to choose a username (validated in real-time — see username rules in Section 7)
3. Optional display name
4. Account created, tokens issued

### Multiple Auth Methods

Users can link additional sign-in methods from settings. All methods link to the same account. Adding Bluesky/AT Protocol auth in the future requires no schema changes.

---

## 3. User Profiles

### Schema

```sql
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username TEXT NOT NULL UNIQUE,
    display_name TEXT,
    email TEXT UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE user_auth_methods (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    provider TEXT NOT NULL, -- 'apple', 'google', 'email', 'passkey'
    provider_id TEXT NOT NULL, -- provider-specific identifier
    credential_data JSONB, -- passkey public key, etc.
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (provider, provider_id)
);
```

### Profile Fields

- **Username** — required, unique, 3-30 characters, alphanumeric + underscores, case-insensitive uniqueness. See Section 7 for validation rules.
- **Display name** — optional, max 50 characters. Falls back to username when not set.
- **No avatar uploads** — avoid hosting user-generated images. Use generated identicons or initials client-side.

---

## 4. Playlists & Blocks

### Core Concepts

- **Playlist** — an ordered collection of entries, owned by a user
- **Entry** — a reference to a specific track (by UUID) at a position in the playlist. The same track can appear multiple times (different entries, different positions).
- **Block** — a group of entries that stay together during shuffle. Identified by a shared `block_id` on entries. Entries within a block must be positionally contiguous.
- **Fractional indexing** — positions are text strings that support arbitrary insertion without rewriting other entries (e.g., "a", "aM", "b"). Used for both entry ordering and block positioning.

### Shuffle Behavior

1. Group entries by `block_id` (null `block_id` = standalone track = its own shuffle unit)
2. Shuffle the groups
3. Within each group, play entries in `position` order

A block might be Scarlet Begonias > Fire on the Mountain from a Grateful Dead show — shuffling moves the pair together, never separates them. Or it might be a 40-minute run from a show — the user decides granularity.

### Block Terminology

"Block" is the internal/data model term. The user-facing label is TBD — candidates include "segment," "group," "flow," or "run." The data model and API use "block" consistently; the UI can map to whatever resonates.

### Schema

```sql
CREATE TABLE playlists (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    short_id TEXT NOT NULL UNIQUE, -- URL-friendly, 8-10 chars (e.g., "Xk9mPq2v")
    owner_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    description TEXT,
    is_public BOOLEAN NOT NULL DEFAULT false,
    archived_at TIMESTAMPTZ, -- soft delete
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE playlist_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    playlist_id UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    track_uuid UUID NOT NULL, -- references catalog DB source_tracks.uuid (no FK)
    position TEXT NOT NULL, -- fractional index
    block_id UUID, -- nullable; shared UUID groups entries into a block
    added_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_playlist_entries_playlist_id ON playlist_entries(playlist_id);
CREATE INDEX idx_playlist_entries_block_id ON playlist_entries(block_id);

CREATE TABLE playlist_collaborators (
    playlist_id UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role TEXT NOT NULL DEFAULT 'editor',
    invited_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    accepted_at TIMESTAMPTZ, -- null = pending invitation
    PRIMARY KEY (playlist_id, user_id)
);

CREATE TABLE playlist_followers (
    playlist_id UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    followed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (playlist_id, user_id)
);
```

### Content Limits

| Field | Limit |
|---|---|
| Playlist name | 200 characters |
| Playlist description | 1000 characters |
| Entries per playlist | 2000 |
| Playlists per user | 200 |
| Collaborators per playlist | 20 |

---

## 5. Operations & Edit Log

Operations are the write path for playlist mutations. The server applies each operation to the materialized state in `playlist_entries` and logs it to `playlist_edit_log`. The edit log is a first-class data structure — not just an audit trail — because it maps to future AT Protocol records where each user owns their own operations.

### Schema

```sql
CREATE TABLE playlist_edit_log (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    playlist_id UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id),
    operation JSONB NOT NULL, -- full operation payload
    idempotency_key UUID NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_playlist_edit_log_playlist_id ON playlist_edit_log(playlist_id);
```

### Operation Catalog

Every operation is:
- Attributed to a user (`user_id`)
- Idempotent (via `idempotency_key` — replays are safe)
- Self-contained (no implicit state dependencies)
- Logged to `playlist_edit_log` with the full payload
- Applied to materialized state in `playlist_entries`

#### Playlist Lifecycle

**`create_playlist`**
```json
{
    "op": "create_playlist",
    "name": "Best Phish Jams 2024",
    "description": "Top segues from summer tour"
}
```

**`update_playlist`**
```json
{
    "op": "update_playlist",
    "playlistId": "uuid",
    "name": "Best Phish Jams Summer 2024",
    "description": "Updated description",
    "isPublic": true
}
```

**`archive_playlist`** (soft delete)
```json
{
    "op": "archive_playlist",
    "playlistId": "uuid"
}
```

#### Entry Operations

**`add_track`** — add a single track to the playlist
```json
{
    "op": "add_track",
    "playlistId": "uuid",
    "trackUuid": "catalog-track-uuid",
    "position": "aM",
    "blockId": null
}
```

**`remove_entry`** — remove an entry from the playlist
```json
{
    "op": "remove_entry",
    "playlistId": "uuid",
    "entryId": "entry-uuid"
}
```

**`move_entry`** — reposition a track, optionally changing its block membership
```json
{
    "op": "move_entry",
    "playlistId": "uuid",
    "entryId": "entry-uuid",
    "newPosition": "cG",
    "blockId": "block-uuid-or-null"
}
```
- `blockId: null` removes the entry from any block (standalone)
- `blockId: "some-uuid"` moves the entry into that block at the specified position
- Server validates that the new position maintains block contiguity

#### Block Operations

**`create_block`** — group consecutive entries into a block
```json
{
    "op": "create_block",
    "playlistId": "uuid",
    "entryIds": ["entry-1", "entry-2", "entry-3"]
}
```
Server assigns a new `block_id` UUID and reassigns entries to contiguous fractional positions if needed.

**`dissolve_block`** — ungroup entries (entries keep positions, `block_id` set to null)
```json
{
    "op": "dissolve_block",
    "playlistId": "uuid",
    "blockId": "block-uuid"
}
```

**`move_block`** — reorder an entire block atomically
```json
{
    "op": "move_block",
    "playlistId": "uuid",
    "blockId": "block-uuid",
    "newPosition": "m"
}
```
All entries in the block get new contiguous fractional positions at the target location.

#### Compound Operations

**`add_tracks_as_block`** — add multiple tracks as a block in one atomic operation (the "add this segue" flow)
```json
{
    "op": "add_tracks_as_block",
    "playlistId": "uuid",
    "trackUuids": ["track-1", "track-2", "track-3"],
    "position": "g"
}
```
Server creates new entries with a shared `block_id` at contiguous positions starting from the specified position. Always creates new entries — if a track already exists in the playlist, this adds another instance (tracks can appear multiple times).

#### Collaboration Operations

**`invite_collaborator`**
```json
{
    "op": "invite_collaborator",
    "playlistId": "uuid",
    "username": "jake"
}
```

**`remove_collaborator`**
```json
{
    "op": "remove_collaborator",
    "playlistId": "uuid",
    "userId": "user-uuid"
}
```

**`accept_invite`**
```json
{
    "op": "accept_invite",
    "playlistId": "uuid"
}
```

### Contiguity Enforcement

Block contiguity (entries in a block occupy an unbroken position range) is enforced by the server at operation application time:

- `create_block` reassigns entries to adjacent fractional positions
- `add_track` with a `blockId` assigns a position within the block's current range
- `move_entry` into a block validates the position falls within the block's range
- `move_entry` of a non-block entry between two entries in the same block is rejected (would split the block)
- `move_block` reassigns all member positions atomically at the target location

The data model itself does not enforce contiguity — the operation application logic does. This is analogous to how a database doesn't enforce business rules, the application does.

### AT Protocol Compatibility

The operation log is designed to map to AT Protocol records:

```
User A's PDS:
  app.relisten.playlist.op/1 → { op: "create_playlist", ... }
  app.relisten.playlist.op/2 → { op: "add_track", ... }

User B's PDS (collaborator):
  app.relisten.playlist.op/1 → { op: "add_track", ... }
```

Each user owns their operations. The server aggregates operations from all collaborators and materializes the current playlist state. This is future work — v1 stores the log server-side — but the stable, self-contained operation schemas are designed with this in mind.

---

## 6. Sync & Offline

### Client Data Layer

TanStack DB replaces Realm as the client-side data layer. The core data layer is React-agnostic — collections and live queries are plain TypeScript objects instantiated at app startup. React hooks (`useLiveQuery`) are a thin wrapper. This supports CarPlay, background sync, and any non-React consumer.

**Prerequisite:** the Realm → TanStack DB migration for catalog data is a separate project that should happen first. This spec assumes TanStack DB is the client data layer.

### Collections

```
User data collections (bidirectional sync with user service):
  playlists          — playlist metadata
  playlistEntries    — entries with positions and block IDs
  favorites          — entity type + entity UUID
  userSettings       — JSON settings blob

Local-only collections (batch upload, never download full history):
  playbackHistory    — local journal, batch-synced up

Catalog collections (read-only, fetch-on-navigate, etag-cached):
  artists, shows, sources, sourceTracks, sourceSets,
  venues, tours, years, eras, setlistSongs, reviews, etc.
```

Catalog collections follow the existing Realm pattern: fetch when the user navigates to a page, cache in local SQLite, use etag-based freshness checks to avoid redundant fetches. Not polling — on-demand with caching.

### Reactive Cross-Collection Joins

TanStack DB's live queries join catalog and user data reactively. When either side changes, only affected rows recompute (~0.7ms per row change via differential dataflow).

Example — artists with favorite status:

```ts
const artistsWithFavorites = liveQuery()
  .from(artists)
  .leftJoin(
    liveQuery().from(favorites).where({ entityType: 'artist' }),
    (artist, fav) => artist.uuid === fav.entityUuid
  )
  .select((artist, fav) => ({
    ...artist,
    isFavorite: fav !== null,
  }))
  .orderBy((a) => a.sortName)
```

This replaces Realm's `isFavorite` property directly on the Artist object. Favorites are their own collection; the join is computed client-side.

### React-Agnostic Data Layer

The data layer is shared across React UI, CarPlay, and background processes:

```
┌──────────────────────────────────────────────┐
│  Core data layer (plain TypeScript)          │
│                                              │
│  collections.ts — all collections            │
│  queries.ts — all live queries               │
│                                              │
│  Objects with .subscribe() and .state        │
│  No React dependency                         │
└───────────┬──────────────────┬───────────────┘
            │                  │
     ┌──────┴──────┐    ┌─────┴────────────┐
     │  React UI   │    │  CarPlay /       │
     │             │    │  Background      │
     │ useLiveQuery│    │                  │
     │  (hook)     │    │  query.subscribe │
     │             │    │  (callback)      │
     └─────────────┘    └──────────────────┘
```

### Sync by Data Type

| Data | Write path | Sync direction | Offline behavior |
|---|---|---|---|
| Playlists + entries | Operation → offline outbox → server → materialized state syncs back | Bidirectional | Queue operations locally, optimistic UI, replay on reconnect |
| Favorites | Toggle → offline outbox → server → syncs back | Bidirectional | Toggle locally, sync when online |
| Playback history | Write to local SQLite → batch upload | Up only (never downloads full history to devices) | Accumulates locally, batch syncs |
| User settings | Write → server → syncs to other devices | Bidirectional | Save locally, sync when online |
| Catalog data | Read-only from existing API | Down only | Cached in local SQLite, fetched on-navigate with etag |

### Offline Outbox (Playlist Operations & Favorites)

TanStack DB's `offline-transactions` package provides a persistent outbox pattern:

1. User performs a mutation (e.g., adds a track to a playlist)
2. TanStack DB applies optimistic state immediately (UI updates)
3. The operation is serialized and persisted to the outbox (AsyncStorage, survives app restart)
4. When online, the outbox replays operations in order against the user service API
5. Each operation has an idempotency key — replays are safe
6. Server applies the operation, returns authoritative state
7. TanStack DB reconciles optimistic state with server state

On app launch, the outbox loads pending operations and resumes sync.

### Offline Playback History (Batch Upload)

Playback history does not use the outbox — it's append-only, high-volume, and doesn't need optimistic UI or conflict resolution.

1. Track starts playing → write to local SQLite immediately
2. Entry includes `synced: false` flag
3. Background job batches unsynced entries when online
4. `POST /api/v1/history/batch` accepts up to 500 entries per request
5. Server deduplicates on `(user_id, track_uuid, played_at)`
6. On success, entries marked `synced: true` locally

This handles hundreds of offline listens on a trip — SQLite handles thousands of rows without issue.

### Real-Time Collaborative Sync

When viewing a collaborative playlist, the client opens a WebSocket to the user service. When any collaborator's operation is applied server-side, the server pushes the updated entries to all connected clients. TanStack DB's sync function receives the new state and the UI updates reactively.

When not actively viewing a playlist, no persistent WebSocket is held. Changes sync on next open via a standard fetch.

### Conflict Resolution

- **Favorites** — last-write-wins. Simple toggle, no meaningful conflict.
- **Settings** — last-write-wins.
- **History** — append-only, never conflicts.
- **Playlist operations** — server-received order wins. Operations are applied sequentially. If two collaborators edit offline and reconnect, their operations are interleaved in the order received. Fractional indexing minimizes position conflicts. In rare cases where the result isn't what either person intended, the operation log provides visibility and the UI allows correction.

### New Device Sync

When a user signs in on a new device:

1. **Favorites** — full download (small, high-value)
2. **Playlists** — playlist metadata and entries download (current snapshot, not operation history)
3. **Settings** — full download
4. **History** — NOT downloaded. "Recently played" screen queries the API. Local history starts fresh on the new device.
5. **Catalog** — builds up progressively as the user navigates (same as today)

---

## 7. Anti-Spam & Moderation

### Rate Limits

| Action | Limit | Notes |
|---|---|---|
| Create playlist | 10/hour per user | Prevents mass creation |
| Playlist operations (individual) | 60/minute per user | Normal editing headroom |
| Batch sync (offline replay) | 500 operations/request | Exempt from per-operation rate limit |
| Batch history upload | 500 entries/request | Offline trip sync |

Account creation rate limiting is handled by Cloudflare bot protection, not application-level limits.

### Username Validation

- 3-30 characters
- Alphanumeric + underscores only
- Must start with a letter
- Case-insensitive uniqueness (`Jake` and `jake` are the same)
- **Artist name blocking** — reject usernames matching any artist name or slug in the catalog DB (normalized comparison). Also block `{artist}_official`, `{artist}official`, `official_{artist}` patterns.
- **System reserved words** — admin, root, support, relisten, system, moderator, staff, help, official, playlist, api, auth, settings, etc.
- **Impersonation patterns** — reject anything ending in `_official`, `_support`, `_admin`, `_staff`
- **Profanity filter** — blocklist of slurs and offensive terms
- **OpenAI moderation API** — free, async check as a second layer

### Content Moderation Pipeline

Applied to playlist names and descriptions on create/update:

1. Character/format validation (length limits, basic regex)
2. URL detection — strip or reject URLs in descriptions to prevent spam links
3. Blocklist check (known spam patterns, slurs)
4. OpenAI moderation API (free, async) — flag for review if flagged, publish immediately if passed

### Abuse Handling

No manual moderation system in v1. The limits above are preventive. Abuse surface is limited because:
- No in-app playlist discovery or search (playlists spread via direct link sharing)
- No free-text content beyond playlist name and description
- No user-uploaded media

If needed in the future: soft-ban (user's playlists stop appearing for followers, collaborative edits paused).

---

## 8. Sharing, Following & Cloning

### Sharing

Playlists are **private by default**. The owner (or any editor) can share via URL:

```
https://relisten.net/playlist/{shortId}
```

The `short_id` is generated at playlist creation — 8-10 URL-safe characters (e.g., `Xk9mPq2v`).

On mobile, `relisten.net/playlist/{shortId}` deep-links into the app via existing app links.

### Access Levels

| Role | View | Edit | Share | Delete |
|---|---|---|---|---|
| Owner | Yes | Yes | Yes | Yes (soft delete) |
| Editor (collaborator) | Yes | Yes | Yes | No |
| Follower | Yes | No | Yes (re-share URL) | Can unfollow |
| Anonymous (link viewer) | Yes | No | Can copy URL | No |

### Web Experience

When someone opens a playlist link on relisten.net:

- **Full playlist view** — name, description, track list with show/artist info, creator username, editor attributions ("added by @jake")
- **Web playback** — play the playlist directly in the browser
- **Rich embeds** — Open Graph / Twitter Card meta tags: title, description, track count, total duration, creator. Generated preview image.
- **Embeddable player** — `relisten.net/embed/playlist/{shortId}` with compact form, similar to existing show embeds. Can be dropped into forums, Reddit posts, etc.
- **Deep link** — "Open in App" button. On mobile browsers, auto-opens the app.
- **Auth actions** — signed-in users see Follow / Clone buttons. Not signed in: read-only view with sign-in prompt.

### Following

"Follow" adds the playlist to the user's library as a live reference:

- Shows up under a "Following" section in the user's library
- Updates automatically when the owner or collaborators edit it
- Unfollow at any time
- If the owner archives (soft deletes) the playlist, it disappears from followers' libraries with a "no longer available" indication
- If the owner makes it private, existing followers keep access; new link viewers get "this playlist is private"

### Cloning

"Clone" creates a full independent copy owned by the cloning user:

- All entries and blocks are copied at the point-in-time of cloning
- `added_by` on all cloned entries is set to the cloning user (clean ownership, no false attribution)
- The clone's edit log starts fresh (a `create_playlist` and bulk add operation)
- Optional metadata: "Cloned from @username's [Original Name]" (attribution, not enforced)
- The clone is fully independent — edits to the original don't affect the clone

### Inviting Collaborators

1. Owner searches for a username, sends invite
2. Creates a `playlist_collaborators` row with `accepted_at: null`
3. Invitee sees the invitation in an "Invitations" section in their library (in-app only, no push notifications in v1)
4. Accept → `accepted_at` is set, editor access granted
5. Decline → row is deleted

### Privacy Transitions

- **Private → Public:** existing followers keep access, playlist becomes viewable by anyone with the link
- **Public → Private:** existing followers keep access, new link viewers get "this playlist is private"

---

## 9. Playback History & Analytics

### Schema

```sql
CREATE TABLE playback_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    track_uuid UUID NOT NULL, -- references catalog source_tracks.uuid
    source_uuid UUID NOT NULL, -- references catalog sources.uuid
    playlist_id UUID, -- nullable; which playlist was playing
    playlist_entry_id UUID, -- nullable; specific entry for block attribution
    played_at TIMESTAMPTZ NOT NULL,
    platform TEXT NOT NULL, -- 'ios', 'android', 'web', 'carplay', 'sonos'
    app_version TEXT NOT NULL, -- e.g., '4.2.1'
    device_id TEXT NOT NULL -- stable per-device identifier
);

CREATE INDEX idx_playback_history_user_id ON playback_history(user_id);
CREATE INDEX idx_playback_history_played_at ON playback_history(played_at);
CREATE INDEX idx_playback_history_playlist_id ON playback_history(playlist_id);
```

### Two Consumption Patterns

**Local device history (fast, offline):**
- "What was I listening to recently?" — query local SQLite, sorted by `played_at`
- Immediate, no network, works offline
- Day-to-day UX

**Aggregated cross-device stats (server-side, future):**
- "Your year in Relisten" / wrapped-style features
- Total listening time, top artists, top shows, listening streaks
- Computed server-side across all devices and platforms
- API endpoint: `GET /api/v1/history/stats?period=2025`
- The data model supports this from day one; the feature is built later

### Play Attribution for Radio (Future)

When a track plays from a playlist, we record `playlist_id` and `playlist_entry_id`. This enables:
- "Most played blocks across all playlists" — group by `playlist_id` + `block_id` (via entry → block_id lookup)
- Popular blocks become radio segments
- Batch aggregation job runs against this data periodically (Hangfire)

### Interaction with Existing Popularity Tracking

The existing catalog DB has `source_track_plays` for anonymous popularity (trending shows, momentum scores). The user service history is separate — per-user, attributed, richer. Both systems operate independently. The user service can optionally push anonymous play counts to the catalog DB for popularity computation, but that's an optimization, not a requirement.

---

## 10. Favorites Sync

### Schema

```sql
CREATE TABLE user_favorites (
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    entity_type TEXT NOT NULL, -- 'artist', 'show', 'track'
    entity_uuid UUID NOT NULL, -- references catalog UUID
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, entity_type, entity_uuid)
);
```

### Sync Behavior

- Favorites are a TanStack DB collection synced bidirectionally with the user service
- Toggling a favorite writes to the local collection (immediate) and queues a sync operation
- On new device sign-in, all favorites download immediately (small dataset)
- Favorites are joined with catalog data via TanStack DB live queries (no denormalization onto catalog objects)

### Migration from Realm

Currently `isFavorite` is a boolean flag on Artist, Show, and SourceTrack Realm objects. During migration:
1. Read all objects where `isFavorite == true` from Realm
2. Create corresponding `user_favorites` entries in TanStack DB
3. Remove `isFavorite` from catalog Realm objects (handled in the Realm → TanStack DB migration spec)

---

## 11. User Settings Sync

### Schema

```sql
CREATE TABLE user_settings (
    user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    settings JSONB NOT NULL DEFAULT '{}',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### Settings Stored

Settings are a JSON blob. Some settings are device-specific (not synced), others are account-wide (synced):

**Synced (account-wide):**
- Track listening history preference (always/never)
- Autoplay deep link behavior
- Source selection preference

**Not synced (device-specific):**
- Download via cellular data
- Offline mode
- Show offline tab
- Auto-cache settings (storage thresholds)
- Auto-cache delete strategy

The device-specific settings stay in local TanStack DB only. Account-wide settings sync via the user service.

---

## 12. API Surface

### User Service API Endpoints

All endpoints under the user service base URL. Auth required unless noted.

**Auth:**
- `POST /api/v1/auth/login` — initiate auth flow
- `POST /api/v1/auth/callback/{provider}` — OAuth callback (Apple, Google)
- `POST /api/v1/auth/magic-link` — send magic link email
- `POST /api/v1/auth/magic-link/verify` — verify magic link token
- `POST /api/v1/auth/passkey/register` — register a passkey
- `POST /api/v1/auth/passkey/authenticate` — authenticate with passkey
- `POST /api/v1/auth/refresh` — exchange refresh token for new access token
- `POST /api/v1/auth/logout` — revoke refresh token

**Users:**
- `GET /api/v1/users/me` — current user profile
- `PATCH /api/v1/users/me` — update display name
- `GET /api/v1/users/check-username/{username}` — availability check (no auth required)
- `POST /api/v1/users/me/auth-methods` — link additional auth method
- `DELETE /api/v1/users/me/auth-methods/{id}` — unlink auth method

**Playlists:**
- `GET /api/v1/playlists` — list user's playlists (owned, collaborating, following)
- `POST /api/v1/playlists` — create playlist
- `GET /api/v1/playlists/{shortId}` — get playlist with entries (no auth required if public). Supports `?hydrate=true` to return track/show/artist metadata from the catalog DB (used by web). Without hydration, returns raw UUIDs (used by mobile, which joins client-side via TanStack DB).
- `POST /api/v1/playlists/{shortId}/operations` — apply a single operation
- `POST /api/v1/playlists/{shortId}/operations/batch` — apply batch operations (offline sync)
- `GET /api/v1/playlists/{shortId}/log` — get edit log (owner/editors only)
- `DELETE /api/v1/playlists/{shortId}` — archive (soft delete, owner only)
- `POST /api/v1/playlists/{shortId}/follow` — follow a playlist
- `DELETE /api/v1/playlists/{shortId}/follow` — unfollow
- `POST /api/v1/playlists/{shortId}/clone` — clone to own library
- `WebSocket /api/v1/playlists/{shortId}/live` — real-time updates for collaborative editing

**Favorites:**
- `GET /api/v1/favorites` — list all favorites
- `PUT /api/v1/favorites/{entityType}/{entityUuid}` — add favorite
- `DELETE /api/v1/favorites/{entityType}/{entityUuid}` — remove favorite
- `POST /api/v1/favorites/batch` — batch sync favorites (offline)

**History:**
- `POST /api/v1/history/batch` — batch upload playback history
- `GET /api/v1/history/recent` — paginated recent history (cross-device)
- `GET /api/v1/history/stats` — aggregated stats (future)

**Settings:**
- `GET /api/v1/settings` — get synced settings
- `PUT /api/v1/settings` — update synced settings

**Invitations:**
- `GET /api/v1/invitations` — list pending invitations
- `POST /api/v1/invitations/{playlistId}/accept` — accept
- `POST /api/v1/invitations/{playlistId}/decline` — decline

---

## 13. Future Features (Designed For, Not Built)

These features are explicitly out of v1 scope. The architecture and data model are designed to support them without rearchitecting.

### Relisten Radio

Client-driven, not broadcast. Server pre-computes popular blocks via batch aggregation of `playback_history` entries attributed to playlists. Per-artist radio (e.g., "Phish Radio") filters blocks by artist. Client fetches a sequence of blocks, controls playback locally.

Future possibility: shared sequence across listeners (everyone gets the same order, different positions).

**What v1 provides:** playlist-attributed play tracking with `playlist_id` and `playlist_entry_id`, block-level data model, batch aggregation infrastructure via Hangfire.

### AT Protocol / Bluesky

Users could sign in with their DID. The `playlist_edit_log` maps to AT Protocol records — each operation is a record owned by the user in their PDS. The server becomes an aggregator: it reads operation records from users' PDSes and materializes playlist state.

**What v1 provides:** operation log as first-class data with stable schemas, idempotency keys, and user attribution. `users` table can accept a `did` column. Auth methods table supports adding new providers.

### Push Notifications

Collaborative playlist edits, invitation responses, "a playlist you follow was updated."

**What v1 provides:** the event model (operations, invitations, follows) that notifications would trigger from.

### Social / Friends

See what friends are listening to, follow users, friend activity feed.

**What v1 provides:** user profiles with usernames, playback history with attribution, public playlist infrastructure.

### Playlist Discovery / Search

Search public playlists by name, browse curated/popular playlists.

**What v1 provides:** `is_public` flag, playlist metadata, follower counts for future ranking.

### User-Submitted Reviews

Users write reviews of sources/shows, displayed alongside imported reviews. Potentially federated back to archive.org via their write API.

**What v1 provides:** user identity and auth infrastructure. Reviews would be a new collection in the user DB.

---

## 14. Deployment & Infrastructure

### New Components

- **User Service API** — .NET application, deployed as a Kubernetes deployment alongside the existing API. Separate Docker image.
- **User Postgres** — separate Postgres instance. Can start on the same server, migrate to dedicated hardware if needed.
- **WebSocket support** — for real-time collaborative playlist sync. Can use the same Kubernetes service with a sticky session or a lightweight WebSocket gateway.

### Environment Variables (User Service)

```
USER_DATABASE_URL=postgresql://user:pass@host:port/relisten_users
CATALOG_DATABASE_URL=postgresql://user:pass@host:port/app  (read-only connection to catalog DB)
REDIS_URL=redis://host:port
JWT_SECRET=...
APPLE_CLIENT_ID=...
APPLE_TEAM_ID=...
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
AWS_SES_REGION=...  (for magic link emails)
OPENAI_API_KEY=...  (for moderation API, free tier)
```

### Backup Strategy

- **User Postgres** — daily automated backups, point-in-time recovery. This is the precious data.
- **Catalog Postgres** — existing backup strategy unchanged. Data is rebuildable from upstream sources.
- **Redis** — ephemeral (rate limit counters, session cache). No backup needed.

---

## Appendix A: Glossary

| Term | Definition |
|---|---|
| **Block** | A group of playlist entries that stay together during shuffle. Internal term; user-facing name TBD. |
| **Entry** | A single item in a playlist, referencing a catalog track by UUID. Has its own ID — the same track can appear as multiple entries. |
| **Fractional index** | A text-based position value that supports arbitrary insertion between existing values without rewriting other positions. |
| **Operation** | An atomic, self-contained mutation applied to a playlist. Logged, attributed, and idempotent. |
| **Catalog** | The existing read-only indexed data (artists, shows, sources, tracks) in the existing Postgres database. |
| **User service** | The new API and database for user accounts, playlists, favorites, history, and settings. |
| **Outbox** | TanStack DB's offline-transactions queue that persists operations locally and replays them when connectivity is available. |
| **Short ID** | URL-friendly identifier for playlists (e.g., `Xk9mPq2v`), used in shareable links. |

## Appendix B: Open Questions

1. **User-facing term for "block"** — segment, group, flow, run? Needs user research / community input.
2. **Fractional indexing library** — which implementation to use for position strings? Several open-source options exist.
3. **WebSocket infrastructure** — dedicated gateway vs. integrated into user service? Depends on scale expectations.
4. **Magic link email sender** — AWS SES vs. Resend vs. Postmark? All are cheap at expected volume.
5. **Passkey implementation library** — which .NET WebAuthn library to use? (e.g., Fido2Net, passwordless.dev)
6. **TanStack DB stability timeline** — currently alpha (v0.6). Team relationship mitigates risk, but should track v1 timeline.
7. **Embeddable player design** — compact form factor for playlist embeds. Design work needed.
8. **Generated preview images** — what should the Open Graph image for a shared playlist look like? Needs design.
9. **Account deletion behavior** — `ON DELETE CASCADE` on `playlists.owner_id` means hard-deleting a user account hard-deletes their playlists (distinct from soft-delete/archiving). Collaborative playlists where the deleted user was an editor would lose that editor. Need to decide if this is acceptable or if account deletion should transfer ownership.
10. **Archive.org review API** — investigate whether archive.org's write API supports posting reviews, for future federation of user-submitted reviews back to the source.
