# Relisten Playlists & User Accounts — Design Spec

> **Superseded for implementation.** Use the [identity, user-data, and Sonos architecture](../architecture/2026-07-18-relisten-identity-user-data-and-sonos-architecture.md), the [API-to-TestFlight delivery plan](../plans/active/2026-07-18-relisten-mobile-first-account-delivery-plan.md), and the companion [mobile architecture](../../../relisten-mobile/docs/architecture/2026-07-18-relisten-mobile-accounts-library-sync-and-sonos-architecture.md) and [mobile implementation plan](../../../relisten-mobile/docs/plans/active/2026-07-18-relisten-mobile-accounts-library-sync-and-sonos-implementation.md). This document remains historical product exploration, not the account-system contract.

**Date:** 2026-04-11
**Status:** Draft v2 (revised with review feedback)
**Prerequisites:**
- Realm → TanStack DB migration (separate spec)
- Mobile playback queue contract migration (separate spec — see Section 15)

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
- Real-time collaborative editing via WebSocket (M1 deferred — collaborators sync on next fetch)
- Social features / friends / activity feeds
- Playlist discovery / search across all public playlists
- User-submitted reviews
- Full Realm → TanStack DB migration (separate spec)

### M1 Scope

The first milestone is narrower than the full spec:

- Create playlist, add track, add contiguous source range as a block
- Reorder entries and blocks
- Shuffle by block
- Share (unlisted link), follow, clone
- Play on web and mobile
- Sync favorites
- Upload play events (playback history)
- Collaborative editing (invite editors, apply operations) — but NOT real-time WebSocket sync

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

### Deployment Model

**Separate database on the same Postgres instance initially; separable to a distinct server later.** Concretely:

- User DB is a separate database (`relisten_users`) on the same Postgres cluster as the catalog DB (`app`).
- User service API is a separate .NET process / Kubernetes deployment with its own Docker image.
- Connection pools are separate: user service has its own pool for `relisten_users` (read-write) and a separate read-only pool for the catalog DB (read replica when available).
- Migration ownership: user DB migrations are owned by the user service, use SimpleMigrations (same pattern as the catalog API). The catalog API never touches the user DB and vice versa.
- Local dev: two databases in the same Postgres container (`relisten_db` for catalog, `relisten_users` for user data), configured in `local-dev/docker-compose.yml`.
- If user DB load grows, move `relisten_users` to its own Postgres instance — only connection strings change.

### Cross-Database References

User data references catalog data by UUID (no foreign key constraints). Trade-offs:

- **No cross-DB JOINs** — hydration requires two queries (fetch user data, batch-fetch catalog data by UUID). This matches the mobile app's existing pattern of assembling data from API responses.
- **Dangling references** — if a source/track is removed from the catalog (e.g., removed from archive.org), playlist entries become unresolvable. Display as "track no longer available" in the UI. See Section 4 for unavailable track handling.
- **No cross-DB transactions** — not needed. No operation requires atomicity across both databases.
- **Playback attribution** — play records in the user DB include a nullable `playlist_id` and `playlist_entry_uuid`. No FK enforcement against the catalog, but the data is in one database so it's queryable.

### User Service Tech Stack

- **.NET** (same as existing API) — one language, shared patterns, small team
- **Dapper** for data access (consistent with existing API)
- **Separate Postgres database** on the same instance (separable later)
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

All auth goes through a web-based flow hosted at `relisten.net/auth`. The mobile app opens a web view. The flow uses an authorization-code pattern — **refresh tokens never appear in URLs.**

```
Mobile app                    relisten.net/auth               User Service API
    │                              │                               │
    ├──opens webview with          │                               │
    │  state + code_challenge─────>│                               │
    │                              ├──user picks method───────────>│
    │                              │  (Apple/Google/magic link/    │
    │                              │   passkey)                    │
    │                              │                               │
    │                              │<──auth_code (short-lived)─────┤
    │<──deep link with auth_code───┤                               │
    │   + state (verified)         │                               │
    │                              │                               │
    ├──POST /auth/token            │                               │
    │  { auth_code,                ────────────────────────────────>│
    │    code_verifier }           │                               │
    │                              │                               │
    │<──{ access_token,            │                               │
    │     refresh_token }──────────────────────────────────────────┤
    │                              │                               │
    │  (store refresh_token in     │                               │
    │   Keychain/Keystore)         │                               │
```

**Security requirements:**

- **State parameter** — random value generated by the mobile app, passed through the web flow, verified on callback to prevent CSRF.
- **PKCE (code_challenge/code_verifier)** — the auth code is bound to the original requesting app. Prevents interception.
- **Auth code** — short-lived (60 seconds), single-use, exchanged for tokens via a direct API call (not through a URL).
- **Refresh token storage** — Keychain on iOS, Keystore on Android, httpOnly secure cookie on web. Never in URLs, localStorage, or AsyncStorage.
- **Refresh token rotation** — each use of a refresh token issues a new one and invalidates the old. Detects token reuse (possible theft).
- **Session/device tracking** — each refresh token is associated with a device ID. Users can view and revoke active sessions.

Note: the existing backend auth (`EnvUserStore` at `Startup.cs:337`, `EnvUserStore.cs:19`) is admin-only, cookie-based, env-backed plaintext auth. It is not reusable for public user accounts. The user service auth is built from scratch.

### Token Model

- **Access token** — JWT, 1 hour expiry. Contains user ID, username, session ID. Sent with every API request via `Authorization: Bearer` header.
- **Refresh token** — opaque, 1 year expiry, rotated on each use. Stored in device secure storage. Used to obtain new access tokens.
- **Auth code** — opaque, 60 second expiry, single-use. Exchanged for token pair via PKCE.

### Session Management

```sql
CREATE TABLE user_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    refresh_token_hash TEXT NOT NULL, -- bcrypt hash, never stored plaintext
    device_id TEXT NOT NULL,
    device_name TEXT, -- "iPhone 15", "Chrome on Mac"
    platform TEXT NOT NULL, -- 'ios', 'android', 'web'
    last_used_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at TIMESTAMPTZ -- null = active
);

CREATE INDEX idx_user_sessions_user_id ON user_sessions(user_id);
```

