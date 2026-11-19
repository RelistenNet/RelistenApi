# Relisten MCP server

> Status: PR 1 and PR 2 implemented locally. PR 3 is planned. Production work
> waits for owner approval of each step below. The
> [server roadmap](2026-11-18-server-roadmap.md) places this work among the
> other planned server work.

## Dependencies

- **Account tools launch only after [account deletion](2026-11-18-account-deletion.md)
  ships.** Its purge deletes OpenIddict tokens and authorizations by subject, which
  removes ChatGPT access. Whatever table records MCP consent grants must also
  cascade from the user, and the deletion schema test must cover it.
- **PR 2 waits for guide content** (section 2 of the
  [v4 playlist plan](2026-11-06-v4-playlists.md)), so `create_playlist` and
  `edit_playlist` can write the notes that explain each pick.
- **Connected clients and Delete account share one account settings screen.**
  Design it once.
- **Once sharing ships, `my_library` returns followed playlists too.** Tools tell
  owned and followed playlists apart by `access`, and only edit owned ones.

## Purpose

Give LLM clients — ChatGPT first — the catalog facts and account access they need
to help a person build a playlist they would not have found alone. The server
exposes evidence and lets the client and the person choose. It does not rank
"the best" versions, because a shared ranking makes every client's playlist the
same and feeds its own popularity.

This plan grew out of the Grateful Dead "Jam Vehicles" playlist that Codex built
from direct production replica queries. Every query in that session maps to an
existing v4 endpoint or to one of the backend additions below.

## Design rules

1. No "best" or quality-ranking tool. List tools default to date order. Other
   orders are seeded random, duration, rating, and review count. Plays are plain
   evidence fields, never the default sort.
2. Return facts the model can reason over: setlists with segue marks, durations,
   soundboard flag, ratings, review counts, and review excerpts.
3. Make variety cheap. List tools accept `exclude` (shows or tracks already
   chosen), `seed`, and, when signed in, `not_in_my_library`.
4. Keep responses small. Paginate with cursors, return compact rows, and include
   the relisten.net link and the playable track IDs on every row.

## Tools

### Catalog (anonymous)

| Tool | Returns | Backend |
| --- | --- | --- |
| `find(text, artist?)` | Artists, shows, songs, venues, and tours for free text such as "Cornell 77" or "scarlet fire" | Existing: v4 search and `resolve` |
| `browse(artist, by=years\|tours\|venues\|eras)` | Catalog grouping with show counts | Existing |
| `get_song(artist, song)` | Aliases, matched show and track counts, duration statistics | Existing: `songs/by-slug`, `SongShowTimingsResource` |
| `list_performances(artist, song?, years?, tour?, venue?, min_minutes?, max_minutes?, soundboard_only?, segued_from?, segued_into?, order, seed, exclude, limit, cursor)` | One row per show and song: date, venue, longest version, source count, soundboard available, rating, review count, `plays_1y`, `days_with_plays`, the neighbouring songs with segue marks, track IDs, link | **New endpoint** |
| `get_show(artist, date \| show_id)` | Sources with soundboard flag, rating, review count, and setlist with segues per source | Existing |
| `get_source(source_id)` | One recording of a show: tracks, durations, song parts, links | Existing |
| `get_tracks(ids[])` | Up to 500 tracks with song parts, for checking a draft | Existing: `POST /api/v4/catalog/resolve` |
| `search_reviews(artist, query? \| song?, show?, years?, limit)` | Review excerpts with rating, source, show, link | **New endpoint** |
| `sample_shows(artist, n, seed, years?, tour?, venue?, spread=year\|era\|tour)` | Seeded sample spread across years, eras, or tours | **New option**; can start inside the MCP server by sampling year listings |

### Account (OAuth)

