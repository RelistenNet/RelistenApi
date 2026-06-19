# Relisten Playlists & User Accounts — Design Spec

**Date:** 2026-04-11
**Status:** Draft v4 (revised with explicit mobile M1 contract)
**Prerequisites:**
- Mobile M1 contract: Queue V2, scoped Realm user data, auth/session scope, mobile share-token exchange, source favorites, and history upload migration (see Section 15)

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
- Realm → TanStack DB migration. M1 builds on the existing Realm layer instead of replacing it.

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
- Mobile contract work required before playlist playback ships: Queue V2, scoped user-owned Realm rows, auth/session services, mobile share-token exchange, source favorites, and playback history migration

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
           └────────────┬─────────────────┘
                        │
              ┌─────────┴──────────┐
              │ Existing Postgres  │
              │                    │
              │ public/catalog     │
              │ user_data schema   │
              └─────────┬──────────┘
                        │
              ┌─────────┴──────────┐
              │   Mobile / Web     │
              │   Realm-first M1   │
              │   Web API cache    │
              └────────────────────┘
```

### Why a Separate Schema First

The existing catalog data is a read-heavy cache rebuilt by importers. User data is write-heavy and mutable. M1 keeps user data in the same physical Postgres database but isolates it in a dedicated schema such as `user_data`.

- **No new database to operate** — local development, backups, and production deployment stay close to the current Relisten setup.
- **Clear ownership boundary** — user tables, migration history, indexes, and grants live under `user_data`. Catalog tables remain in the existing catalog schema.
- **Backup priority is explicit** — user data is irreplaceable, so the existing database backup policy must meet user-data recovery needs. Catalog data can be rebuilt from upstream sources (archive.org, phish.in, etc.).
- **Future flexibility** — the schema boundary keeps a later move to a separate database or server possible. That extraction must be Timescale-aware: provision extensions first, dump/restore `user_data` tables including hypertable chunks, recreate retention policies and continuous aggregates, validate counts/checksums, then switch connection strings.

### Deployment Model

**Same physical Postgres database, separate schema initially; separable later.** Concretely:

- User-service tables live in a schema named `user_data` inside the existing application database (`app` in production, `relisten_db` locally).
- User service API is a separate .NET project in `RelistenApi.sln` (for example `RelistenUserApi/RelistenUserApi.csproj`), separate .NET process, separate Kubernetes deployment, and separate Docker image. It may share code through small class-library projects, but it should not be implemented as controllers inside the existing catalog API project.
- Connection pools are separate by role and search path: user-service read-write connections default to `user_data, public`; catalog hydration uses a read-only role and can move to a catalog replica later.
- Migration ownership: user-data migrations are owned by the user service, use SimpleMigrations (same pattern as the catalog API), and write their migration history into `user_data`. The catalog API does not create or mutate user-data tables.
- Local dev: create the `user_data` schema in the existing `relisten_db` container. No second local database is required.
- If user-data load grows, move `user_data` tables to a separate Postgres database or instance. Keep the API contract and table names stable; change connection strings and migration targets.

### Schema Bootstrap & Migration Safety

Because M1 uses the same physical database, schema placement must be explicit. The user service must not rely on the default Postgres search path.

Bootstrap requirements:

```sql
CREATE SCHEMA IF NOT EXISTS user_data;

-- Role names are illustrative; production can choose exact names.
GRANT USAGE ON SCHEMA user_data TO relisten_user_rw, relisten_user_ro;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA user_data TO relisten_user_rw;
GRANT SELECT ON ALL TABLES IN SCHEMA user_data TO relisten_user_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO relisten_catalog_ro;
ALTER ROLE relisten_user_rw SET search_path = user_data, public;
ALTER ROLE relisten_user_ro SET search_path = user_data, public;
ALTER DEFAULT PRIVILEGES IN SCHEMA user_data
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO relisten_user_rw;
ALTER DEFAULT PRIVILEGES IN SCHEMA user_data
    GRANT SELECT ON TABLES TO relisten_user_ro;
```

Migration requirements:

- User-service migration DDL should either schema-qualify user tables (`user_data.playlists`) or run through a connection whose `Search Path` is explicitly `user_data,public`.
- SimpleMigrations must use a user-service migration table in `user_data`, not the catalog API migration table in `public`.
- If the SimpleMigrations provider cannot be safely schema-scoped, create a distinct user-service migration table name and schema-qualify all migration DDL.
- Add a CI or startup check that the user-service migration table exists only in `user_data` and that no user-owned tables were created in `public`.
- Migration order: database extensions and schema bootstrap; `users`; `user_auth_methods`; `user_sessions` and refresh-token tables; playlists and edit log; favorites/settings; playback history hypertable and policies.

### Cross-Schema Catalog References

User data references catalog data by UUID (no foreign key constraints). Trade-offs:

- **No cross-schema foreign keys** — playlist entries store catalog UUIDs without FK constraints. This preserves the option to move user data to a separate database later and avoids coupling user writes to importer churn.
- **Bounded hydration queries** — the service may query both schemas when serving hydrated playlist responses, but hydration must stay bounded and explicit (see Section 12). Do not build application behavior that depends on arbitrary cross-schema joins.
- **Dangling references** — if a source/track is removed from the catalog (e.g., removed from archive.org), playlist entries become unresolvable. Display as "track no longer available" in the UI. See Section 4 for unavailable track handling.
- **No cross-domain transactions** — not needed. No operation requires atomic writes to catalog rows and user-data rows.
- **Playback attribution** — play records in `user_data.playback_history` include nullable `playlist_uuid` and `playlist_entry_uuid`. No FK enforcement against the catalog, but the data remains queryable while both schemas share one database.

### User Service Tech Stack

- **.NET** (same as existing API) — one language, shared patterns, small team
- **Dapper** for data access (consistent with existing API)
- **Same Postgres database, separate `user_data` schema** (separable later)
- **Redis** for rate limiting, session cache
- **Hangfire** for background jobs (batch aggregation, cleanup)

Unless a SQL snippet says otherwise, user-owned tables in this document are created in the `user_data` schema through the user service's search path.

---

## 2. Authentication

### Auth Methods

Two sign-in options in M1:

1. **Apple Sign-In** — one-tap on iOS, also works on web via Apple JS SDK
2. **Google Sign-In** — one-tap on Android, also works on web via Google Identity Services

No email magic links in M1. Relisten should not run an email sender, maintain deliverability, or depend on email as an account recovery path. Passkeys are also out of M1; Apple and Google are enough to start.

ATProto / Bluesky login is designed for later as another provider in `user_auth_methods`, with the user's DID as the provider subject.

### Auth Flow

All auth goes through a web-based flow hosted at `relisten.net/auth`. The mobile app opens a system auth session (ASWebAuthenticationSession on iOS, Chrome Custom Tabs on Android), not an embedded webview. The flow uses an authorization-code pattern — **refresh tokens never appear in URLs.**

```
Mobile app                    relisten.net/auth               User Service API
    │                              │                               │
    ├──opens auth session with     │                               │
    │  state + code_challenge─────>│                               │
    │                              ├──user picks method───────────>│
    │                              │  (Apple or Google)            │
    │                              │                               │
    │                              │<──auth_code (short-lived)─────┤
    │<──deep link with auth_code───┤                               │
    │   + state (verified)         │                               │
    │                              │                               │
    ├──POST /api/v3/library/auth/token                             │
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
- **Mobile deep-link sanitizer** — mobile must own first-class auth callback routes before account launch. Cold-start and warm-link handling must scrub `auth_code`, `code`, `state`, and any token-like query params before generic routing, logging, navigation state serialization, analytics, crash reporting, or error UI.
- **Refresh token storage** — Keychain on iOS, Keystore on Android, httpOnly secure cookie on web. Never in URLs, localStorage, or AsyncStorage.
- **Refresh token rotation** — each use of a refresh token issues a new one and invalidates the old. Detects token reuse (possible theft).
- **Session/device tracking** — each refresh token is associated with a device ID. Users can view and revoke active sessions.
- **Provider verification** — Apple and Google callbacks verify issuer, audience, signature, expiry, nonce, and provider subject before creating or linking an auth method.
- **Cookie-auth CSRF protection** — web refresh-token cookies use `Secure`, `HttpOnly`, and `SameSite=Lax` or `Strict`. Cookie-auth endpoints validate `Origin`, use an explicit CORS allowlist, require an anti-CSRF token or custom header, and return `Cache-Control: no-store`.

Note: the existing backend auth (`EnvUserStore` at `Startup.cs:337`, `EnvUserStore.cs:19`) is admin-only, cookie-based, env-backed plaintext auth. It is not reusable for public user accounts. The user service auth is built from scratch.

### Token Model