Users can list active sessions (`GET /api/v1/users/me/sessions`) and revoke any session.

### First-Time Signup

1. User authenticates via chosen provider
2. Prompted to choose a username (validated in real-time — see Section 7)
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
    username TEXT NOT NULL,
    username_lower TEXT NOT NULL UNIQUE, -- lowercase for case-insensitive uniqueness
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

- **Username** — required, unique, 3-30 characters, alphanumeric + underscores, case-insensitive uniqueness (enforced via `username_lower`). See Section 7 for validation rules.
- **Display name** — optional, max 50 characters. Falls back to username when not set.
- **No avatar uploads** — avoid hosting user-generated images. Use generated identicons or initials client-side.

---

## 4. Playlists & Blocks

### Core Concepts

- **Playlist** — an ordered collection of entries, owned by a user. Has a `current_revision` that increments on every operation for conflict detection.
- **Entry** — a reference to a specific track (by UUID) at a position in the playlist. Each entry has its own `uuid` (`playlist_entry_uuid`). The same track can appear multiple times as different entries. All operations, playback, analytics, and history reference `playlist_entry_uuid`, not `source_track_uuid`.
- **Block** — a group of entries that stay together during shuffle. Identified by a shared `block_uuid` on entries. Entries within a block must be positionally contiguous. `block_uuid` is a real stable UUID — not just an incidental grouping token. If block metadata (title, notes, type) is needed later, a `playlist_blocks` table can be added without changing entry identity.
- **Fractional indexing** — `position` is a text string supporting arbitrary insertion without rewriting other entries (e.g., "a", "aM", "b").
- **Block position** — `block_position` is an integer giving the entry's order within its block (0-indexed). This provides unambiguous within-block ordering independent of fractional position string comparison edge cases.

### Identity Distinction

Three distinct UUIDs serve different purposes:

- **`source_track_uuid`** — catalog identity. "Which recording is this?"
- **`playlist_entry_uuid`** — this occurrence of that track in this playlist. "Which copy in the playlist?"
- **`block_uuid`** — grouping identity. "Which block does this entry belong to?"

This distinction is critical because:

```
Entry A (uuid: aaa) -> source_track_uuid = X   block_uuid = B1, block_position = 0
Entry B (uuid: bbb) -> source_track_uuid = X   block_uuid = B1, block_position = 1
Entry C (uuid: ccc) -> source_track_uuid = Y   block_uuid = null
```

Track X appears twice. Playback cursor, deletion, move, history, and analytics all reference `playlist_entry_uuid` (aaa or bbb), never `source_track_uuid` alone.

### Shuffle Behavior

1. Group entries by `block_uuid` (null `block_uuid` = standalone track = its own shuffle unit)
2. Shuffle the groups
3. Within each group, play entries in `block_position` order

### Block Terminology

"Block" is the internal/data model term. The user-facing label is TBD — candidates include "segment," "group," "flow," or "run." The data model and API use "block" consistently; the UI can map to whatever resonates.

### Duplicate Tracks

A playlist can contain the same `source_track_uuid` multiple times as different entries. This is valid and expected — the same track might appear in two different blocks, or twice in different contexts. All operations, playback position, and analytics reference `playlist_entry_uuid`, not `source_track_uuid`.

### Unavailable Tracks

When a `source_track_uuid` no longer resolves in the catalog (source removed from archive.org, track deleted by importer):

- The entry remains in the playlist with its position and block membership intact
- Hydration returns `null` for the track metadata, with `available: false`
- The UI renders "Track no longer available" (greyed out, not hidden)
- Playback skips unavailable entries automatically, advancing to the next available entry
- If all entries in a block are unavailable, the block is skipped during shuffle
- Unavailable entries are still counted in playlist length but not in duration

### Partial Offline Blocks

When some entries in a block are downloaded for offline and others are not:

- In offline mode, only downloaded entries in the block play (the block is partially available)
- The UI indicates which entries in a block are available offline
- The block still shuffles as a unit — partial offline blocks are not split
- If no entries in a block are available offline, the block is skipped

### Current Entry / Playback Cursor

The playback cursor is a `playlist_entry_uuid`, not a `source_track_uuid` or positional index. This means:

- If the playlist is reordered while playing, the current track continues (cursor is stable)
- If the current entry is deleted by a collaborator, playback advances to the next entry
- Resume position is stored as `(playlist_id, playlist_entry_uuid, progress_seconds)` — not as a positional index

### Schema

```sql
CREATE TABLE playlists (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    short_id TEXT NOT NULL UNIQUE, -- URL-friendly, 8-10 chars (e.g., "Xk9mPq2v")
    owner_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    description TEXT,
    visibility TEXT NOT NULL DEFAULT 'private', -- 'private', 'unlisted', 'public'
    current_revision BIGINT NOT NULL DEFAULT 0,
    moderation_status TEXT NOT NULL DEFAULT 'approved', -- 'approved', 'pending_review', 'hidden'
    archived_at TIMESTAMPTZ, -- soft delete
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE playlist_entries (
    uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    source_track_uuid UUID NOT NULL, -- references catalog source_tracks.uuid (no FK)
    block_uuid UUID, -- nullable; shared UUID groups entries into a block
    position TEXT NOT NULL, -- fractional index for playlist-level ordering
    block_position INT, -- nullable; 0-indexed order within a block
    added_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_playlist_entries_playlist_uuid ON playlist_entries(playlist_uuid);
CREATE INDEX idx_playlist_entries_block_uuid ON playlist_entries(block_uuid);

CREATE TABLE playlist_collaborators (
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role TEXT NOT NULL DEFAULT 'editor',
    invited_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    accepted_at TIMESTAMPTZ, -- null = pending invitation
    PRIMARY KEY (playlist_uuid, user_id)
);

CREATE TABLE playlist_followers (
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    followed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (playlist_uuid, user_id)
);
```