| Tool | Scope | Does | Backend |
| --- | --- | --- | --- |
| `whoami()` | `user.read` | Shows which Relisten account is connected; marked `_meta["openai/profile"]: true` | Existing account profile |
| `my_library()` | `library.read` | Favorites, owned playlist summaries and followed playlist heads, each marked with `access`, to learn taste and skip repeats | Existing: `GET /v1/library/snapshot` |
| `read_playlist(id)` | `library.read` | Ordered segments and tracks. An owned playlist reads the owner snapshot. A followed one reads the public snapshot by its `public_code`, through the same visibility rule as any public read | Existing: `GET /v1/playlists/{id}/snapshot` and the public snapshot |
| `create_playlist(name, description, segments: [{description?, entries: [{track_id?, description?}]}])` | `library.write` | One segment per segued run. Track descriptions explain each pick from review evidence; an entry with no `track_id` is a text block. | Existing: `POST /v1/playlists`, extended by guide content |
| `edit_playlist(id, changes)` | `library.write` | Add segment, move, remove, rename, and add or change notes and text blocks. Checks ownership itself, so a later editor role on the batch service doesn't widen it | Existing: `operations:batch` |

**Retries.** Unlike mobile, the model can't keep IDs between calls. Both write
tools take an optional `idempotency_key`. The server derives the command,
operation and created IDs with HMAC using the account, target and caller's key.
An identical call replays its receipts. A keyed edit stores a fingerprint of
the complete ordered batch and `expected_revision` in its operation receipts.
Reusing the key with different arguments returns a conflict. Without a key,
the server generates fresh UUIDv7 IDs and a retried call is a new write.

**Stale edits.** `read_playlist` returns the playlist's `revision`.
`edit_playlist` takes an optional `expected_revision` and refuses the edit when
the playlist has moved on, unless the call is a replay of its own earlier
success. The playlist service treats `base_revision` as a diagnostic, so the
tool passes the expected revision to the write service, which compares it under
the playlist lock after replaying any stored result. Two edits from the same
read can't both apply.

**Untrusted text.** Review excerpts, playlist names, descriptions and notes are
written by listeners. A copied public playlist keeps its author's text. Tools
name every field they return and place this text under
`untrusted_listener_text` with a notice that it is content and not
instructions. The tool descriptions repeat the warning, because the same
session may hold `library.write`.

Tool annotations: catalog and read tools set `readOnlyHint: true`.
`create_playlist` sets `readOnlyHint: false`. `edit_playlist` sets
`destructiveHint: true` because it can remove entries. Every tool sets
`openWorldHint: false`: it touches only the Relisten catalog and the signed-in
account.

### Resource and prompt

- `relisten://caveats`: plays are track starts, not listeners; anniversaries and
  default sources inflate plays; segue marks are sometimes missing; some tracks
  have no song data.
- `build-playlist` prompt: ask about taste and limits first, cover many songs
  and eras, explain each pick from review evidence, verify durations and
  streams, and check the library to avoid repeats.

### Not in v1

Popularity rankings, trending, and per-track "lift". Lift stays an internal
signal for a future show-page "fan favorite" badge.

## Backend work

### 1. Review search

`GET /api/v4/artists/{id}/reviews/search?q=&song_id=&show_id=&years=&limit=`

- Require an artist. Grateful Dead is 75,112 reviews (34 MB of text); the
  unindexed artist-wide scan takes about 1.0 s and a one-show scan about 107 ms.
- `song_id` builds the pattern from `norm.song_aliases`. The route accepts no
  caller-supplied regular expression.
- Every query runs with `SET LOCAL statement_timeout = '2s'`. A timeout returns
  a "narrow the search" error, not a 500.
- The limits live on the catalog route itself, because the route is public
  even with MCP disabled. Rate-limit it per client IP, separately from cheap
  catalog reads, starting at 10 searches a minute.
- Migration 22 installs `pg_trgm` and creates a GIN index on the same
  title-and-review expression the search uses. Literal search escapes LIKE
  wildcards. Regex search uses the indexable match operator. Compute excerpt
  offsets after selecting the page. Short literals and regex patterns with no
  extractable trigrams can still require a broad scan.