- **Access token** — JWT, 1 hour expiry. Contains user ID, username, session ID. Sent with every API request via `Authorization: Bearer` header.
- **Refresh token** — opaque, 1 year expiry, rotated on each use. Stored in device secure storage. Used to obtain new access tokens.
- **Auth code** — opaque, 60 second expiry, single-use. Exchanged for token pair via PKCE.

### Mobile Auth Service Contract

Mobile auth is a cross-cutting service, not an add-on to the catalog API client. The current catalog client is optimized for unauthenticated GETs, ETag freshness, and URL logging. M1 needs a separate user-service/auth layer that owns:

- Secure token storage in Keychain/Keystore, with no refresh tokens in AsyncStorage, Realm catalog rows, logs, or URLs.
- Refresh rotation handling, including retry boundaries and forced sign-in when reuse/revocation is detected.
- Active `scope_id` switching between anonymous and authenticated user scopes.
- Request auth middleware for `/api/v3/library` endpoints, including `Authorization: Bearer` injection and refresh-on-401.
- Sign-out/session revoke flow that clears secure token material and switches scope without deleting local scoped rows by default.
- Account deletion and data export UI entry points.
- Recent reauthentication flows for sensitive actions: link/unlink provider, export, delete account, and future collaborator/security-sensitive changes.

### Session Management

```sql
CREATE TABLE user_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id TEXT NOT NULL,
    device_name TEXT, -- "iPhone 15", "Chrome on Mac"
    platform TEXT NOT NULL, -- 'ios', 'android', 'web'
    last_used_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at TIMESTAMPTZ -- null = active
);

CREATE TABLE refresh_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES user_sessions(id) ON DELETE CASCADE,
    token_selector TEXT NOT NULL UNIQUE, -- public lookup prefix, not a secret
    token_secret_hash TEXT NOT NULL, -- hash of secret part, never stored plaintext
    status TEXT NOT NULL DEFAULT 'active', -- 'active', 'rotated', 'revoked', 'reuse_detected'
    issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    rotated_at TIMESTAMPTZ,
    replaced_by_token_id UUID REFERENCES refresh_tokens(id),
    reuse_detected_at TIMESTAMPTZ
);

CREATE INDEX idx_user_sessions_user_id ON user_sessions(user_id);
CREATE INDEX idx_refresh_tokens_session_id ON refresh_tokens(session_id);
```

Refresh tokens are formatted as `selector.secret`. The server looks up `selector`, verifies the secret hash, and atomically rotates the token. If a rotated token is presented again, mark it `reuse_detected`, revoke the whole session, and require the user to sign in again.

Users can list active sessions (`GET /api/v3/library/users/me/sessions`) and revoke any session.

### First-Time Signup

1. User authenticates via chosen provider
2. Prompted to choose a username (validated in real-time — see Section 7)
3. Optional display name
4. Account created, tokens issued

### Multiple Auth Methods

Users can link additional sign-in methods from settings. All methods link to the same account only after the user is already authenticated and has completed recent reauthentication.

Rules:

- Do not auto-link Apple and Google accounts by email address; provider email claims can be private, missing, or reassignable.
- Linking a provider uses a nonce-bound flow tied to the current session.
- Unlinking requires recent reauthentication and must not remove the account's last active auth method.
- Account export and account deletion also require recent reauthentication.

Adding Bluesky/AT Protocol auth in the future should not require a core `users` table change: create a `user_auth_methods` row with `provider = 'atproto'` and `provider_subject = did:...`. ATProto OAuth/PDS sync will still need its own design for auth grants, DPoP keys, PDS/auth-server discovery, scopes, token expiry, encrypted refresh metadata, and sync cursors.

---

## 3. User Profiles

### Schema

All tables in this section live under the `user_data` schema.

```sql
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username TEXT NOT NULL,
    username_lower TEXT NOT NULL UNIQUE, -- lowercase for case-insensitive uniqueness
    display_name TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE user_auth_methods (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    provider TEXT NOT NULL, -- 'apple', 'google'; future: 'atproto'
    provider_subject TEXT NOT NULL, -- Apple sub, Google sub, or future ATProto DID
    provider_claims JSONB, -- allowlisted non-PII claims only; no raw email
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (provider, provider_subject)
);
```

### Profile Fields

- **Username** — required, unique, 3-30 characters, alphanumeric + underscores, case-insensitive uniqueness (enforced via `username_lower`). See Section 7 for validation rules.
- **Display name** — optional, max 50 characters. Falls back to username when not set.
- **No email field on the core user profile** — Relisten does not send account emails or use email for login. Do not persist raw provider email claims unless a future design explicitly justifies retention, encryption, and deletion behavior.
- **No avatar uploads** — avoid hosting user-generated images. Use generated identicons or initials client-side.

---

## 4. Playlists & Blocks

### Core Concepts

- **Playlist** — an ordered collection of entries, owned by a user. Has a `current_revision` that increments on every operation for conflict detection.
- **Entry** — a reference to a specific track (by UUID) at a position in the playlist. Each entry has its own `uuid` (`playlist_entry_uuid`). The same track can appear multiple times as different entries. All operations, playback, analytics, and history reference `playlist_entry_uuid`, not `source_track_uuid`.
- **Block** — a group of entries that stay together during shuffle. Identified by a shared `block_uuid` on entries. Entries within a block must be positionally contiguous. `block_uuid` is a real stable UUID — not just an incidental grouping token. If block metadata (title, notes, type) is needed later, a `playlist_blocks` table can be added without changing entry identity.
- **Fractional indexing** — `position` is a text string supporting arbitrary insertion without rewriting other entries (e.g., "a", "aM", "b").
- **Block position** — `block_position` is an integer giving the entry's order within its block (0-indexed). This is deliberately not fractional indexing in M1.

### Ordering Decision

Use fractional indexing for playlist-level order only. Keep `block_position` as an integer ordinal inside a block.

Reasons:

- The global `position` column already determines where a standalone entry or whole block sits in the playlist.
- Blocks are expected to be short enough that renumbering `block_position` values inside one block is cheap and easy to reason about.
- A second fractional order inside each block creates two ordering systems that can disagree: `position` order and `block_position` order. The server would still need to reconcile them.
- Integer `block_position` values are easier for queue construction, shuffle playback, and debugging.

If a future `playlist_blocks` table materializes blocks as first-class rows, that table can have its own fractional `position` for ordering blocks relative to standalone entries. That should be a derived optimization or v2 model change, not a second source of truth in M1.

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

1. Group entries by shuffle unit key: `block_uuid ?? playlist_entry_uuid` (`null` `block_uuid` = standalone track = its own shuffle unit)
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
- Resume position is stored as `(playlist_uuid, playlist_entry_uuid, progress_seconds)` — not as a positional index

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
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT chk_playlist_entries_block_position
        CHECK (
            (block_uuid IS NULL AND block_position IS NULL)
            OR
            (block_uuid IS NOT NULL AND block_position IS NOT NULL AND block_position >= 0)
        )
);

CREATE INDEX idx_playlist_entries_playlist_uuid ON playlist_entries(playlist_uuid);
CREATE INDEX idx_playlist_entries_block_uuid ON playlist_entries(block_uuid);
CREATE UNIQUE INDEX idx_playlist_entries_playlist_position
    ON playlist_entries(playlist_uuid, position);
CREATE UNIQUE INDEX idx_playlist_entries_block_position
    ON playlist_entries(playlist_uuid, block_uuid, block_position)
    WHERE block_uuid IS NOT NULL;

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
- **`unlisted`** — anyone with a share token can view. Not discoverable. This is the default link-sharing mode once an owner chooses to share a playlist.
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

CREATE TABLE playlist_mobile_access_grants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    playlist_uuid UUID NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    source_share_token_id UUID REFERENCES playlist_share_tokens(id) ON DELETE SET NULL,
    device_id TEXT NOT NULL,
    token_selector TEXT NOT NULL UNIQUE, -- public lookup prefix, not a secret
    token_secret_hash TEXT NOT NULL, -- hash of secret part, never stored plaintext
    role TEXT NOT NULL DEFAULT 'viewer',
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

- Share URL landing format: `https://relisten.net/playlist/{shortId}?t={shareToken}`
- The owner can create multiple share tokens with different roles (viewer, editor)
- Creating a share token is the owner's explicit opt-in to link sharing. If the playlist is `private`, token creation atomically changes `visibility` to `unlisted`. If the owner wants to stop link access later, they revoke tokens and/or set visibility back to `private`.
- Tokens can be revoked without changing the playlist's `short_id`
- `public` playlists don't require a token — `short_id` alone is sufficient
- `unlisted` playlists require a valid, non-expired, non-revoked token
- `private` playlists are not accessible via link at all (only direct collaborator invites)

Share tokens in URLs are landing credentials, not durable bearer credentials:

- On first valid web token use, the server sets a short-lived httpOnly playlist-access cookie and redirects to the tokenless playlist URL.
- Playlist pages set `Referrer-Policy: no-referrer` or `same-origin` so tokens do not leak through `Referer`.
- Viewer tokens can grant anonymous read access through the short-lived cookie.
- Editor tokens require sign-in before write access. After sign-in, the token is converted into an accepted collaborator row or explicit editor grant; the token itself is not used as an API write credential.
- Logs and analytics must scrub `t=` query parameters.

### Mobile Share-Token Flow

Mobile Universal Links cannot rely on the browser's httpOnly cookie. The app may receive the token directly:

1. App receives `https://relisten.net/playlist/{shortId}?t={shareToken}`.
2. A first-class `/playlist/{shortId}` mobile route or global pre-router sanitizer handles the URL before the generic not-found route. It parses the token, immediately scrubs `t` from logs/navigation state, and navigates internally using the tokenless playlist route.
3. App calls `POST /api/v3/library/playlists/{playlistUuidOrShortId}/share-tokens/exchange` with:
   ```json
   {
       "token": "...",
       "device_id": "stable-device-id",
       "platform": "ios"
   }
   ```
4. Viewer token:
   - if signed out, the server creates a short-lived `playlist_mobile_access_grants` row and returns:
     ```json
     {
         "playlist_uuid": "uuid",
         "role": "viewer",
         "mobile_access_grant": {
             "token": "selector.secret",
             "expires_at": "2026-04-11T12:00:00Z",
             "header_name": "X-Relisten-Mobile-Grant"
         }
     }
     ```
     The grant secret is stored in secure storage; non-secret grant metadata can live in local-only scoped Realm data. The app never stores the original URL token.
   - if signed in, the app may offer Follow or Clone. Following creates durable viewer access; cloning creates an owned copy. The short-lived mobile access grant can still be used until the user chooses.
5. Editor token:
   - requires sign-in before write access.
   - after sign-in, exchange converts the token into an accepted collaborator row or equivalent durable editor grant for the authenticated user.
6. Reopened tokenless links still resolve if the viewer is a follower, owner, accepted collaborator, clone owner, public viewer, or presents an unexpired mobile grant with `X-Relisten-Mobile-Grant: selector.secret` and `X-Relisten-Device-Id: stable-device-id`.
7. Mobile logs, analytics, error screens, request metadata, and crash reports must scrub `t=` before serialization. The current mobile API client logs full URLs, so this is a real implementation requirement, not just a server concern.

Release gate: add cold-start and warm-link tests proving `/playlist/{shortId}?t=...` and auth callback URLs never reach the generic `+not-found` logging path with sensitive query params intact.

### Access Resolution

When a request arrives for a playlist:

1. If requester is owner or accepted collaborator → full access per their role
2. If playlist is `public` → viewer access (no token needed)
3. If playlist is `unlisted` and a valid share-token landing flow has established access → access per token role
4. If playlist is `private` and requester is not a collaborator → 404
5. Transitioning `public` → `private`: existing followers retain access; new requests without collaborator status get 404
6. Transitioning `public` → `unlisted`: existing followers retain access; new requests need a share token

For mobile, "established access" includes a short-lived mobile access grant returned by the exchange endpoint and verified against `playlist_mobile_access_grants`. That grant is a device-scoped bridge for Universal Links; it is not a durable share token and should expire quickly.

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

### Offline Identity & Placement

Offline edits need stable IDs before the server sees them. M1 requires client-generated UUIDs for objects created offline:

- `create_playlist` includes `playlist_uuid`
- `add_track` includes `entry_uuid`
- `create_block`, `add_tracks_as_block`, and `add_source_range_as_block` include `block_uuid`
- Multi-entry block adds include the client-generated `entry_uuids` in the same order as `source_track_uuids`

The server accepts these IDs idempotently if they are valid UUIDs, belong to the authenticated user's operation, and do not collide with an existing object outside the idempotent replay.

Clients may include anchor-based placement intent (`after_entry_uuid`, `before_entry_uuid`, `target_block_uuid`, `target_block_index`) and an optional `position_hint`. The server owns canonical `position` and `block_position` assignment. Operation responses return the canonical entries after renumbering.

### Deterministic Per-Operation Results

Every operation returns a result status. For batch operations, each operation gets its own result — batches are NOT all-or-nothing unless explicitly transactional. Independent operations may continue after a failure. Operations that depend on a failed prerequisite return `skipped_dependency`.

Result statuses:
- **`applied`** — operation succeeded, state changed
- **`noop_already_applied`** — idempotent replay, no state change
- **`noop_entry_missing`** — referenced entry was already deleted
- **`noop_block_empty`** — referenced block has no entries
- **`conflict_entry_deleted`** — tried to move/modify an entry that was deleted by another editor
- **`conflict_block_changed`** — tried to move/split a block that was modified by another editor. Server applies the operation to the block's current state if possible, or returns this status if the operation is no longer meaningful.
- **`rejected_contiguity`** — operation would break block contiguity
- **`rejected_limit`** — would exceed playlist entry/collaborator limits
- **`skipped_dependency`** — an earlier operation in the same offline batch failed and this operation referenced the object that would have been created or modified by it

### Conflict Scenarios

Specific scenarios and their deterministic outcomes:

- **Move entry after someone deleted it:** return `noop_entry_missing`. No state change.
- **Move block after someone split it:** move the current entries with that `block_uuid` to the new position. If the block_uuid no longer exists, return `noop_block_empty`.
- **Add track between entries that moved:** accept the client's anchors as placement intent, assign a new canonical position, return revised surrounding order.
- **Delete entry already deleted:** idempotent success if same `idempotency_key`, otherwise `noop_entry_missing`.
- **Offline batch with 50 ops and op 17 invalid:** ops 1-16 applied, op 17 returns its specific error status. Ops 18-50 continue only if they do not depend on op 17; dependent ops return `skipped_dependency`.

### Operation Catalog

Every operation is:
- Attributed to a user (`user_id`)
- Idempotent (via `idempotency_key` — replays are safe)
- Self-contained (all referenced entries, blocks, anchors, and client-generated IDs are explicit in the payload)
- Logged to `playlist_edit_log` with the full payload and result
- Applied to materialized state in `playlist_entries`
- Returns `result_revision` and `result_status`

#### Playlist Lifecycle

**`create_playlist`**
```json
{
    "op": "create_playlist",
    "playlist_uuid": "client-generated-playlist-uuid",
    "name": "Best Phish Jams 2024",
    "description": "Top segues from summer tour"
}
```

**`update_playlist`**
```json
{
    "op": "update_playlist",
    "playlist_uuid": "uuid",
    "name": "Best Phish Jams Summer 2024",
    "description": "Updated description",
    "visibility": "unlisted"
}
```

**`archive_playlist`** (soft delete)
```json
{
    "op": "archive_playlist",
    "playlist_uuid": "uuid"
}
```

#### Entry Operations

**`add_track`** — add a single track to the playlist
```json
{
    "op": "add_track",
    "playlist_uuid": "uuid",
    "entry_uuid": "client-generated-entry-uuid",
    "source_track_uuid": "catalog-track-uuid",
    "placement": {
        "after_entry_uuid": "entry-before-or-null",
        "before_entry_uuid": "entry-after-or-null",
        "target_block_uuid": null,
        "target_block_index": null,
        "position_hint": "aM"
    }
}
```

**`remove_entry`** — remove an entry from the playlist
```json
{
    "op": "remove_entry",
    "playlist_uuid": "uuid",
    "entry_uuid": "playlist-entry-uuid"
}
```

**`move_entry`** — reposition a track, optionally changing its block membership
```json
{
    "op": "move_entry",
    "playlist_uuid": "uuid",
    "entry_uuid": "playlist-entry-uuid",
    "placement": {
        "after_entry_uuid": "entry-before-or-null",
        "before_entry_uuid": "entry-after-or-null",
        "target_block_uuid": "block-uuid-or-null",
        "target_block_index": 2,
        "position_hint": "cG"
    }
}
```
- `target_block_uuid: null` removes the entry from any block (standalone)
- `target_block_uuid: "some-uuid"` moves the entry into that block at the specified target index
- Server computes canonical `position` and `block_position`, then validates block contiguity

#### Block Operations

**`create_block`** — group consecutive entries into a block
```json
{
    "op": "create_block",
    "playlist_uuid": "uuid",
    "block_uuid": "client-generated-block-uuid",
    "entry_uuids": ["entry-1", "entry-2", "entry-3"]
}
```
Server assigns the provided `block_uuid` and sets `block_position` (0, 1, 2...) based on current entry order. Reassigns entries to contiguous fractional positions if needed.