### Sharing & Visibility

Playlists have a three-level visibility model:

- **`private`** — only the owner and invited collaborators can see it. Not accessible via link.
- **`unlisted`** — anyone with a share token can view. Not discoverable. This is the default sharing mode.
- **`public`** — anyone can view via the `short_id` URL. Discoverable in future search/browse features.

### Share Tokens

Share tokens provide revocable, role-scoped access to playlists:

```sql
CREATE TABLE playlist_share_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE, -- hash of the share token
    role TEXT NOT NULL DEFAULT 'viewer', -- 'viewer' or 'editor'
    created_by UUID NOT NULL REFERENCES users(id),
    expires_at TIMESTAMPTZ, -- nullable; null = no expiry
    revoked_at TIMESTAMPTZ, -- nullable; null = active
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

- Share URL format: `https://relisten.net/playlist/{shortId}?t={shareToken}`
- The owner can create multiple share tokens with different roles (viewer, editor)
- Tokens can be revoked without changing the playlist's `short_id`
- `public` playlists don't require a token — `short_id` alone is sufficient
- `unlisted` playlists require a valid, non-expired, non-revoked token
- `private` playlists are not accessible via link at all (only direct collaborator invites)

### Access Resolution

When a request arrives for a playlist:

1. If requester is owner or accepted collaborator → full access per their role
2. If playlist is `public` → viewer access (no token needed)
3. If playlist is `unlisted` and valid share token provided → access per token role
4. If playlist is `private` and requester is not a collaborator → 404
5. Transitioning `public` → `private`: existing followers retain access; new requests without collaborator status get 404
6. Transitioning `public` → `unlisted`: existing followers retain access; new requests need a share token

### Follower vs Public Access

Following a playlist creates a `playlist_followers` row. Followers have viewer access regardless of visibility changes — if an owner makes a public playlist unlisted, existing followers still see it. This matches the mental model of "I chose to follow this."

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
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id),
    operation JSONB NOT NULL, -- full operation payload
    idempotency_key UUID NOT NULL UNIQUE,
    base_revision BIGINT, -- the revision the client believed was current (diagnostic/conflict hint)
    result_revision BIGINT NOT NULL, -- the playlist revision after this operation was applied
    result_status TEXT NOT NULL, -- 'applied', 'noop_entry_missing', 'conflict_block_changed', etc.
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_playlist_edit_log_playlist_uuid ON playlist_edit_log(playlist_uuid);
```

### Revision Model

Every playlist has a `current_revision` (bigint, starts at 0, increments on every applied operation). Operations include:

- **`base_revision`** (nullable) — the revision the client believed was current when composing the operation. Used for diagnostics and optional conflict hints. The server does not reject based on stale revision alone.
- **`result_revision`** — the playlist revision after the operation was applied.
- **`idempotency_key`** — UUID, unique. Replaying the same operation returns the same result without re-applying.

**The rule:** server serializes operations per playlist. Server order wins. Client sends `base_revision` for diagnostics. Server always returns the resulting canonical playlist state and revision. Client reconciles by applying or replacing with server state.

### Deterministic Per-Operation Results

Every operation returns a result status. For batch operations, each operation gets its own result — batches are NOT all-or-nothing unless explicitly transactional.

Result statuses:
- **`applied`** — operation succeeded, state changed
- **`noop_already_applied`** — idempotent replay, no state change
- **`noop_entry_missing`** — referenced entry was already deleted
- **`noop_block_empty`** — referenced block has no entries
- **`conflict_entry_deleted`** — tried to move/modify an entry that was deleted by another editor
- **`conflict_block_changed`** — tried to move/split a block that was modified by another editor. Server applies the operation to the block's current state if possible, or returns this status if the operation is no longer meaningful.
- **`rejected_contiguity`** — operation would break block contiguity
- **`rejected_limit`** — would exceed playlist entry/collaborator limits

### Conflict Scenarios

Specific scenarios and their deterministic outcomes:

- **Move entry after someone deleted it:** return `noop_entry_missing`. No state change.
- **Move block after someone split it:** move the current entries with that `block_uuid` to the new position. If the block_uuid no longer exists, return `noop_block_empty`.
- **Add track at position between entries that moved:** accept, assign a new canonical position, return revised surrounding order.
- **Delete entry already deleted:** idempotent success if same `idempotency_key`, otherwise `noop_entry_missing`.
- **Offline batch with 50 ops and op 17 invalid:** ops 1-16 applied, op 17 returns its specific error status, ops 18-50 continue executing. Per-op results, not all-or-nothing.

### Operation Catalog

Every operation is:
- Attributed to a user (`user_id`)
- Idempotent (via `idempotency_key` — replays are safe)
- Self-contained (no implicit state dependencies)
- Logged to `playlist_edit_log` with the full payload and result
- Applied to materialized state in `playlist_entries`
- Returns `result_revision` and `result_status`

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
    "visibility": "unlisted"
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
    "sourceTrackUuid": "catalog-track-uuid",
    "position": "aM",
    "blockUuid": null,
    "blockPosition": null
}
```

**`remove_entry`** — remove an entry from the playlist
```json
{
    "op": "remove_entry",
    "playlistId": "uuid",
    "entryUuid": "playlist-entry-uuid"
}
```

**`move_entry`** — reposition a track, optionally changing its block membership
```json
{
    "op": "move_entry",
    "playlistId": "uuid",
    "entryUuid": "playlist-entry-uuid",
    "newPosition": "cG",
    "blockUuid": "block-uuid-or-null",
    "blockPosition": 2
}
```
- `blockUuid: null` removes the entry from any block (standalone)
- `blockUuid: "some-uuid"` moves the entry into that block at the specified `blockPosition`
- Server validates that the new position maintains block contiguity

#### Block Operations