- Before deploying the catalog build in production, run
  `CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public`, then run
  [the concurrent index statement](../../../RelistenApi/Migrations/Sql/022_review_search_index.sql)
  on the writer as a standalone statement and analyze `public.source_reviews`.
  Check the build on the writer with:

  ```sql
  SELECT indisready, indisvalid, pg_get_indexdef(indexrelid)
  FROM pg_index
  WHERE indexrelid = to_regclass('public.source_reviews_search_trgm_idx');
  ```

  Both flags should be true, and the definition should match the statement above.
  A failed concurrent build can leave an invalid index. Drop that invalid
  index concurrently before retrying. The migration reuses the expected prebuilt
  index and builds it concurrently in a fresh database.
- Before widening access, measure artist-wide searches at the configured
  limits and expected concurrency.
- Return excerpts ranked by rating, then length.

### 2. Monthly play rollup and the `1y` window

**Monthly rollup.** Add `source_track_plays_monthly`, a continuous aggregate on
`source_track_plays_daily` (TimescaleDB 2.28 supports hierarchical aggregates
and monthly buckets). Keep the daily grain's keys (track, source, show, artist)
and store `plays`, `total_track_seconds`, and `days_with_plays` (the count of
daily rows). That count is per track. A source's `days_with_plays` can't be
summed from its tracks: two tracks played once each may share a date or not. Add
a source × day rollup and count its days for source-level `days_with_plays`. On Grateful Dead's last year, track × month is 1.68M rows against
7.05M for track × day.

**Exact 365-day window.** Sum whole months from the monthly rollup and the
partial months at both edges from the daily rollup. Month-only windows run
365–396 days and double-count the leading days and their anniversaries.

**`1y` popularity window.** Add `1y` to `PopularityWindowsResource` with the
existing `plays`, `hours`, and `hot_score` (`Math.Sqrt(plays)`). Add
`plays_1y`, `hours_1y`, and `hot_score_1y` to `show_popularity`,
`year_popularity`, and `artist_popularity`, and to the upsert column list in
`PopularityStore.Replace`. Give the monthly aggregate its own Timescale refresh
policy, run daily. Both the full and on-demand popularity refreshes write rows through the same
upsert, which replaces the whole row and turns any counter it didn't compute into
zero. So the `1y` values can't be computed in one path and merely carried in
the other. Start by measuring the yearly calculation inside the existing refresh
path. If it fits, every refresh computes `1y` and there stays one writer. If it
doesn't, give the `1y` columns their own writer and schedule, and remove them
from the shared upsert's column list. Either way, a show with only yearly plays
keeps its `plays_1y` through an ordinary refresh. Measure the refresh cost first: today the global 365-day show sum over the daily
rollup takes 34 s cold and 3.5 s warm.

**Population.** Change the maps' `WHERE p30.plays_30d IS NOT NULL` to "has plays
in the last 365 days". 105,224 of 149,601 shows played in the last year had no
plays in the last 30 days.

- Keep `ApplyMomentumScores` limited to rows with 30-day activity, so percentile
  ranks for active shows do not shift. Store null momentum for the other rows,
  so rankings, which filter on `momentum_score IS NOT NULL`, still leave them
  out. `trend_ratio` stays `NOT NULL` and is stored as 0, as today.
- Change metadata reads (`CatalogPopularityMetadataReader`) to return
  `popularity` when any window has plays.
- **Public responses keep sending numbers.** Mobile writes `momentumScore` and
  `trendRatio` into required Realm `double` fields
  (`relisten/realm/models/popularity.ts`), so a null would fail the catalog page
  write. The response sends 0 for both on rows without 30-day activity. Making
  them nullable on the wire waits for a mobile build with optional fields.

**Track and source totals.** Read per-track and per-source `plays_1y` and
`days_with_plays` from the same rollups for `list_performances`. All-time totals
become cheap now that the daily backfill reaches 2018-10-19 (about 97 months).

### 3. `list_performances`