**`dissolve_block`** — ungroup entries (entries keep positions, `block_uuid` and `block_position` set to null)
```json
{
    "op": "dissolve_block",
    "playlist_uuid": "uuid",
    "block_uuid": "block-uuid"
}
```

**`move_block`** — reorder an entire block atomically
```json
{
    "op": "move_block",
    "playlist_uuid": "uuid",
    "block_uuid": "block-uuid",
    "placement": {
        "after_entry_uuid": "entry-before-or-null",
        "before_entry_uuid": "entry-after-or-null",
        "position_hint": "m"
    }
}
```
All entries in the block get new contiguous fractional positions at the target location. `block_position` values are preserved (internal order unchanged).

**`split_block`** — split a block at a given entry
```json
{
    "op": "split_block",
    "playlist_uuid": "uuid",
    "block_uuid": "block-uuid",
    "split_after_entry_uuid": "entry-uuid"
}
```
Entries after the split point get a new `block_uuid`. `block_position` values are renumbered in both resulting blocks.

**`merge_blocks`** — merge two adjacent blocks
```json
{
    "op": "merge_blocks",
    "playlist_uuid": "uuid",
    "block_uuid_1": "block-uuid-a",
    "block_uuid_2": "block-uuid-b"
}
```
All entries get `block_uuid_1`. `block_position` values are renumbered sequentially. Server validates blocks are adjacent.

#### Compound Operations

**`add_tracks_as_block`** — add multiple tracks as a block in one atomic operation (the "add this segue" flow)
```json
{
    "op": "add_tracks_as_block",
    "playlist_uuid": "uuid",
    "block_uuid": "client-generated-block-uuid",
    "entry_uuids": ["client-entry-1", "client-entry-2", "client-entry-3"],
    "source_track_uuids": ["track-1", "track-2", "track-3"],
    "placement": {
        "after_entry_uuid": "entry-before-or-null",
        "before_entry_uuid": "entry-after-or-null",
        "position_hint": "g"
    }
}
```
Server creates new entries with the shared `block_uuid` at contiguous canonical positions near the requested placement. `block_position` is assigned 0, 1, 2... in the order given. Always creates new entries — if a track already exists in the playlist, this adds another instance (tracks can appear multiple times).

**`add_source_range_as_block`** — add a contiguous range of tracks from a source set (the "add this show segment" flow)
```json
{
    "op": "add_source_range_as_block",
    "playlist_uuid": "uuid",
    "block_uuid": "client-generated-block-uuid",
    "entry_uuids": ["client-entry-1", "client-entry-2", "client-entry-3", "client-entry-4"],
    "source_uuid": "catalog-source-uuid",
    "start_track_position": 3,
    "end_track_position": 6,
    "placement": {
        "after_entry_uuid": "entry-before-or-null",
        "before_entry_uuid": "entry-after-or-null",
        "position_hint": "g"
    }
}
```
Server resolves the source's tracks from the catalog (via read replica), creates entries for tracks at positions 3-6, assigns the provided shared `block_uuid`, and returns the generated canonical entry UUIDs if the client did not provide them. For offline use, the client should provide entry UUIDs up front. This is the primary flow for "add this segue from this show."

#### Collaboration Operations

**`invite_collaborator`**
```json
{
    "op": "invite_collaborator",
    "playlist_uuid": "uuid",
    "username": "jake"
}
```

**`remove_collaborator`**
```json
{
    "op": "remove_collaborator",
    "playlist_uuid": "uuid",
    "user_uuid": "user-uuid"
}
```

**`accept_invite`**
```json
{
    "op": "accept_invite",
    "playlist_uuid": "uuid"
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
- `add_track` with a `block_uuid` assigns a position within the block's current range
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

M1 builds on the existing Realm client data layer. Do not combine user accounts/playlists with a Realm → TanStack DB migration. The playlist/account work is already large enough, and Realm is sufficient for local cache, offline writes, and live UI updates.

The sync code should still be React-independent. Put the user-data repository and sync runner in plain TypeScript services that can be called by React screens, CarPlay code, CarPlay/Cast playback paths, app launch/foreground hooks, network reconnect hooks, and write completion hooks.

### Mobile Scoped Realm Contract

Mobile uses one Realm database. Do not create a separate Realm file per account, and do not model account-owned state as booleans on catalog rows long-term. Catalog rows (`Artist`, `Show`, `Source`, `SourceTrack`, etc.) remain shared local cache. Every user-owned Realm row has a `scope_id` field (TypeScript model property can be `scopeId`) so the app can switch between signed-out, signed-in, and future multi-account scopes without deleting local data.

Required scopes:

- **Anonymous device scope** — signed-out local data: existing favorites, listening history, pending local state, short-lived mobile playlist access grants.
- **Authenticated user scope** — rows owned by one Relisten account.
- **Future external scopes** — ATProto or other provider-backed identity can map in later without changing catalog cache identity.

Scoped user-owned rows include playlists, playlist entries, playlist followers/collaborator views, favorites, user settings, pending user operations, playback history journal rows, mobile playlist access grants, auth/session metadata, and one-time migration markers.

Logout behavior:

- Revoke the current session/refresh token when possible and forget access/refresh tokens from secure storage.
- Switch the active scope to anonymous device scope.
- Do not delete the signed-in user's local scoped rows by default. This preserves offline cache and avoids destructive surprises when a user signs out temporarily.
- Provide an explicit "remove this account's local data from this device" action for privacy. That action deletes rows for the selected `scope_id` and clears secure token material for that account/device.

```
┌──────────────────────────────────────────────┐
│  Realm-backed user data layer                │
│                                              │
│  userDataRealm.ts — user Realm models        │
│  userSyncService.ts — outbox + pull sync     │
│  playlistRepository.ts — read/write API      │
│                                              │
│  Plain TypeScript service boundary           │
│  No React dependency                         │
└───────────┬──────────────────┬───────────────┘
            │                  │
     ┌──────┴──────┐    ┌─────┴────────────┐
     │  React UI   │    │  CarPlay /       │
     │             │    │  Sync triggers   │
     │ Realm hooks │    │                  │
     │  or wrapper │    │  service calls   │
     │             │    │  (callback)      │
     └─────────────┘    └──────────────────┘
```

**Explicit non-prerequisite:** Realm → TanStack DB is not required for M1. A future TanStack DB migration can replace the persistence layer after the account/playlist API contract is stable.

### Collections

```
User Realm models (bidirectional sync with user service):
  playlists          — playlist metadata, scoped by scope_id
  playlistEntries    — entries with positions, block UUIDs, block positions, scoped by scope_id
  favorites          — entity type + entity UUID, scoped by scope_id
  userSettings       — JSON settings blob, scoped by scope_id
  pendingUserOps     — durable outbox for playlist/favorite/settings mutations, scoped by scope_id

Local-only Realm or existing local storage models (batch upload, never download full history):
  playbackHistory    — local journal, scoped by scope_id, batch-synced up
  mobileAccessGrants — non-secret metadata for short-lived playlist grants, scoped by scope_id

Existing Realm catalog models (read-only, fetch-on-navigate, etag-cached):
  artists, shows, sources, sourceTracks, sourceSets,
  venues, tours, years, eras, setlistSongs, reviews,
  popularity, etc.