**`create_block`** — group consecutive entries into a block
```json
{
    "op": "create_block",
    "playlistId": "uuid",
    "entryUuids": ["entry-1", "entry-2", "entry-3"]
}
```
Server assigns a new `block_uuid` and sets `block_position` (0, 1, 2...) based on current entry order. Reassigns entries to contiguous fractional positions if needed.

**`dissolve_block`** — ungroup entries (entries keep positions, `block_uuid` and `block_position` set to null)
```json
{
    "op": "dissolve_block",
    "playlistId": "uuid",
    "blockUuid": "block-uuid"
}
```

**`move_block`** — reorder an entire block atomically
```json
{
    "op": "move_block",
    "playlistId": "uuid",
    "blockUuid": "block-uuid",
    "newPosition": "m"
}
```
All entries in the block get new contiguous fractional positions at the target location. `block_position` values are preserved (internal order unchanged).

**`split_block`** — split a block at a given entry
```json
{
    "op": "split_block",
    "playlistId": "uuid",
    "blockUuid": "block-uuid",
    "splitAfterEntryUuid": "entry-uuid"
}
```
Entries after the split point get a new `block_uuid`. `block_position` values are renumbered in both resulting blocks.

**`merge_blocks`** — merge two adjacent blocks
```json
{
    "op": "merge_blocks",
    "playlistId": "uuid",
    "blockUuid1": "block-uuid-a",
    "blockUuid2": "block-uuid-b"
}
```
All entries get `blockUuid1`. `block_position` values are renumbered sequentially. Server validates blocks are adjacent.

#### Compound Operations

**`add_tracks_as_block`** — add multiple tracks as a block in one atomic operation (the "add this segue" flow)
```json
{
    "op": "add_tracks_as_block",
    "playlistId": "uuid",
    "sourceTrackUuids": ["track-1", "track-2", "track-3"],
    "position": "g"
}
```
Server creates new entries with a shared `block_uuid` at contiguous positions starting from the specified position. `block_position` is assigned 0, 1, 2... in the order given. Always creates new entries — if a track already exists in the playlist, this adds another instance (tracks can appear multiple times).

**`add_source_range_as_block`** — add a contiguous range of tracks from a source set (the "add this show segment" flow)
```json
{
    "op": "add_source_range_as_block",
    "playlistId": "uuid",
    "sourceUuid": "catalog-source-uuid",
    "startTrackPosition": 3,
    "endTrackPosition": 6,
    "position": "g"
}
```
Server resolves the source's tracks from the catalog (via read replica), creates entries for tracks at positions 3-6, assigns a shared `block_uuid`. This is the primary flow for "add this segue from this show."

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

### Block Invariants

The server enforces these invariants on every operation:

1. **Contiguity** — entries with the same `block_uuid` must occupy a contiguous range of fractional positions. No non-block entry or entry from a different block may have a position between a block's first and last entry.
2. **Block position consistency** — `block_position` values within a block must be sequential integers starting from 0, matching the order implied by `position`.
3. **No orphaned blocks** — if all entries with a `block_uuid` are removed, the block ceases to exist.
4. **No cross-playlist blocks** — all entries with the same `block_uuid` must be in the same playlist.
5. **Standalone entries** — entries with `block_uuid = null` must have `block_position = null`.

### Contiguity Enforcement

- `create_block` reassigns entries to adjacent fractional positions if needed
- `add_track` with a `blockUuid` assigns a position within the block's current range
- `move_entry` into a block validates the position falls within the block's range
- `move_entry` of a non-block entry between two entries in the same block is rejected (`rejected_contiguity`)
- `move_block` reassigns all member positions atomically at the target location
- `split_block` and `merge_blocks` update positions to maintain contiguity

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

**Prerequisite:** the Realm → TanStack DB migration for catalog data is a separate project that should happen first. This spec assumes TanStack DB is the client data layer.

### Collections