One row per show and song, built on the server. For date order, walk shows in
descending date and ID batches, then match recordings only for those shows.
The date cursor includes the last show and song so a page can resume inside a
show. Other sorts need to compare the full filtered set. The Catalog API runs
at most four performance queries at once per instance, gives each SQL statement
two seconds, and gives the complete read three seconds. On timeout, narrow by
song, year, or tour.

`track_ids`, the source counts and ratings, and the play totals summarize the
eligible recordings. `representative` identifies the longest playable file
with its track, source, link, neighbors and source play totals. Duration filters
and sorting use whole-file lengths, including medleys. Dark Star alone has 1,649 tracks,
so client-side assembly from `songs/{id}?include_tracks=all` plus source reads is
too many calls. Include neighbouring songs with segue marks so clients can find
sequences such as Help > Slip > Franklin's.

### 4. Sampling (optional)

`spread` and `seed` parameters on show listings. The MCP server can do this
itself from year listings until the endpoint exists.

## Authentication

### Shape

    ChatGPT ──OAuth 2.1 + PKCE──▶ auth.relisten.net ┐
       │                                             │ RelistenUserService
       └──streamable HTTP + Bearer──▶ mcp.relisten.net ┘ (one deployment)
                                         ├─ catalog tools → api.relisten.net
                                         └─ account tools → library services, in process

    MCP tokens carry aud = https://mcp.relisten.net/mcp and are rejected by
    accounts.relisten.net/v1; accounts tokens are rejected by /mcp.

**Host the MCP endpoint in `RelistenUserService`, not as a separate server.**
The official C# SDK (`ModelContextProtocol.AspNetCore`) is a library: the user
service maps it with `MapMcp("/mcp")` beside its `/v1` routes, using the
streamable HTTP transport that ChatGPT requires. There is one build and one
deployment.

- The user service's access tokens are encrypted and validated by its local
  OpenIddict server, so the MCP endpoint validates them without sharing keys or
  adding introspection.
- Account tools call the library and playlist services in process. A separate
  MCP service would have to forward the user's token to the accounts API, which
  the MCP spec forbids ("token passthrough"), or add token exchange.
- Catalog tools call `api.relisten.net` over HTTPS. The catalog has its own
  database. Review searches for active accounts carry a service credential for
  admission to the authenticated search budget. User tokens and IDs stay in the
  User Service.

Split the endpoint into its own deployment only if MCP traffic needs to scale
separately. The URL stays the same, so connected clients do not reconnect.

### Host

`https://mcp.relisten.net/mcp` is the canonical resource URI: the connector
address users add in ChatGPT and the audience in every MCP token. Never change
it; a new URL forces every user to reconnect.

It is a third public host for the user service, beside `auth.relisten.net`
(OpenID Connect) and `accounts.relisten.net` (the `/v1` bearer-token API). The
[identity architecture](../../architecture/2026-07-18-relisten-identity-user-data-and-sonos-architecture.md)
gives each host one protocol and one cookie or token policy, and keeps
`api.relisten.net` as the anonymous catalog service. An authenticated endpoint
at `api.relisten.net/mcp` would break that boundary and share a host whose
catalog responses Cloudflare caches publicly (`max-age=1800`).
`accounts.relisten.net/mcp` would mix two token audiences on one host.

Changes for the new host:

1. **`HostBoundaryMiddleware`.** Serve `/mcp`,
   `/.well-known/oauth-protected-resource/mcp`, and the root
   `/.well-known/oauth-protected-resource` only on the MCP host, and return 404
   for everything else there, including `/v1/*`. Return 404 for `/mcp` on the
   auth and accounts hosts. The middleware sends all of `/.well-known` to the
   auth host today, so the protected resource metadata paths need an explicit
   exception ahead of that rule.
2. **`AccountsOptions` and `AccountsRuntimeConfiguration`.** Add an `McpHost`
   option and the MCP resource URI. Extend the production check that pins
   `auth.relisten.net` and `accounts.relisten.net` to pin `mcp.relisten.net`.