```

Catalog data continues to follow the existing Realm pattern: fetch when the user navigates to a page, cache locally, and use etag-based freshness checks to avoid redundant fetches. Not polling — on-demand with caching.

### Catalog/User Data Composition

M1 does not need a new query engine for catalog/user joins. Use the same practical composition pattern the app already uses:

- Realm stores catalog objects and user objects separately.
- View models join by UUID in repository/service code, with memoized lookups where needed.
- Server-hydrated playlist responses are available for web and for mobile fallback/debugging.
- Favorite state can be read from `user_favorites` Realm rows instead of denormalizing `isFavorite` onto catalog objects long-term.

### Sync by Data Type

| Data | Write path | Sync direction | Offline behavior |
|---|---|---|---|
| Playlists + entries | Operation → offline outbox → server → materialized state syncs back | Bidirectional | Queue operations locally, optimistic UI, replay on reconnect |
| Favorites | Toggle → offline outbox → server → syncs back | Bidirectional | Toggle locally, sync when online |
| Playback history | Write to local persistence → batch upload | Up only (never downloads full history to devices) | Accumulates locally, batch syncs |
| User settings | Write → server → syncs to other devices | Bidirectional | Save locally, sync when online |
| Catalog data | Read-only from existing API | Down only | Cached in Realm, fetched on-navigate with etag |

### Incremental Pull Sync & Tombstones

Realm-first sync still needs a server pull contract. The client stores a per-scope sync cursor and calls `GET /api/v3/library/sync?cursor=...` on app launch, foreground, network reconnect, and after successful outbox replay or local writes.

Do not promise native background sync in M1 unless iOS/Android background task infrastructure is explicitly added and tested. "Sync on launch/foreground/reconnect/after writes" is the M1 contract.

The response includes:

- changed playlists and entries the user owns, collaborates on, or follows
- changed favorites and settings
- pending invitations
- tombstones for deleted/archived playlists, removed entries, unfavorited entities, removed collaborators, and revoked follower access
- `next_cursor`

Server-side rows that need cross-device deletion semantics must have either `updated_at` + `deleted_at` or a separate tombstone record retained long enough for devices that were offline. Clients apply tombstones to Realm before applying changed rows.

### Offline Outbox (Playlist Operations & Favorites)

Use a Realm-backed outbox for user-data mutations:

1. User performs a mutation (e.g., adds a track to a playlist)
2. The app applies optimistic Realm state immediately (UI updates)
3. The operation is serialized into `pendingUserOps` in Realm (survives app restart)
4. When online, the outbox replays operations in order against the user service API
5. Each operation has an idempotency key — replays are safe
6. Create operations use client-generated UUIDs so later queued operations can reference objects that were created offline
7. Server applies the operation, returns authoritative state including `current_revision`
8. The sync service reconciles optimistic Realm state with server state

On app launch, the outbox loads pending operations and resumes sync.

### Offline Playback History (Batch Upload)

Playback history does not use the playlist/favorites outbox — it's append-only, high-volume, and doesn't need optimistic UI or conflict resolution.

1. Track starts playing → write to local persistence immediately
2. Entry includes `scope_id`, `client_event_uuid`, `device_id`, and `synced: false`
3. Sync service batches unsynced entries when online after launch, foreground, network reconnect, and track/write events
4. `POST /api/v3/library/history/batch` accepts up to 500 entries per request
5. Server deduplicates through `playback_history_ingest_keys` on `(user_id, device_id, client_event_uuid)`. A secondary tolerance window can catch legacy duplicate uploads, but it must include `playlist_entry_uuid` when present.
6. On success, entries marked `synced: true` locally

This handles hundreds of offline listens on a trip without introducing a second client database.

Current mobile writes local `PlaybackHistoryEntry` rows tied to `SourceTrack`, `Artist`, `Show`, and `Source`, then uploads anonymous source-track plays one at a time through `/api/v2/live/play`. M1 must migrate to a scoped local journal and authenticated batch upload without breaking signed-out local history.

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
- **Unlisted** — owner generates a revocable share token. The token URL is a landing URL; after validation, the server redirects to the tokenless playlist URL with short-lived access established.
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
- Public playlist responses are cacheable by playlist revision (`ETag: revision-{current_revision}`)
- Unlisted playlist responses require validated share access and should be `private, no-store` unless a later design adds cache keys that include the validated access context
- Viewer-specific state (is_following, is_collaborator) is fetched separately, never cached in the public response
- Crawler/embed/OG-image requests should be rate-limited and served from cache to protect read-replica load
- Token landing pages set `Cache-Control: no-store`, scrub token query params from logs/analytics, and redirect to tokenless URLs

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
- **User playback history** (`user_data` schema) — per-user, rich, partitioned, retained
- **Anonymous aggregate plays** (catalog DB) — for popularity/trending, stays in `source_track_plays`

Do not give the user service broad write access to catalog tables in M1. If popularity/trending needs user play events, route anonymized aggregate writes through the existing catalog API/job or a narrowly scoped database function that can only insert approved fields into `source_track_plays`.

### Schema

User playback history is stored as a TimescaleDB hypertable from the start:

```sql
CREATE TABLE playback_history (
    id UUID NOT NULL DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    client_event_uuid UUID NOT NULL, -- generated by device; idempotency key for uploads
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
SELECT create_hypertable('user_data.playback_history'::regclass, 'played_at');

-- Indexes for common queries
CREATE INDEX idx_playback_history_user_id_played_at ON playback_history(user_id, played_at DESC);
CREATE INDEX idx_playback_history_playlist_uuid ON playback_history(playlist_uuid);
CREATE INDEX idx_playback_history_client_event
    ON playback_history(user_id, device_id, client_event_uuid);

CREATE TABLE playback_history_ingest_keys (
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id TEXT NOT NULL,
    client_event_uuid UUID NOT NULL,
    playback_history_id UUID NOT NULL,
    playback_history_played_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, device_id, client_event_uuid)
);
```

Note: `id` is not a PRIMARY KEY because TimescaleDB hypertables require the partitioning column (`played_at`) in unique constraints. Use `(id, played_at)` as composite PK if uniqueness constraints are needed.

Race-safe upload dedupe uses `playback_history_ingest_keys`, not a check-then-insert against the hypertable. The batch ingest transaction inserts an ingest key with `ON CONFLICT DO NOTHING`; only rows whose key was inserted are written to `playback_history`. A replay returns the existing accepted result without adding a duplicate play.

If the deployed TimescaleDB/Postgres version cannot support the desired `ON DELETE CASCADE` relationship on a hypertable, account deletion must call a deletion job/stored procedure that deletes `playback_history` rows by `user_id` and verifies zero rows remain before completing account deletion.

### Retention Policy

```sql
-- Keep detailed per-user history for 2 years
SELECT add_retention_policy('user_data.playback_history'::regclass, INTERVAL '2 years');

-- Continuous aggregate for long-term stats (computed, not stored per-play)
CREATE MATERIALIZED VIEW user_data.playback_stats_monthly
WITH (timescaledb.continuous) AS
SELECT
    user_id,
    time_bucket('1 month', played_at) AS month,
    count(*) AS play_count,
    count(DISTINCT source_track_uuid) AS unique_tracks,
    count(DISTINCT source_uuid) AS unique_sources,
    count(DISTINCT playlist_uuid) FILTER (WHERE playlist_uuid IS NOT NULL) AS playlists_used
FROM user_data.playback_history
GROUP BY user_id, time_bucket('1 month', played_at);
```

### Two Consumption Patterns

**Local device history (fast, offline):**
- "What was I listening to recently?" — query local persistence, sorted by `played_at`
- Immediate, no network, works offline
- Day-to-day UX

**Aggregated cross-device stats (server-side, future):**
- "Your year in Relisten" / wrapped-style features
- Total listening time, top artists, top shows, listening streaks
- Computed server-side from `playback_history` and `playback_stats_monthly`
- API endpoint: `GET /api/v3/library/history/stats?period=2025`

### Play Attribution

When a track plays from a playlist, we record both `playlist_uuid` and `playlist_entry_uuid`. Because entries have their own UUIDs, this correctly attributes plays even when the same track appears multiple times in a playlist. This enables:

- "Most played blocks" — join entry → block_uuid, aggregate by block_uuid across playlists
- Radio segment popularity — analyze which source_track_uuid groupings appear together across playlists and weight by play count (see Section 13)
- Per-playlist analytics — "this playlist has been played 342 times"

### Mobile History Migration Contract

Current mobile history is local, source-track based, and uploads anonymous plays one at a time through the existing live-play endpoint. M1 changes this to a scoped local journal plus authenticated batch upload.

Required mobile fields per local journal row:

- `scope_id` — active anonymous or authenticated user scope
- `client_event_uuid` — stable idempotency key generated when the play is recorded
- `device_id` — stable per-install/device identifier used for dedupe, stored outside catalog models
- `source_track_uuid` and `source_uuid`
- `show_uuid` and `artist_uuid` when available locally, to support later catalog hydration/debugging
- nullable `playlist_uuid` and `playlist_entry_uuid` from Queue V2
- `played_at`, platform/app version, playback flags, and sync status

Behavior:

- **Authenticated with history enabled** — batch upload to `POST /api/v3/library/history/batch`, deduped by `playback_history_ingest_keys` on `(user_id, device_id, client_event_uuid)`.
- **Signed out** — preserve existing local history UX. Continue anonymous aggregate reporting only if the current signed-out behavior allows it; do not create per-user history rows.
- **History disabled** — do not write authenticated history and do not batch upload. Existing anonymous aggregate reporting must obey the user's setting or have an explicit product decision if it remains separate from personal history.
- **Playlist playback** — Queue V2 must provide `playlist_uuid` and `playlist_entry_uuid`; falling back to `source_track_uuid` loses duplicate-track attribution and is not acceptable for playlist plays.
- **Migration** — keep existing local history rows visible. New rows use the scoped journal schema. Do not bulk-upload old local history on first sign-in unless the user explicitly opts in or the product decision is documented; otherwise only new authenticated plays upload.

### Catalog Popularity Integration

When processing user history batch uploads, the user service may emit anonymized aggregate play events for the existing popularity infrastructure. In M1, do not grant the user service broad write access to the catalog schema. Pick one narrow integration:

- call an existing catalog API/job that owns writes to `source_track_plays`
- enqueue an anonymized event for a catalog-owned worker
- expose a `SECURITY DEFINER` function or narrow `GRANT INSERT` only for approved `source_track_plays` fields

This ensures:
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
    entity_type TEXT NOT NULL, -- 'artist', 'show', 'source', 'track', 'tour', 'song'
    entity_uuid UUID NOT NULL, -- references catalog UUID
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ, -- null = active favorite; non-null = tombstone for sync
    CONSTRAINT chk_user_favorites_entity_type
        CHECK (entity_type IN ('artist', 'show', 'source', 'track', 'tour', 'song')),
    PRIMARY KEY (user_id, entity_type, entity_uuid)
);
```