```
User data collections (bidirectional sync with user service):
  playlists          — playlist metadata
  playlistEntries    — entries with positions, block UUIDs, block positions
  favorites          — entity type + entity UUID
  userSettings       — JSON settings blob

Local-only collections (batch upload, never download full history):
  playbackHistory    — local journal, batch-synced up

Catalog collections (read-only, fetch-on-navigate, etag-cached):
  artists, shows, sources, sourceTracks, sourceSets,
  venues, tours, years, eras, setlistSongs, reviews,
  popularity, etc.
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
6. Server applies the operation, returns authoritative state including `current_revision`
7. TanStack DB reconciles optimistic state with server state

On app launch, the outbox loads pending operations and resumes sync.

### Offline Playback History (Batch Upload)

Playback history does not use the outbox — it's append-only, high-volume, and doesn't need optimistic UI or conflict resolution.

1. Track starts playing → write to local SQLite immediately
2. Entry includes `synced: false` flag
3. Background job batches unsynced entries when online
4. `POST /api/v1/history/batch` accepts up to 500 entries per request
5. Server deduplicates on `(user_id, source_track_uuid, played_at)` within a tolerance window
6. On success, entries marked `synced: true` locally

This handles hundreds of offline listens on a trip — SQLite handles thousands of rows without issue.

### Collaborative Sync (M1)

In M1, collaborative playlists do NOT use real-time WebSocket sync. Instead:

- When opening a playlist, the client fetches the current state and `current_revision`
- When submitting operations, the client sends `base_revision` and receives `result_revision`
- If `result_revision > base_revision + 1`, another editor made changes — client refetches the full playlist state
- This is poll-on-interact, not real-time push

Real-time WebSocket sync is a post-M1 enhancement.

### Conflict Resolution

- **Favorites** — last-write-wins. Simple toggle, no meaningful conflict.
- **Settings** — last-write-wins.
- **History** — append-only, never conflicts.
- **Playlist operations** — server-received order wins. Operations are applied sequentially with deterministic per-op results (see Section 5). Client always converges by fetching canonical state after operation response.

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
| Public playlist creation | 20/day per account | Higher bar for publicly visible content |

Account creation rate limiting is handled by Cloudflare bot protection, not application-level limits.

### Username Validation

- 3-30 characters
- Alphanumeric + underscores only
- Must start with a letter
- Case-insensitive uniqueness (enforced via `username_lower` column)
- **Artist name blocking** — reject usernames matching any artist name or slug in the catalog DB (normalized comparison). Also block `{artist}_official`, `{artist}official`, `official_{artist}` patterns.
- **System reserved words** — admin, root, support, relisten, system, moderator, staff, help, official, playlist, api, auth, settings, etc.
- **Impersonation patterns** — reject anything ending in `_official`, `_support`, `_admin`, `_staff`
- **Profanity filter** — blocklist of slurs and offensive terms
- **OpenAI moderation API** — free, async check as a second layer

### Content Moderation Pipeline

Applied to playlist names and descriptions on create/update. **Fail-open** — content is published immediately, moderation runs async:

1. **Synchronous (blocking):**
   - Character/format validation (length limits, basic regex)
   - Hard length limits enforced at the database level
   - URL detection — strip or reject URLs in descriptions to prevent spam links
   - Local profanity/spam blocklist

2. **Asynchronous (non-blocking):**
   - OpenAI moderation API (free)
   - If flagged: set `moderation_status = 'pending_review'` on the playlist
   - If clean: `moderation_status` stays `'approved'`

3. **Post-moderation:**
   - Playlists with `moderation_status = 'pending_review'` remain visible to the owner but are hidden from followers and public access
   - If content is confirmed inappropriate: `moderation_status = 'hidden'`
   - Owner can edit the name/description to trigger re-moderation

### Moderation Fields

```sql
-- On playlists table (already included in Section 4 schema):
moderation_status TEXT NOT NULL DEFAULT 'approved'
-- Values: 'approved', 'pending_review', 'hidden'
```

### Abuse Handling

No manual moderation system in v1. The limits above are preventive. Abuse surface is limited because:
- No in-app playlist discovery or search in M1 (playlists spread via direct link sharing)
- No free-text content beyond playlist name and description
- No user-uploaded media

If needed in the future: soft-ban (user's playlists stop appearing for followers, collaborative edits paused).

---

## 8. Sharing, Following & Cloning

### Sharing

Playlists are **private by default**. Sharing options depend on visibility level (see Section 4 for the full visibility and share token model):

- **Private** — only direct collaborator invites. No link sharing.
- **Unlisted** — owner generates a share token. URL: `https://relisten.net/playlist/{shortId}?t={token}`. Token is revocable.
- **Public** — accessible via `https://relisten.net/playlist/{shortId}`. No token needed.

On mobile, playlist URLs deep-link into the app via existing app links.

### Access Levels

| Role | View | Edit | Share | Delete |
|---|---|---|---|---|
| Owner | Yes | Yes | Yes (create/revoke tokens) | Yes (soft delete) |
| Editor (collaborator) | Yes | Yes | Yes (share existing link) | No |
| Follower | Yes | No | Yes (re-share URL) | Can unfollow |
| Token viewer | Yes | No | Can copy URL | No |
| Anonymous (public playlist) | Yes | No | Can copy URL | No |

### Web Experience

When someone opens a playlist link on relisten.net:

- **Full playlist view** — name, description, track list with show/artist info, creator username, editor attributions ("added by @jake")
- **Web playback** — play the playlist directly in the browser
- **Rich embeds** — Open Graph / Twitter Card meta tags: title, description, track count, total duration, creator. Generated preview image.
- **Embeddable player** — `relisten.net/embed/playlist/{shortId}` with compact form, similar to existing show embeds. Can be dropped into forums, Reddit posts, etc.
- **Deep link** — "Open in App" button. On mobile browsers, auto-opens the app.
- **Auth actions** — signed-in users see Follow / Clone buttons. Not signed in: read-only view with sign-in prompt.

**Cache/privacy behavior for public playlist pages:**
- Public/unlisted playlist responses are cacheable by playlist revision (`ETag: revision-{current_revision}`)
- Viewer-specific state (is_following, is_collaborator) is fetched separately, never cached in the public response
- Crawler/embed/OG-image requests should be rate-limited and served from cache to protect read-replica load

### Following

"Follow" adds the playlist to the user's library as a live reference:

- Shows up under a "Following" section in the user's library
- Updates automatically when the owner or collaborators edit it (on next fetch in M1, real-time in future)
- Unfollow at any time
- If the owner archives (soft deletes) the playlist, it disappears from followers' libraries with a "no longer available" indication
- Followers retain access through visibility transitions (see Section 4)

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

---

## 9. Playback History & Analytics

### Cost Awareness

Playback history is the likely long-term cost center. The existing `source_track_plays_old` hypertable is ~16GB and growing. Per-user history must be designed for efficient storage from the start.

**Do not retrofit per-user history into the existing popularity table.** Keep them separate:
- **User playback history** (user DB) — per-user, rich, partitioned, retained
- **Anonymous aggregate plays** (catalog DB) — for popularity/trending, stays in `source_track_plays`

Optionally dual-write: when user history is uploaded, also write an anonymized aggregate event to `source_track_plays` in the catalog DB so popularity/trending doesn't regress.

### Schema

User playback history is stored as a TimescaleDB hypertable from the start:

```sql
CREATE TABLE playback_history (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL,
    source_track_uuid UUID NOT NULL, -- references catalog source_tracks.uuid
    source_uuid UUID NOT NULL, -- references catalog sources.uuid
    playlist_uuid UUID, -- nullable; which playlist was playing
    playlist_entry_uuid UUID, -- nullable; specific entry (handles duplicate tracks)
    played_at TIMESTAMPTZ NOT NULL,
    platform TEXT NOT NULL, -- 'ios', 'android', 'web', 'carplay', 'sonos'
    app_version TEXT NOT NULL, -- e.g., '4.2.1'
    device_id TEXT NOT NULL -- stable per-device identifier
);

-- Convert to TimescaleDB hypertable, partitioned by played_at
SELECT create_hypertable('playback_history', 'played_at');

-- Indexes for common queries
CREATE INDEX idx_playback_history_user_id_played_at ON playback_history(user_id, played_at DESC);
CREATE INDEX idx_playback_history_playlist_uuid ON playback_history(playlist_uuid);
```