3. **Token audiences.** `NativeSessionAuthorizationHandler` already rejects
   tokens without the `https://accounts.relisten.net` audience, so MCP tokens
   cannot call `/v1`. Add the mirror-image policy on `/mcp`: require the MCP
   resource URI as audience, so an accounts token cannot call MCP tools.
4. **Ingress and Cloudflare.** Route `mcp.relisten.net` to the user service.
   Bypass caching, allow long-lived streamed responses, and apply MCP rate
   limits on this host without changing catalog rules.
5. **Tests.** Extend `TestHostBoundaryMiddleware` and the authentication
   configuration tests for the new host, the metadata exception, and both
   audience rejections.

### Authorization server changes (auth.relisten.net)

The live discovery document already meets several requirements: `authorization_code`
and `refresh_token` grants, `code_challenge_methods_supported: ["S256"]`, PKCE
required, `authorization_response_iss_parameter_supported: true`, rotating
reference refresh tokens, and 10-minute access tokens. Because `iss` is
returned, ChatGPT uses its stable redirect
`https://chatgpt.com/connector_platform_oauth_redirect`.

Changes:

1. **Client registration by allowlisted client ID metadata documents.** ChatGPT
   prefers CIMD and uses `https://chatgpt.com/oauth/client.json` as its
   `client_id`. Pre-create an OpenIddict application whose client ID is that URL:
   a public client, explicit consent, the redirect URI above, and permissions for
   `openid`, `profile`, `offline_access`, `user.read`, `library.read`, and
   `library.write`. Not `account.manage`. Advertise
   `client_id_metadata_document_supported: true` in discovery. Later, fetch and
   validate unknown CIMD URLs from an allowlist of hosts, with SSRF protection,
   instead of pre-creating each client. Skip dynamic client registration: it is
   deprecated in the 2026-07-28 MCP spec, and CIMD covers ChatGPT.
2. **Public-client token authentication.** Advertise `none` in
   `token_endpoint_auth_methods_supported`; the live list has only
   `client_secret_post`, `private_key_jwt`, and `client_secret_basic`.
   When the ChatGPT client is enabled, discovery omits `private_key_jwt`.
   ChatGPT prefers it if advertised, and the pinned public client has no keys
   registered. Adding ChatGPT's published JWKS is a later hardening step.
3. **Resource indicators (RFC 8707).** ChatGPT sends
   `resource=https://mcp.relisten.net/mcp` on authorization and token requests.
   `NativePrincipalFactory` stamps every token with the fixed
   `Accounts:Audience` (`https://accounts.relisten.net`). For MCP clients, accept
   only the MCP resource, reject any other value with `invalid_target`, and set it
   as the token's resource so the MCP endpoint can require it and the accounts API
   rejects it.
4. **Consent page.** Existing clients use implicit consent. Third-party MCP
   clients need an explicit consent screen that shows the client name, the
   redirect host (required by the MCP spec), and the requested scopes in plain
   language. Show it on every authorization, including after an earlier grant,
   and issue a code only from the form's anti-forgery-protected POST. Every
   ChatGPT user shares one redirect URI, so a silent code would let an attacker
   link a signed-in listener's Relisten account to the attacker's ChatGPT.
   `prompt=none` returns `consent_required`.
5. **Revocation.** List and revoke connected MCP clients in account settings.
   The list reports `connected` for an active session and `expired` when the
   session can no longer refresh; the app must sign in and be approved again.
   Disconnect revokes either state.

### MCP endpoint changes

1. Serve protected resource metadata at
   `/.well-known/oauth-protected-resource/mcp` and the root fallback:
   `resource: "https://mcp.relisten.net/mcp"`,
   `authorization_servers: ["https://auth.relisten.net/"]`,
   `scopes_supported: ["user.read", "library.read", "library.write",
   "offline_access"]`, the scopes the registered client may be granted. A client
   may still request `library.write` by step-up when a write tool is first
   called.