### Sync Behavior

- Favorites are Realm user-data rows synced bidirectionally with the user service. Active favorites have `deleted_at = null`.
- Toggling a favorite writes to Realm immediately and queues a sync operation.
- On new device sign-in, all favorites download immediately (small dataset).
- Favorites are joined with catalog data in repository/view-model code by UUID.
- Unfavorite operations update `deleted_at` instead of relying on absence. The sync cursor carries these tombstones to other devices.
- Source favorites are required in M1. Current mobile uses `Source.isFavorite` to prioritize source selection and library behavior. Do not silently drop source favorites or collapse them into show favorites.
- Tour and song favorites are also preserved because current mobile has favorite flags on those Realm models. If product wants to remove them, that needs an explicit migration/deprecation path.

If source favorites are ever deprecated, that needs a separate product migration: map existing source favorites to a visible replacement, explain changed source-selection behavior, and keep compatibility for users whose library depends on a specific recording/source.

### Migration from Existing Realm Favorite Flags

Currently `isFavorite` is a boolean flag on catalog Realm objects including Artist, Show, Source, SourceTrack, Tour, and Song. During account signup or first authenticated sync:

1. Read all catalog Realm objects where `isFavorite == true`.
2. Create corresponding `user_favorites` rows locally and enqueue a batch sync.
3. Keep the old flags during rollout so signed-out behavior does not regress.
4. Record a one-time migration marker so the same local favorites are not repeatedly re-enqueued.
5. After authenticated favorites are stable, treat `user_favorites` as the source of truth for signed-in users. Removing the old catalog flags can happen in a later cleanup, not as part of M1.

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

The device-specific settings stay local only. Account-wide settings sync via the user service.

---

## 12. Hydrated Playlist DTO & Query Contract

When the user service serves a playlist (for web display, embed, or mobile with `?hydrate=true`), it must hydrate entry UUIDs against the catalog read replica.

API v3 serializes JSON field names as `snake_case`. C# models should keep normal C# naming conventions only if the user service explicitly configures snake-case serialization for its v3 DTOs. Do not assume the existing catalog API's `ApiV3ContractResolver` handles naming; add contract tests that serialize representative user-service DTOs and assert snake-case wire names.

### Hydration Query Contract

Hydration is performed as a bounded set of queries, not per-entry fanout:

1. Fetch playlist metadata + all entries from `user_data` (1 query)
2. Collect unique `source_track_uuid` values from entries
3. Batch-fetch track metadata from the catalog schema or catalog read replica: `SELECT * FROM source_tracks WHERE uuid IN (...)` (1 query)
4. Batch-fetch source metadata: `SELECT * FROM sources WHERE id IN (...)` (1 query)
5. Batch-fetch show metadata: `SELECT * FROM shows WHERE id IN (...)` (1 query)
6. Batch-fetch artist metadata: `SELECT * FROM artists WHERE id IN (...)` (1 query)

Total: 5 bounded queries maximum. No recursive fanout.

### Hydrated Playlist Response

```json
{
    "playlist": {
        "uuid": "...",
        "short_id": "Xk9mPq2v",
        "name": "Best Phish Jams 2024",
        "description": "Top segues from summer tour",
        "visibility": "unlisted",
        "current_revision": 47,
        "owner": { "username": "alec", "display_name": "Alec" },
        "collaborators": [
            { "username": "jake", "role": "editor" }
        ],
        "entry_count": 42,
        "total_duration": 18340,
        "created_at": "...",
        "updated_at": "..."
    },
    "entries": [
        {
            "uuid": "entry-uuid-1",
            "position": "a",
            "block_uuid": "block-uuid-1",
            "block_position": 0,
            "added_by": { "username": "alec" },
            "available": true,
            "track": {
                "uuid": "source-track-uuid",
                "title": "Scarlet Begonias",
                "duration": 612,
                "mp3_url": "https://archive.org/...",
                "show": {
                    "uuid": "show-uuid",
                    "date": "1977-05-08",
                    "display_date": "1977-05-08",
                    "venue": { "name": "Barton Hall", "city": "Ithaca", "state": "NY" }
                },
                "artist": { "uuid": "artist-uuid", "name": "Grateful Dead" },
                "source": { "uuid": "source-uuid", "is_soundboard": true }
            }
        },
        {
            "uuid": "entry-uuid-2",
            "position": "aM",
            "block_uuid": "block-uuid-1",
            "block_position": 1,
            "added_by": { "username": "alec" },
            "available": false,
            "track": null
        }
    ]
}
```

- `available: false` with `track: null` indicates an unavailable/orphaned track
- `total_duration` only counts available entries
- Viewer-specific state (is_following, is_collaborator, can_edit) is returned in a separate `viewer` field, only when authenticated. Never cached in the public response.

### Caching

- Public playlists: cache by `ETag: playlist-{short_id}-rev-{current_revision}`
- Unlisted playlists: require validated share access and should be private/no-store unless cache keys include the validated access context
- Invalidate on any operation that changes `current_revision`
- Viewer-specific state fetched separately via `GET /api/v3/library/playlists/{playlistUuid}/viewer-state` (authenticated, never cached publicly)
- Rate-limit crawler/OG-image/embed requests to protect read-replica load

### Non-Hydrated Response

With `?hydrate=false` (default for mobile), the response includes entries with raw UUIDs only — no track/show/artist metadata. It must still include `available` when the server already knows a catalog reference is unresolved.

The mobile client joins with catalog data from Realm by UUID. Before playback, the mobile client must run `ensureCatalogForPlaylist(entries)`:

1. Collect playlist `source_track_uuid` values missing from Realm.
2. Resolve missing entries to `show_uuid`/`source_uuid`. A non-hydrated playlist response should include these UUIDs when available so mobile can use the existing catalog path efficiently.
3. Fetch full `ShowWithSources` payloads through the existing catalog API for the affected shows, or use a new mobile-compatible batch catalog endpoint that returns the same full source/source-set/source-track graph shape.
4. Upsert the full catalog graph into Realm. Minimal hydrated playlist DTOs are not enough for current mobile playback because `SourceTrack` Realm rows require fields such as source set UUID, track position, slug, timestamps, URLs, and linked source/show/artist data.
5. Mark entries that still cannot be hydrated as unavailable and skip them during playback.

`?hydrate=true` is sufficient for web display and debugging, but it is not by itself the mobile playback hydration contract unless it returns the full mobile-compatible catalog graph. M1 mobile playback should prefer full `ShowWithSources` hydration before constructing Queue V2 items.

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

Users could sign in with their DID via ATProto / Bluesky. The `playlist_edit_log` maps to AT Protocol records — each operation is a record owned by the user in their PDS. The server becomes an aggregator: it reads operation records from users' PDSes and materializes playlist state.

**What v1 provides:** operation log as first-class data with stable schemas, idempotency keys, per-op result statuses, and user attribution. `user_auth_methods` can add `provider = 'atproto'` with the DID as `provider_subject`, so the core `users` table does not need a DID column. A later ATProto implementation still needs its own auth-grant design for PDS URL, auth issuer, scopes, DPoP key metadata, encrypted refresh/session data, expiry, and sync cursor.

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

**What v1 provides:** user identity and auth infrastructure. Reviews would be a new collection in the `user_data` schema.

---

## 14. Deployment & Infrastructure

### New Components

- **User Service API** — .NET application, deployed as a Kubernetes deployment alongside the existing API. Separate Docker image.
- **`RelistenUserApi` project** — separate ASP.NET Core web project in `RelistenApi.sln`, runnable and deployable independently from the existing catalog API.
- **`user_data` Postgres schema** — user-owned tables inside the existing application database (separable to its own database/server later).
- **TimescaleDB extension** — required in the application database for the `user_data.playback_history` hypertable.

### Environment Variables (User Service)

```
USER_DATA_DATABASE_URL=Host=host;Port=5432;Database=app;Username=relisten_user_rw;Password=...;Search Path=user_data,public
USER_DATA_SCHEMA=user_data
CATALOG_DATABASE_URL=Host=host;Port=5432;Database=app;Username=relisten_catalog_ro;Password=...;Search Path=public
REDIS_URL=redis://host:port
JWT_SECRET=...
JWT_ISSUER=relisten.net
APPLE_CLIENT_ID=...
APPLE_TEAM_ID=...
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
OPENAI_API_KEY=...  (for moderation API, free tier)
```