Note: `id` is not a PRIMARY KEY because TimescaleDB hypertables require the partitioning column (`played_at`) in the primary key. Use `(id, played_at)` as composite PK if uniqueness constraints are needed.

### Retention Policy

```sql
-- Keep detailed per-user history for 2 years
SELECT add_retention_policy('playback_history', INTERVAL '2 years');

-- Continuous aggregate for long-term stats (computed, not stored per-play)
CREATE MATERIALIZED VIEW playback_stats_monthly
WITH (timescaledb.continuous) AS
SELECT
    user_id,
    time_bucket('1 month', played_at) AS month,
    count(*) AS play_count,
    count(DISTINCT source_track_uuid) AS unique_tracks,
    count(DISTINCT source_uuid) AS unique_sources,
    count(DISTINCT playlist_uuid) FILTER (WHERE playlist_uuid IS NOT NULL) AS playlists_used
FROM playback_history
GROUP BY user_id, time_bucket('1 month', played_at);
```

### Two Consumption Patterns

**Local device history (fast, offline):**
- "What was I listening to recently?" — query local SQLite, sorted by `played_at`
- Immediate, no network, works offline
- Day-to-day UX

**Aggregated cross-device stats (server-side, future):**
- "Your year in Relisten" / wrapped-style features
- Total listening time, top artists, top shows, listening streaks
- Computed server-side from `playback_history` and `playback_stats_monthly`
- API endpoint: `GET /api/v1/history/stats?period=2025`

### Play Attribution

When a track plays from a playlist, we record both `playlist_uuid` and `playlist_entry_uuid`. Because entries have their own UUIDs, this correctly attributes plays even when the same track appears multiple times in a playlist. This enables:

- "Most played blocks" — join entry → block_uuid, aggregate by block_uuid across playlists
- Radio segment popularity — analyze which source_track_uuid groupings appear together across playlists and weight by play count (see Section 13)
- Per-playlist analytics — "this playlist has been played 342 times"

### Dual-Write to Catalog Popularity

When processing user history batch uploads, the user service optionally writes anonymized aggregate events to the catalog DB's `source_track_plays` table (existing popularity infrastructure). This ensures:
- Trending/popularity scores don't regress when plays come through the user service
- No per-user data leaks into the catalog DB
- The write is fire-and-forget — failure doesn't block history upload

Integration point: existing play insertion at `LiveController.cs:41`.

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

## 12. Hydrated Playlist DTO & Query Contract

When the user service serves a playlist (for web display, embed, or mobile with `?hydrate=true`), it must hydrate entry UUIDs against the catalog read replica.

### Hydration Query Contract

Hydration is performed as a bounded set of queries, not per-entry fanout:

1. Fetch playlist metadata + all entries from user DB (1 query)
2. Collect unique `source_track_uuid` values from entries
3. Batch-fetch track metadata from catalog read replica: `SELECT * FROM source_tracks WHERE uuid IN (...)` (1 query)
4. Batch-fetch source metadata: `SELECT * FROM sources WHERE id IN (...)` (1 query)
5. Batch-fetch show metadata: `SELECT * FROM shows WHERE id IN (...)` (1 query)
6. Batch-fetch artist metadata: `SELECT * FROM artists WHERE id IN (...)` (1 query)

Total: 5 bounded queries maximum. No recursive fanout.

### Hydrated Playlist Response

```json
{
    "playlist": {
        "uuid": "...",
        "shortId": "Xk9mPq2v",
        "name": "Best Phish Jams 2024",
        "description": "Top segues from summer tour",
        "visibility": "unlisted",
        "currentRevision": 47,
        "owner": { "username": "alec", "displayName": "Alec" },
        "collaborators": [
            { "username": "jake", "role": "editor" }
        ],
        "entryCount": 42,
        "totalDuration": 18340,
        "createdAt": "...",
        "updatedAt": "..."
    },
    "entries": [
        {
            "uuid": "entry-uuid-1",
            "position": "a",
            "blockUuid": "block-uuid-1",
            "blockPosition": 0,
            "addedBy": { "username": "alec" },
            "available": true,
            "track": {
                "uuid": "source-track-uuid",
                "title": "Scarlet Begonias",
                "duration": 612,
                "mp3Url": "https://archive.org/...",
                "show": {
                    "uuid": "show-uuid",
                    "date": "1977-05-08",
                    "displayDate": "1977-05-08",
                    "venue": { "name": "Barton Hall", "city": "Ithaca", "state": "NY" }
                },
                "artist": { "uuid": "artist-uuid", "name": "Grateful Dead" },
                "source": { "uuid": "source-uuid", "isSoundboard": true }
            }
        },
        {
            "uuid": "entry-uuid-2",
            "position": "aM",
            "blockUuid": "block-uuid-1",
            "blockPosition": 1,
            "addedBy": { "username": "alec" },
            "available": false,
            "track": null
        }
    ]
}
```

- `available: false` with `track: null` indicates an unavailable/orphaned track
- `totalDuration` only counts available entries
- Viewer-specific state (is_following, is_collaborator, can_edit) is returned in a separate `viewer` field, only when authenticated. Never cached in the public response.

### Caching

- Public/unlisted playlists: cache by `ETag: playlist-{shortId}-rev-{currentRevision}`
- Invalidate on any operation that changes `current_revision`
- Viewer-specific state fetched separately via `GET /api/v1/playlists/{shortId}/viewer-state` (authenticated, never cached publicly)
- Rate-limit crawler/OG-image/embed requests to protect read-replica load

### Non-Hydrated Response