2. **Mixed authentication.** Accept unauthenticated `initialize` and catalog calls.
   Declare per-tool `securitySchemes` in `_meta`: `{ "type": "noauth" }` on
   catalog tools, `{ "type": "oauth2", "scopes": [...] }` on account tools.
   The two catalog tools with an optional library filter and review search
   advertise both schemes when account access is available. When
   an account tool runs without a valid token, return a tool error whose
   `_meta["mcp/www_authenticate"]` carries `error` and `error_description`.
   ChatGPT needs both the declaration and this error to show its linking UI.
   Missing scopes return `insufficient_scope` with every scope the call needs.
3. Validate every request: signature, `iss`, the MCP resource as audience,
   `exp`, and scopes. Never trust the model to gate access.
4. A valid signature isn't enough after a disconnect or account deletion. A
   token's ten minutes don't end when the grant does. On every account tool call
   and every refresh, also require the user to exist and be `active`, its
   `security_version` to match the token, and the OpenIddict authorization to be
   valid, as `NativeSessionAuthorizationHandler` does for `/v1`. Test with a
   token issued before a disconnect, and with one issued before an account purge.
5. Rate limit anonymous calls per client IP (60 a minute) and signed-in calls
   per account (300 a minute), and review search separately from cheap catalog
   reads. The account limit is finite because anyone can sign up and mint a
   token through the public client. The limiter is in process memory, so the
   effective ceiling is the limit times the replica count. Cap `/mcp` request
   bodies at 2 MB, as the playlist routes do. Optionally verify ChatGPT's OpenAI-managed mTLS client
   certificate (SAN `mtls.prod.connectors.openai.com`); it identifies ChatGPT
   but does not replace OAuth.
6. Keep tokens and tool results out of logs.

## Delivery

Two RelistenApi PRs and one relisten-flux PR. Verify each prerequisite in
production before deploying the build that depends on it.

### PR 1: monthly rollup and `1y` window (RelistenApi)

Everything in "Monthly play rollup and the `1y` window": the
`source_track_plays_monthly` migration, the `1y` window and columns, the wider
population with momentum limited to 30-day activity, the metadata gate change,
and the popularity docs. The daily backfill reached 2018-10-19 on 2026-11-18, so it
can start now.
Measure the `1y` refresh cost against production data before merging. It ships
separately because it migrates production play history and changes a public
response contract, and its rollback is independent of the MCP work.

### PR 2: MCP server (RelistenApi)

Everything else:

- Catalog API: review search and `list_performances`. Deploy PR 1 and backfill
  the rollups before serving the performance query.
- User service: the MCP endpoint and all tools, the `mcp.relisten.net` host
  boundary and configuration, protected resource metadata, the authorization
  server changes (MCP audience, ChatGPT client, discovery additions, consent
  page, connected-client revocation), and tests.
- Treat a missing `Accounts:McpHost` as "MCP disabled": no `/mcp` route, no MCP
  host, and no ChatGPT client. The service then deploys safely before the flux
  change, and the production host check in `AccountsRuntimeConfiguration` only
  pins the MCP host when it is configured.

### PR 3: deployment (relisten-flux)

In `clusters/relisten3-k3s/apps/relisten-user-service.yaml`:

- Add `mcp.relisten.net` to the TLS hosts and rules of
  `relisten-user-service-production-ingress` and to
  `relisten-user-service-http-redirect-ingress`.
- On the `relisten-user-service` deployment, add `mcp.relisten.net` to
  `AllowedHosts` and set `Accounts__McpHost`, the MCP resource URI, and the
  catalog base URL for the catalog tools.
- Configure `Catalog__ReviewSearchTrustedProxyNetworks__0` and any additional
  trusted proxy CIDRs for the catalog API. Review search uses the connection
  address until these are configured. Verify that external callers receive
  independent limits through the real ingress chain, including Cloudflare.
- Before configuring the MCP hostname, provision the same high-entropy
  `MCP_REVIEW_SEARCH_KEY` on Catalog and the User Service. Both manifests read
  the required Secret. The User Service gives each active
  connected account 30 searches per minute. It sends a service credential only
  on those review calls. Catalog allows 120 searches per minute per instance
  for that traffic, independently of the anonymous limit. Direct anonymous
  callers keep their 10-per-IP catalog budget. Anonymous MCP calls share the
  budget for the User Service's observed egress IP.
  Verify both paths and measure before widening access.