### Connection Pools

- **User-data read-write** — primary pool for all user service operations; role has write access to `user_data` and read access only where needed.
- **User-data read-only** — for read-heavy endpoints (playlist listing, history queries); role has read access to `user_data`.
- **Catalog read-only** — separate pool, same database initially and read replica when available, for hydration queries only.

### Cache/Header Policies

- Public playlist endpoints (`GET /api/v3/library/playlists/{playlistUuidOrShortId}` without auth): `Cache-Control: public, max-age=300` with ETag by revision
- Authenticated `/me` endpoints: `Cache-Control: private, no-store`
- Auth endpoints: `Cache-Control: no-store`
- Share-token landing pages: redirect to tokenless URLs and set `Referrer-Policy: no-referrer` or `same-origin`
- Distinct policies prevent authenticated state from leaking into CDN/proxy caches

### Backup Strategy

- **One physical application Postgres** — before launch, production must have WAL archiving, point-in-time recovery, and a tested restore drill with explicit RPO/RTO targets. User data makes the application database non-disposable.
- **`user_data` restore procedure** — document and test schema/table-level restore for `user_data`, including Timescale hypertable chunks, retention policies, continuous aggregates, grants, and migration history.
- **Catalog rebuild safety** — catalog importers and restore scripts must target only catalog-owned `public` objects. Do not run destructive whole-database restores, `pg_restore --clean`, or "drop and recreate database" playbooks against production after `user_data` exists.
- **Redis** — ephemeral (rate limit counters, session cache). No backup needed.

### User Data Export / Delete

- Users can request a full export of their data (GDPR-style): playlists, favorites, history, settings
- Account deletion hard-deletes all user data. Cascading FKs handle ordinary user-data tables; `playback_history` must be covered by FK cascade or an explicit deletion job that verifies zero hypertable rows remain for the user before the deletion is marked complete.
- Collaborative playlists where the user was an editor lose that editor. Playlists the user owned are permanently deleted.
- Export format: JSON archive

### Mobile Release Gates

- If mobile allows account creation or sign-in, the app must expose account deletion before App Store release. This is a mobile UI/release requirement, not just `DELETE /api/v3/library/users/me`.
- Mobile settings must expose session sign-out/revoke, account export request, and account deletion entry points.
- Account deletion/export/unlink provider actions require recent reauthentication.
- The app must include a user-visible way to remove one account's local scoped data from the current device without deleting the server account.

### Local Development

Add to local setup:
- Create the `user_data` schema in the existing `relisten_db` database.
- Enable required extensions through a privileged bootstrap step, including `CREATE EXTENSION IF NOT EXISTS timescaledb;` if playback history is enabled locally.
- Run user-service migrations with `Search Path=user_data,public` and verify no user-owned tables were created in `public`.
- User service runs as a separate `dotnet run` process or Docker container.
- Seed script for test user accounts.

---

## 15. Mobile M1 Contract

M1 must not treat mobile as a passive API consumer. Playlists/accounts change local persistence, queue identity, auth, deep links, history, CarPlay, Cast, and App Store release requirements. The server model is a reasonable foundation only if this mobile contract ships with it.

### P0: Queue V2 Before Playlist Playback

The current mobile playback queue is source-track based:

- `PlayerQueueTrack` wraps one `SourceTrack`
- queue item identity is an ephemeral runtime identifier
- persisted `PlayerState` stores `queueSourceTrackUuids`, shuffled source-track UUID arrays, active indexes, shuffle state, and repeat state
- CarPlay queue display and Cast queue payloads are built from the same source-track queue model

Playlist playback requires queue identity to move from `sourceTrackUuid` alone to playlist-aware queue items. This is a prerequisite for duplicate tracks, block shuffle, stable resume after reorder, play attribution, unavailable entries, Cast, CarPlay, and persisted restore.

TypeScript models can use idiomatic camelCase; v3 API wire JSON remains `snake_case`.

```ts
type PersistedQueueItem =
  | {
      kind: 'catalog'
      sourceTrackUuid: string
    }
  | {
      kind: 'playlist'
      playlistUuid: string
      playlistEntryUuid: string
      blockUuid: string | null
      blockPosition: number | null
      sourceTrackUuid: string
    }

interface PersistedQueueStateV2 {
    schemaVersion: 2
    items: PersistedQueueItem[]
    currentItemKey: string
    shuffleMode: 'off' | 'tracks' | 'blocks'
    repeatMode: 'off' | 'queue' | 'track'
    updatedAt: string
}

interface RuntimePlaylistQueueItem {
    playlistUuid: string
    playlistEntryUuid: string
    blockUuid: string | null
    blockPosition: number | null
    sourceTrackUuid: string
    track: SourceTrack
}
```

Queue V2 acceptance criteria:

- Block-aware shuffle groups by shuffle unit key `blockUuid ?? playlistEntryUuid`, shuffles groups, and preserves internal `blockPosition` order for non-null blocks.
- Standalone entries (`blockUuid === null`) never collapse into one shared null block; each standalone `playlistEntryUuid` is its own shuffle unit.
- Playback cursor is `playlistEntryUuid` for playlist queues, not source-track UUID or index.
- Reorder while playing does not interrupt the current entry; deleting the current entry advances deterministically.
- Existing catalog/source playback continues to use `kind: 'catalog'` queue items.
- Existing persisted source-track queues migrate to `PersistedQueueStateV2` with `kind: 'catalog'` items.
- Playlist queues persist UUIDs and block metadata only. Runtime playback hydrates `SourceTrack` from Realm after `ensureCatalogForPlaylist`.
- Switching between catalog playback and playlist playback resets stale native shuffle/session state and rebuilds the native queue from Queue V2.
- Unavailable entries are visible in playlist UI, skipped during playback, and preserved in queue state for rehydration if the catalog row becomes available later.
- Partial-offline blocks shuffle as one unit, but only playable offline entries are enqueued in offline mode.
- Playback history receives `playlistUuid` and `playlistEntryUuid` from the queue item for playlist-originated plays.

### P0: Scoped Realm User Data

Use one Realm database with scoped user-owned rows, as described in Section 6. Do not add user-owned booleans to catalog rows for M1 account state. Existing catalog `isFavorite` flags remain only as migration/source compatibility during rollout.

Mobile acceptance criteria:

- Every user-owned row has `scope_id`/`scopeId`.
- Active scope switches on sign-in/sign-out.
- Logout revokes/forgets tokens and switches scope; it does not delete local account data by default.
- Settings exposes an explicit remove-local-data action for a selected account scope.
- Sync cursors, pending operations, mobile access grants, favorite rows, playlist rows, settings, and playback journal rows are scope-bound.

### P0: Mobile Share-Token Exchange

Mobile implements the Universal Link flow from Section 4:

- App receives `/playlist/{shortId}?t=...`.
- App scrubs `t` from logs, analytics, navigation state, error UI, and request metadata before serialization.
- App exchanges the token and device id with `POST /api/v3/library/playlists/{playlistUuidOrShortId}/share-tokens/exchange`.
- Viewer token yields short-lived mobile access for signed-out viewing, or Follow/Clone choices after sign-in.
- Editor token requires sign-in, then converts to collaborator/editor access.
- Tokenless reopened links resolve through durable relationship state or an unexpired mobile access grant presented via `X-Relisten-Mobile-Grant` and `X-Relisten-Device-Id`.

### P1: Mobile Auth/User Service Layer

Do not bolt auth onto the existing catalog API client. Mobile needs a user-service client and auth/session service with:

- secure token storage
- refresh rotation and 401 retry handling
- active scope switching
- auth middleware for `/api/v3/library`
- session sign-out/revoke
- account deletion/export UI
- recent reauth flows for sensitive actions

### P1: Favorites And History Migration

Favorites:

- Backend and mobile models include source favorites (`entity_type = 'source'`) and preserve current tour/song favorites.
- First authenticated sync migrates existing local Artist, Show, Source, SourceTrack, Tour, and Song favorites into scoped `user_favorites`.
- Signed-out favorite behavior remains compatible during rollout.

History:

- Local history moves to a scoped journal with `clientEventUuid`, `deviceId`, playlist attribution, and sync status.
- Authenticated upload uses `POST /api/v3/library/history/batch`.
- Signed-out behavior remains local-first and compatible with existing anonymous aggregate reporting.
- History-disabled behavior is explicit and tested.
- Existing local history remains visible; old rows are not silently uploaded as user history.

### P1: CarPlay And Cast Acceptance Criteria

Playlist queue behavior must work outside React screens:

- CarPlay queue display uses Queue V2 item identity and shows the correct current playlist entry, including duplicate source tracks.
- CarPlay shuffle/repeat controls update Queue V2 and native playback state consistently.
- CarPlay source/history playback still works for catalog queues.
- Cast queue handoff carries playlist item identity in queue item custom data, including `playlistUuid`, `playlistEntryUuid`, `blockUuid`, `blockPosition`, and `sourceTrackUuid`.
- Cast/native transitions preserve playlist attribution and current playlist entry.
- Block shuffle behavior is identical across native, CarPlay, Cast, and restored persisted queues.
- Unavailable and offline-only entries are handled consistently across React UI, CarPlay, and Cast.

### P2: Playlist Mobile UX Contract

Mobile UX needs explicit flows before implementation:

- My Library playlist sections: owned, following, collaborations, invitations, and recent playlist activity.
- Signed-out playlist states: public viewing, unlisted viewer access, follow/clone sign-in prompts, editor-token sign-in requirement.
- Add to Playlist from track menus.
- Add contiguous source range as block from source/show track lists.
- Reorder entries and blocks.
- Invite inbox, accept/decline, and collaborator state.
- Follow and clone flows.
- Unavailable track display.
- Partial-offline block display and playback behavior.
- Conflict result states after sync: applied, noop, conflict, rejected, skipped dependency.

---

## Appendix A: Glossary

| Term | Definition |
|---|---|
| **Block** | A group of playlist entries that stay together during shuffle. Identified by a shared `block_uuid`. Internal term; user-facing name TBD. |
| **Entry** | A single item in a playlist, referencing a catalog track by `source_track_uuid`. Has its own `uuid` (`playlist_entry_uuid`). The same track can appear as multiple entries. |
| **Fractional index** | A text-based position value (`position` column) that supports arbitrary insertion between existing values without rewriting other positions. |
| **Block position** | An integer (`block_position` column) giving the entry's order within its block (0-indexed). Deliberately not fractional indexing in M1. |
| **Operation** | An atomic, self-contained mutation applied to a playlist. Logged, attributed, idempotent, with deterministic result status. |
| **Revision** | A playlist's `current_revision` counter. Increments on every applied operation. Used for conflict detection, caching, and sync. |
| **Catalog** | The existing read-only indexed data (artists, shows, sources, tracks) in the existing Postgres database. |
| **User service** | The new API and `user_data` schema for user accounts, playlists, favorites, history, and settings. |
| **Outbox** | Realm-backed queue that persists user-data operations locally and replays them when connectivity is available. |
| **Short ID** | URL-friendly identifier for playlists (e.g., `Xk9mPq2v`), used in public/unlisted shareable links. |
| **Share token** | A revocable, role-scoped token for accessing unlisted playlists. |
| **Hydration** | The process of resolving catalog UUIDs in playlist entries to full track/show/artist metadata. |

## Appendix B: Open Questions

1. **User-facing term for "block"** — segment, group, flow, run? Needs user research / community input.
2. **Fractional indexing library** — which implementation to use for position strings? Several open-source options exist.
3. **Production database role names and recovery targets** — exact role names, RPO, RTO, and restore-drill cadence.
4. **ATProto login shape** — which ATProto / Bluesky auth flow to support when that work is scheduled, and whether it needs an `external_auth_grants` table in addition to `user_auth_methods`.
5. **Embeddable player design** — compact form factor for playlist embeds. Design work needed.
6. **Generated preview images** — what should the Open Graph image for a shared playlist look like? Needs design.
7. **Archive.org review API** — investigate whether archive.org's write API supports posting reviews, for future federation.
8. **Playback history retention tuning** — 2-year retention is a starting point. Monitor hypertable size and adjust.

## Appendix C: API Surface

### User Library API Endpoints

User-data endpoints live under `/api/v3/library`. Auth is required unless noted. API v3 wire JSON is `snake_case`; C# models keep normal C# naming conventions only when the user service serializer or explicit JSON attributes guarantee snake-case output. Add DTO serialization tests for every new endpoint family.

Public web pages and embeds stay outside the API namespace:

- `/playlist/{shortId}` — public/share landing page
- `/embed/playlist/{shortId}` — embeddable player

**Auth:**
- `POST /api/v3/library/auth/authorize` — initiate auth flow, returns auth URL with state + PKCE challenge (no auth required)
- `POST /api/v3/library/auth/callback/{provider}` — OAuth callback (Apple, Google; no auth required)
- `POST /api/v3/library/auth/token` — exchange auth code + PKCE verifier for token pair (no auth required)
- `POST /api/v3/library/auth/refresh` — exchange refresh token for new token pair (refresh token required)
- `POST /api/v3/library/auth/logout` — revoke refresh token / session (auth or refresh token required)

**Users:**
- `GET /api/v3/library/users/me` — current user profile
- `PATCH /api/v3/library/users/me` — update display name
- `GET /api/v3/library/users/me/sessions` — list active sessions
- `DELETE /api/v3/library/users/me/sessions/{sessionUuid}` — revoke a session
- `GET /api/v3/library/users/check-username/{username}` — availability check (no auth required)
- `POST /api/v3/library/users/me/auth-methods` — link additional auth method (recent reauth required)
- `DELETE /api/v3/library/users/me/auth-methods/{authMethodUuid}` — unlink auth method (recent reauth required; cannot remove last method)
- `POST /api/v3/library/users/me/export` — request data export (recent reauth required)
- `DELETE /api/v3/library/users/me` — delete account (recent reauth required)

**Sync:**
- `GET /api/v3/library/sync?cursor={cursor}` — incremental pull for user-data changes and tombstones. Returns `{ changes, tombstones, next_cursor }`.

**Playlists:**
- `GET /api/v3/library/playlists` — list user's playlists (owned, collaborating, following)
- `POST /api/v3/library/playlists` — create playlist
- `GET /api/v3/library/playlists/{playlistUuidOrShortId}` — get playlist with entries. Public playlists and validated share-token access do not require user auth. Supports `?hydrate=true` for web (returns track/show/artist metadata from catalog read replica). Default `?hydrate=false` returns raw UUIDs for mobile.
- `GET /api/v3/library/playlists/{playlistUuid}/viewer-state` — authenticated viewer's relationship to playlist (`is_following`, `is_collaborator`, `can_edit`). Never cached publicly.
- `POST /api/v3/library/playlists/{playlistUuid}/operations` — apply a single operation. Returns `{ result_revision, result_status, playlist }`.
- `POST /api/v3/library/playlists/{playlistUuid}/operations/batch` — apply batch operations (offline sync). Returns per-operation results.
- `GET /api/v3/library/playlists/{playlistUuid}/log` — get edit log (owner/editors only)
- `DELETE /api/v3/library/playlists/{playlistUuid}` — archive (soft delete, owner only)
- `POST /api/v3/library/playlists/{playlistUuid}/share-tokens` — create share token (owner only). If playlist is `private`, this atomically sets `visibility` to `unlisted`.
- `DELETE /api/v3/library/playlists/{playlistUuid}/share-tokens/{shareTokenUuid}` — revoke share token
- `POST /api/v3/library/playlists/{playlistUuidOrShortId}/share-tokens/exchange` — exchange a URL share token for web/mobile established access. Mobile clients send `{ "token": "...", "device_id": "...", "platform": "ios" }`; signed-out viewer tokens return a short-lived mobile grant sent on later reads as `X-Relisten-Mobile-Grant` plus `X-Relisten-Device-Id`. Editor tokens require sign-in and convert to durable collaborator/editor access.
- `POST /api/v3/library/playlists/{playlistUuid}/follow` — follow a playlist
- `DELETE /api/v3/library/playlists/{playlistUuid}/follow` — unfollow
- `POST /api/v3/library/playlists/{playlistUuid}/clone` — clone to own library

Authenticated playlist writes are UUID-first. If a user arrives through `/playlist/{shortId}`, the client resolves the playlist once, then uses `playlistUuid` for write endpoints.

**Favorites:**
- `GET /api/v3/library/favorites` — list all favorites
- `PUT /api/v3/library/favorites/{entityType}/{entityUuid}` — add favorite
- `DELETE /api/v3/library/favorites/{entityType}/{entityUuid}` — remove favorite
- `POST /api/v3/library/favorites/batch` — batch sync favorites (offline)

**History:**
- `POST /api/v3/library/history/batch` — batch upload playback history
- `GET /api/v3/library/history/recent` — paginated recent history (cross-device)
- `GET /api/v3/library/history/stats` — aggregated stats (future)

**Settings:**
- `GET /api/v3/library/settings` — get synced settings
- `PUT /api/v3/library/settings` — update synced settings

**Invitations:**
- `GET /api/v3/library/invitations` — list pending invitations
- `POST /api/v3/library/invitations/{playlistUuid}/accept` — accept
- `POST /api/v3/library/invitations/{playlistUuid}/decline` — decline