With `?hydrate=false` (default for mobile), the response includes entries with raw UUIDs only — no track/show/artist metadata. The mobile client joins with catalog data via TanStack DB live queries.

---

## 13. Future Features (Designed For, Not Built)

These features are explicitly out of v1 scope. The architecture and data model are designed to support them without rearchitecting.

### Relisten Radio

Client-driven, not broadcast. Rather than tracking popularity per specific block instance, analyze playlist data to find commonly grouped `source_track_uuid` combinations across playlists. Weight by aggregate play count from `playback_history`.

Approach:
- Batch job analyzes `playlist_entries` across all playlists: which `source_track_uuid` groupings (within the same `block_uuid`) appear frequently?
- Two-track blocks and four-track blocks of the same underlying segue are all valid — they don't need to match exactly. Overlapping groupings are all candidates.
- Weight candidates by total play count from `playback_history` entries attributed to those playlists
- Per-artist radio (e.g., "Phish Radio") filters by artist
- Client fetches a sequence of popular blocks, controls playback locally

Future possibility: shared sequence across listeners (everyone gets the same order, different positions).

**What v1 provides:** playlist-attributed play tracking with `playlist_entry_uuid`, block-level data model, batch aggregation infrastructure via Hangfire, `playback_history` as a TimescaleDB hypertable.

### AT Protocol / Bluesky

Users could sign in with their DID. The `playlist_edit_log` maps to AT Protocol records — each operation is a record owned by the user in their PDS. The server becomes an aggregator: it reads operation records from users' PDSes and materializes playlist state.

**What v1 provides:** operation log as first-class data with stable schemas, idempotency keys, per-op result statuses, and user attribution. `users` table can accept a `did` column. Auth methods table supports adding new providers.

### Push Notifications

Collaborative playlist edits, invitation responses, "a playlist you follow was updated."

**What v1 provides:** the event model (operations, invitations, follows) that notifications would trigger from.

### Real-Time Collaborative Editing

WebSocket-based real-time sync: when viewing a collaborative playlist, the client opens a WebSocket. When any collaborator's operation is applied, the server pushes updated entries to all connected clients.

**What v1 provides:** revision-based conflict resolution, deterministic per-op results. WebSocket is additive — the operation model doesn't change.

### Social / Friends

See what friends are listening to, follow users, friend activity feed.

**What v1 provides:** user profiles with usernames, playback history with attribution, public playlist infrastructure.

### Playlist Discovery / Search

Search public playlists by name, browse curated/popular playlists.

**What v1 provides:** `visibility` field, playlist metadata, follower counts for future ranking.

### User-Submitted Reviews