- Catalog tools can reach `https://api.relisten.net` through the existing TCP
  443 egress rule. In-cluster routing avoids the Cloudflare round trip and
  needs matching egress and ingress rules. The catalog rate limiter applies
  over either path.
- Provision a separate, high-entropy `MCP_WRITE_ID_KEY` in the
  `default/relisten-mcp-write-id` Secret on the User Service. Keep it the same
  across replicas and deployments so a retry can find its receipts. Changing
  this key makes old retries look like new writes.

Outside flux: add the `mcp.relisten.net` DNS record in Cloudflare, then bypass
caching, allow long streamed responses, and add MCP rate limits for that host.

### Order and checks

1. Provision the shared review-search Secret and the User Service write-ID
   Secret before applying manifests that reference them.
2. Follow the [yearly popularity rollout](../../popularity/README.md#yearly-window-rollout).
   Deploy the Catalog compatibility build before migrating, then backfill and
   verify both rollups before deploying the final Catalog image. Roll the
   Catalog API with the review-search Secret before the User Service.
3. Test the User Service locally with the MCP Inspector and ChatGPT developer
   mode against a temporary public HTTPS URL. Ship account deletion and guide
   content before the MCP host goes live. Add the MCP ingress and the
   Cloudflare DNS, cache, and rate-limit rules after verifying account deletion.
   The configured hostname exposes anonymous and signed-in tools together.
4. In ChatGPT developer mode, call an anonymous catalog tool, then test
   sign-in, consent, a library read, step-up to `library.write`, and playlist
   creation. Confirm `/v1` rejects the MCP token, `/mcp` rejects an accounts
   token, and `/mcp` returns 404 on the auth and accounts hosts. Submit the app
   for review once the checks pass.

### Local Codex smoke test

Relisten registers Codex separately from ChatGPT and pins its callback to
`http://127.0.0.1:51057/callback`. To sign in with a command-only configuration:

```sh
codex mcp login relisten_prod_smoke \
  -c 'mcp_servers.relisten_prod_smoke.url="https://mcp.relisten.net/mcp"' \
  -c 'mcp_oauth_callback_port=51057' \
  --scopes user.read,library.read,library.write
```

Check the account using `whoami` before writing. To roll back Codex support,
use a User Service image that rejects unallowlisted client metadata URLs.
The Codex registration persists across image rolls. Disable that registration
before reverting to any image built before the allowlist guard.

## PR 2 verification

- OpenIddict 7.6 parses resource indicators and checks configured resources and
  client permissions. Relisten requires the exact MCP resource for the ChatGPT
  client and sets it on the principal so OpenIddict issues the MCP audience.
- A local HTTP probe confirmed that OpenIddict publishes CIMD discovery and
  `none`. The MCP SDK 2.4.0 probe confirmed Streamable HTTP, tool metadata,
  and tool-level OAuth challenges. Relisten serves protected-resource
  metadata on its two explicit routes.
- The User Service stores consent in OpenIddict authorizations. Disconnect
  revokes the grants and native sessions. Account purge removes the same
  authorization rows by subject.
- Measure the yearly refresh on the primary and the cost of artist-wide
  catalog searches before launch. Test the real ChatGPT connection and the
  ingress configuration in PR 3. Optional sampling uses existing show listings.

## References

- [MCP authorization, 2026-07-28](https://modelcontextprotocol.io/specification/latest/basic/authorization)
- [ChatGPT Apps SDK: authentication](https://developers.openai.com/apps-sdk/build/auth)
- [ChatGPT Apps SDK: MCP server](https://developers.openai.com/apps-sdk/build/mcp-server)
- [Private playlist contracts](2026-11-16-v4-playlist-contracts.md)
- [Account ownership and authentication](../../api/accounts.md)
- [Popularity storage](../../popularity/README.md)