Users write reviews of sources/shows, displayed alongside imported reviews. Potentially federated back to archive.org via their write API (investigate whether archive.org's API supports review submission).

**What v1 provides:** user identity and auth infrastructure. Reviews would be a new collection in the user DB.

---

## 14. Deployment & Infrastructure

### New Components

- **User Service API** — .NET application, deployed as a Kubernetes deployment alongside the existing API. Separate Docker image.
- **User Postgres database** — separate database on the same Postgres instance (separable to its own server later).
- **TimescaleDB extension** — required on the user Postgres database for `playback_history` hypertable.

### Environment Variables (User Service)

```
USER_DATABASE_URL=postgresql://user:pass@host:port/relisten_users
CATALOG_DATABASE_URL=postgresql://user:pass@host:port/app  (read-only connection to catalog)
REDIS_URL=redis://host:port
JWT_SECRET=...
JWT_ISSUER=relisten.net
APPLE_CLIENT_ID=...
APPLE_TEAM_ID=...
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
AWS_SES_REGION=...  (for magic link emails)
AWS_SES_FROM_ADDRESS=auth@relisten.net
OPENAI_API_KEY=...  (for moderation API, free tier)
```

### Connection Pools

- **User DB (read-write)** — primary pool for all user service operations
- **User DB (read-only)** — for read-heavy endpoints (playlist listing, history queries)
- **Catalog DB (read-only)** — separate pool, read replica when available, for hydration queries only

### Cache/Header Policies

- Public playlist endpoints (`GET /api/v1/playlists/{shortId}` without auth): `Cache-Control: public, max-age=300` with ETag by revision
- Authenticated `/me` endpoints: `Cache-Control: private, no-store`
- Distinct policies prevent authenticated state from leaking into CDN/proxy caches

### Backup Strategy

- **User Postgres** — daily automated backups, point-in-time recovery, WAL archiving. This is the precious data.
- **Catalog Postgres** — existing backup strategy unchanged. Data is rebuildable from upstream sources.
- **Redis** — ephemeral (rate limit counters, session cache). No backup needed.

### User Data Export / Delete

- Users can request a full export of their data (GDPR-style): playlists, favorites, history, settings
- Account deletion hard-deletes all user data (cascading FK deletes). Collaborative playlists where the user was an editor lose that editor. Playlists the user owned are permanently deleted.
- Export format: JSON archive

### Local Development

Add to `local-dev/docker-compose.yml`:
- Second database `relisten_users` in the existing Postgres container
- User service runs as a separate `dotnet run` process or Docker container
- Seed script for test user accounts

---

## 15. Mobile Playback Queue Migration

**This is a separate prerequisite design item.**

The current mobile playback queue is flat and SourceTrack-based:
- `PlayerQueueTrack` wraps one `SourceTrack`
- Shuffle operates over `PlayerQueueTrack[]`
- Persisted player state stores `source_track_uuid` arrays (`relisten_player_queue.tsx:57`, `player_state.ts:8`)

Playlist-aware playback with block shuffle requires queue items that carry richer context:

```ts
interface PlaylistQueueItem {
    playlistUuid: string
    playlistEntryUuid: string  // for attribution, cursor stability
    blockUuid: string | null
    blockPosition: number | null
    sourceTrackUuid: string
    // hydrated track data for playback
    track: SourceTrack
}
```

The queue must support:
- Block-aware shuffle: group by `blockUuid`, shuffle groups, preserve internal `blockPosition` order
- Playback cursor by `playlistEntryUuid` (stable across reorders)
- Playback attribution: when recording history, include `playlistUuid` and `playlistEntryUuid`
- Graceful handling of unavailable entries (skip, advance to next)
- Both playlist and non-playlist playback (regular show/source playback continues to work)

This migration should be designed alongside the Realm → TanStack DB migration, as both affect the player state model.

---

## Appendix A: Glossary

| Term | Definition |
|---|---|
| **Block** | A group of playlist entries that stay together during shuffle. Identified by a shared `block_uuid`. Internal term; user-facing name TBD. |
| **Entry** | A single item in a playlist, referencing a catalog track by `source_track_uuid`. Has its own `uuid` (`playlist_entry_uuid`). The same track can appear as multiple entries. |
| **Fractional index** | A text-based position value (`position` column) that supports arbitrary insertion between existing values without rewriting other positions. |
| **Block position** | An integer (`block_position` column) giving the entry's order within its block (0-indexed). |
| **Operation** | An atomic, self-contained mutation applied to a playlist. Logged, attributed, idempotent, with deterministic result status. |
| **Revision** | A playlist's `current_revision` counter. Increments on every applied operation. Used for conflict detection, caching, and sync. |
| **Catalog** | The existing read-only indexed data (artists, shows, sources, tracks) in the existing Postgres database. |
| **User service** | The new API and database for user accounts, playlists, favorites, history, and settings. |
| **Outbox** | TanStack DB's offline-transactions queue that persists operations locally and replays them when connectivity is available. |
| **Short ID** | URL-friendly identifier for playlists (e.g., `Xk9mPq2v`), used in public/unlisted shareable links. |
| **Share token** | A revocable, role-scoped token for accessing unlisted playlists. |
| **Hydration** | The process of resolving catalog UUIDs in playlist entries to full track/show/artist metadata. |

## Appendix B: Open Questions

1. **User-facing term for "block"** — segment, group, flow, run? Needs user research / community input.
2. **Fractional indexing library** — which implementation to use for position strings? Several open-source options exist.
3. **Magic link email sender** — AWS SES vs. Resend vs. Postmark? All are cheap at expected volume.
4. **Passkey implementation library** — which .NET WebAuthn library to use? (e.g., Fido2Net, passwordless.dev)
5. **TanStack DB stability timeline** — currently alpha (v0.6). Team relationship mitigates risk, but should track v1 timeline.
6. **Embeddable player design** — compact form factor for playlist embeds. Design work needed.
7. **Generated preview images** — what should the Open Graph image for a shared playlist look like? Needs design.
8. **Account deletion and owned playlists** — should account deletion offer ownership transfer for collaborative playlists? Or just hard-delete?
9. **Archive.org review API** — investigate whether archive.org's write API supports posting reviews, for future federation.
10. **Playback history retention tuning** — 2-year retention is a starting point. Monitor hypertable size and adjust.

## Appendix C: API Surface

### User Service API Endpoints

All endpoints under the user service base URL. Auth required unless noted.

**Auth:**
- `POST /api/v1/auth/authorize` — initiate auth flow, returns auth URL with state + PKCE challenge
- `POST /api/v1/auth/callback/{provider}` — OAuth callback (Apple, Google)
- `POST /api/v1/auth/magic-link` — send magic link email
- `POST /api/v1/auth/magic-link/verify` — verify magic link token, returns auth code
- `POST /api/v1/auth/passkey/register` — register a passkey
- `POST /api/v1/auth/passkey/authenticate` — authenticate with passkey, returns auth code
- `POST /api/v1/auth/token` — exchange auth code + PKCE verifier for token pair
- `POST /api/v1/auth/refresh` — exchange refresh token for new token pair (rotates refresh token)
- `POST /api/v1/auth/logout` — revoke refresh token / session

**Users:**
- `GET /api/v1/users/me` — current user profile
- `PATCH /api/v1/users/me` — update display name
- `GET /api/v1/users/me/sessions` — list active sessions
- `DELETE /api/v1/users/me/sessions/{sessionId}` — revoke a session
- `GET /api/v1/users/check-username/{username}` — availability check (no auth required)
- `POST /api/v1/users/me/auth-methods` — link additional auth method
- `DELETE /api/v1/users/me/auth-methods/{id}` — unlink auth method
- `POST /api/v1/users/me/export` — request data export
- `DELETE /api/v1/users/me` — delete account

**Playlists:**
- `GET /api/v1/playlists` — list user's playlists (owned, collaborating, following)
- `POST /api/v1/playlists` — create playlist
- `GET /api/v1/playlists/{shortId}` — get playlist with entries. Supports `?hydrate=true` for web (returns track/show/artist metadata from catalog read replica). Default `?hydrate=false` returns raw UUIDs for mobile.
- `GET /api/v1/playlists/{shortId}/viewer-state` — authenticated viewer's relationship to playlist (is_following, is_collaborator, can_edit). Never cached publicly.
- `POST /api/v1/playlists/{shortId}/operations` — apply a single operation. Returns `{ resultRevision, resultStatus, playlist }`.
- `POST /api/v1/playlists/{shortId}/operations/batch` — apply batch operations (offline sync). Returns per-operation results.
- `GET /api/v1/playlists/{shortId}/log` — get edit log (owner/editors only)
- `DELETE /api/v1/playlists/{shortId}` — archive (soft delete, owner only)
- `POST /api/v1/playlists/{shortId}/share-tokens` — create share token (owner only)
- `DELETE /api/v1/playlists/{shortId}/share-tokens/{tokenId}` — revoke share token
- `POST /api/v1/playlists/{shortId}/follow` — follow a playlist
- `DELETE /api/v1/playlists/{shortId}/follow` — unfollow
- `POST /api/v1/playlists/{shortId}/clone` — clone to own library

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
- `POST /api/v1/invitations/{playlistUuid}/accept` — accept
- `POST /api/v1/invitations/{playlistUuid}/decline` — decline
