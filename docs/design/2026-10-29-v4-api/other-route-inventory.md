# V4 coverage: discovery, playback, operations, and accounts

This inventory complements [catalog routes](catalog-route-inventory.md). Current
routes and behavior are observed at API commit `3fb0e0f`; v4 routes are proposed
contracts, not implemented endpoints. Every HTTP action in the assigned
controllers has a disposition below. Keeping an existing route does not mean
silently deleting its functionality or treating it as a missing catalog API.

There are **39 current method/route combinations** in the six API controllers
covered here: popularity 9, recent 3, live 2, import 6, admin 17, health 2.
Account/authentication routes and middleware route families are listed separately.
HEAD/OPTIONS framework behavior is not counted as separate application actions.

For v4 catalog responses, all resource IDs follow the approved prefix table.
Every track object includes one `normalization` object. Playback titles remain
imported titles; canonical venue information overlays imported venue records.
Counts, limits, timestamps, ranking scores, cursors, dates, external upstream
identifiers, and telemetry client IDs do not become catalog TypeIDs merely
because their names contain `id`.

## Popularity and trending — nine routes

Source: [PopularityController.cs](../../../RelistenApi/Controllers/PopularityController.cs),
[response models](../../../RelistenApi/Models/Api/PopularityDtos.cs), and
[sort window](../../../RelistenApi/Services/Popularity/PopularityService.cs).

| Current method and route | Proposed route | Contract to preserve or change |
| --- | --- | --- |
| GET `/api/v3/popular/artists` | GET `/api/v4/popular/artists` | Preserve `limit` (default 50), ranking/play metrics; encode `artist_id`. Source: controller line 30. |
| GET `/api/v3/trending/artists` | GET `/api/v4/trending/artists` | Preserve `limit` (50), ranking/trend metrics; encode artist identities. Line 37. |
| GET `/api/v3/popular/shows` | GET `/api/v4/popular/shows` | Preserve `limit` (25, clamped), `window` (30-day default; 48-hour/7-day/30-day choices); use v4 show projections. Line 44. |
| GET `/api/v3/trending/shows` | GET `/api/v4/trending/shows` | Same limit/window contract; preserve trending selection, use v4 shows. Line 61. |
| GET `/api/v3/artists/{artistIdOrSlug}/shows/popular-trending` | GET `/api/v4/artists/{artistId}/shows/popular-trending` | Return both popular and trending groups; preserve limit/window and artist context. Line 78. |
| GET `/api/v3/shows/popular-trending` | GET `/api/v4/shows/popular-trending` | Replace `artistUuids[]` with typed `artist_ids[]`; preserve limit (10 per group, clamped), window, and grouped artists. Empty artist input does not mean all artists. Line 99. |
| GET `/api/v3/shows/momentum` | GET `/api/v4/shows/momentum` | Typed `artist_ids[]`, limit (25, clamped); preserve momentum selection. Empty artist input does not expand to all artists. Line 121. |
| GET `/api/v3/popular/years` | GET `/api/v4/popular/years` | Preserve `limit` (50), ranking metrics and human-readable year; encode artist/year resource IDs. Line 145. |
| GET `/api/v3/trending/years` | GET `/api/v4/trending/years` | Preserve limit, year label, trend metrics; encode artist/year IDs. Line 152. |

Ranking algorithms do not need to change merely to emit typed IDs. Shared v4
projection code must apply normalized venue metadata wherever these routes
return shows. Any embedded tracks obey the same normalization contract as track
detail. Legacy artist slug lookup can remain an explicitly supported alias;
the canonical response identity is the typed artist ID.

## Recently added — three current routes, two canonical v4 routes

Source: [RecentController.cs](../../../RelistenApi/Controllers/RecentController.cs).

| Current method and route | Proposed route | Contract to preserve or change |
| --- | --- | --- |
| GET `/api/v2/artists/{artistIdOrSlug}/shows/recently-added` | GET `/api/v4/artists/{artistId}/shows/recently-added` | Preserve `limit` (20, capped at 200), optional `previousDays`, `sort_by=created_at\|updated_at`; v4 show/artist projections. Line 36. |
| GET `/api/v2/artists/shows/recently-added` (obsolete alias) | GET `/api/v4/shows/recently-added` | Same global recently-added capability. The legacy alias defaults to limit 25 and lacks `previousDays`; its callers can send `limit=25` explicitly. No need for a second v4 alias. Line 67. |
| GET `/api/v2/shows/recently-added` | GET `/api/v4/shows/recently-added` | Preserve `limit` (20, capped at 200), optional `previousDays`, and `sort_by`; v4 show/artist projections. Line 86. |

These routes sort catalog addition/update times, not performance dates. Preserve
that distinction from recently performed/on-date show routes in the catalog
inventory. Invalid sort values currently fall back to creation time; whether
v4 rejects invalid inputs instead is an explicit contract decision, not a loss
of the supported sort choices.

## Playback reporting and public listening history — two routes

Source: [LiveController.cs](../../../RelistenApi/Controllers/LiveController.cs).

| Current method and route | Proposed route | Contract to preserve or change |
| --- | --- | --- |
| POST `/api/v2/live/play` | POST `/api/v4/live/play` | Implemented JSON `{ track_id, app_type, client_uuid? }`; canonical `track_` identity, supported app enum, optional telemetry UUID. Returns `{ sequence, played_at, track_id, app_type }`; invalid input 422, unavailable track 404. Same telemetry table, no account-history write. Legacy source: line 41. |
| GET `/api/v2/live/history` | GET `/api/v4/live/history` | Implemented one-hour recent window, `after_sequence` cursor, `limit` (20, capped at 2000), ascending sequence order. Optional repeated `artist_ids` selects any requested artist before the limit; omitted means global. Each event includes a v4 track with normalization and show summary; track IDs identify source/set/artist without repeating their resources. Caller telemetry UUIDs are omitted. Legacy source: line 99. |

`user_uuid` on the current report is a caller-supplied telemetry field. Do not
silently reinterpret it as authenticated account identity or favorite membership.
V4 must document its meaning and optionality separately from catalog prefixes.
The existing play record's generated integer ID may remain a cursor field;
it needs a public resource prefix only if the contract exposes it as an entity.

## Import control — six retained routes

Keep these operations at their current v2 paths and authorization. The separate
relisten-admin application talks directly to Postgres; it does not consume these
routes. Existing built-in admin/integration callers remain supported. No v4
aliases are required. Source: [ImportController.cs](../../../RelistenApi/Controllers/Import/ImportController.cs).

| Current method and route | Retained route | Contract to preserve or change |
| --- | --- | --- |
| POST `/api/v2/import/{idOrSlug}` | POST `/api/v2/import/{idOrSlug}` | Authenticated artist refresh; `deleteOldContent=false` default; queue workflow/job and report its handle. Destructive reimport conflicts while Temporal owns ingestion. Line 42. |
| POST `/api/v2/import/{idOrSlug}/debug` | POST `/api/v2/import/{idOrSlug}/debug` | Authenticated synchronous debug refresh; preserve delete flag and refusal while Temporal is enabled. Line 75. |
| POST `/api/v2/import/phishin` | POST `/api/v2/import/phishin` | Preserve `X-Relisten-Api-Key` admission and Phish-only provider refresh; return Temporal/Hangfire handle. Line 102. |
| POST `/api/v2/import/all-years-and-shows` | POST `/api/v2/import/all-years-and-shows` | Authenticated whole-catalog derived rebuild; return workflow/job handle. Line 137. |
| POST `/api/v2/import/{idOrSlug}/{showIdentifier}` | POST `/api/v2/import/{idOrSlug}/{showIdentifier}` | Authenticated single-item import, optional delete flag; under Temporal the second value is an Archive item identifier, not a Relisten show ID. Preserve external identifier and engine admission semantics. Line 149. |
| POST `/api/v2/import/{idOrSlug}/{showIdentifier}/debug` | POST `/api/v2/import/{idOrSlug}/{showIdentifier}/debug` | Authenticated synchronous item debug import; same external identifier meaning; reject while Temporal enabled. Line 193. |

Workflow IDs, Hangfire job IDs, and Archive identifiers retain their native
formats. This catalog work does not change ingestion engines or admission rules.

## Existing admin HTML/control routes — seventeen explicit dispositions

All remain at their existing paths: this is a separately authenticated operator
UI, not a versioned catalog DTO API. Artist writes and imports also retain their v2 paths; changing these HTML paths adds no mobile coverage.
Source: [AdminController.cs](../../../RelistenApi/Controllers/Admin/AdminController.cs).

| Method and retained route | Purpose and inputs | Source line |
| --- | --- | --- |
| GET `/relisten-admin/login` | Anonymous login form; optional local `returnUrl`. | 56 |
| POST `/relisten-admin/login` | Username/password/remember-me login; local return URL. | 67 |
| GET `/relisten-admin/` | Authenticated artist/upstream operator index. | 103 |
| GET `/relisten-admin/popularity` | Authenticated popularity cache status UI. | 116 |
| POST `/relisten-admin/popularity/refresh-artists-map` | Queue global artist popularity map. | 159 |
| POST `/relisten-admin/popularity/refresh-shows-map` | Queue show map for query `artistUuid`. Internal UI may keep raw UUID binding. | 170 |
| POST `/relisten-admin/popularity/refresh-years-map` | Queue year map for query `artistUuid`. | 187 |
| POST `/relisten-admin/popularity/refresh-popular-artists` | Queue popular artists; query `limit=50`. | 204 |
| POST `/relisten-admin/popularity/refresh-trending-artists` | Queue trending artists; `limit=50`. | 215 |
| POST `/relisten-admin/popularity/refresh-popular-shows` | Queue popular shows; `limit=50`. | 226 |
| POST `/relisten-admin/popularity/refresh-trending-shows` | Queue trending shows; `limit=50`. | 237 |
| POST `/relisten-admin/popularity/refresh-popular-years` | Queue popular years; `limit=50`. | 248 |
| POST `/relisten-admin/popularity/refresh-trending-years` | Queue trending years; `limit=50`. | 259 |
| POST `/relisten-admin/popularity/refresh-all` | Queue all global popularity caches; `limit=50`. | 270 |
| POST `/relisten-admin/index-archiveorg` | Queue artist discovery using selected ingestion engine. | 291 |
| POST `/relisten-admin/refresh-all-artists` | Queue whole-catalog refresh using selected engine. | 305 |
| POST `/relisten-admin/backfill-jerrygarcia-venues` | Queue provider venue repair; `artistSlug=grateful-dead`. | 317 |

## Account and authentication services

These paths are relative to the account/auth service, not the catalog API host.
Catalog `v4` does not turn OAuth endpoints into `/api/v4/connect/*`. The private
alpha library contract changes because it carries catalog targets; profile and
identity-session protocols retain their own versioning.

| Current method and service route | Proposed disposition | Inputs/behavior and source |
| --- | --- | --- |
| GET `/v1/library/snapshot` | GET `/v1/library/snapshot` — changed in place | Native typed favorite/target snapshot supporting global venues; account authorization and numeric library revision. [LibraryController](../../../RelistenUserService/Controllers/LibraryController.cs), line 20. |
| GET `/v1/library/changes` | GET `/v1/library/changes` — changed in place | Preserve opaque `after` cursor, page/has-more semantics, expired-cursor 410 and snapshot recovery; target kind/ID and owned change/favorite IDs become canonical typed strings. Same controller, line 25. |
| POST `/v1/library/favorite-mutations:batch` | POST `/v1/library/favorite-mutations:batch` — changed in place | Versioned typed targets; retain independent mutation/favorite UUIDs, idempotency, quota/conflict errors and 256-KiB request limit. Pre-typed alpha payload compatibility is not required. Same controller, line 46. |
| GET `/v1/me` | Retain `/v1/me` | Typed user_id/native_session_id profile; preserve username-version semantics and invalidate stale pre-typed client profile caches. [MeController](../../../RelistenUserService/Controllers/MeController.cs), line 19. |
| PATCH `/v1/me` | Retain `/v1/me` | Username command with command_ ID, contract version and version conflict/retry errors. Same controller, line 28. |
| POST `/v1/logout` | Retain `/v1/logout` | Revoke native session and linked authorization. [LogoutController](../../../RelistenUserService/Controllers/LogoutController.cs), line 19. |
| GET `/api/user/v1/csrf` | Retain existing browser endpoint | Authenticated browser-bound antiforgery token. [BrowserCsrfController](../../../RelistenUserService/Controllers/BrowserCsrfController.cs), line 13. |
| GET `/auth/session/start` | Retain existing browser endpoint | Validated `return_to`, optional `select_account`; begin web authorization challenge. [WebSessionController](../../../RelistenUserService/Authentication/Browser/WebSessionController.cs), line 27. |
| GET `/auth/session/callback` | Retain existing browser endpoint | Complete OIDC client authentication, create web session, set browser cookies, return locally. Same file, line 74. |
| POST `/auth/session/logout` | Retain existing browser endpoint | Revoke web/parent session; clear cookies; navigation response. Same file, line 144. |
| POST `/auth/session/switch-account` | Retain existing browser endpoint | Validated `return_to`; revoke and restart account selection. Same file, line 155. |
| GET `/auth/sso/clear` | Retain existing auth-host endpoint | Validated `web_origin` and `return_to`; clear auth cookie after lifecycle action. Same file, line 203. |
| GET `/connect/authorize` | Retain OIDC endpoint | Authorization-code/PKCE parameters and provider choice; session/authorization completion. [AuthorizationController](../../../RelistenUserService/Authentication/OpenIdConnect/AuthorizationController.cs), line 29. |
| POST `/connect/token` | Retain OIDC endpoint | Authorization-code and refresh grants; request tokens are protocol credentials. [TokenController](../../../RelistenUserService/Authentication/OpenIdConnect/TokenController.cs), line 22. |
| POST `/connect/token/` | Retain configured slash alias | OpenIddict explicitly registers both token spellings; same replay boundary. [AuthenticationServiceCollectionExtensions](../../../RelistenUserService/Authentication/AuthenticationServiceCollectionExtensions.cs), lines 377–381. |
| GET and POST `/signin-google` | Retain provider callbacks | OpenIddict external identity completion; methods are two route combinations. [ExternalIdentityCallbackController](../../../RelistenUserService/Authentication/OpenIdConnect/ExternalIdentityCallbackController.cs), line 16. |
| GET and POST `/signin-apple` | Retain provider callbacks | Same ownership and method coverage for Apple. Same file, line 21. |
| GET `/development/sign-in` | Retain development-only endpoint | Persona chooser; mounted only with configured development personas. [DevelopmentPersonaEndpoints](../../../RelistenUserService/Authentication/Development/DevelopmentPersonaEndpoints.cs), line 13. |
| POST `/development/sign-in` | Retain development-only endpoint | Development persona sign-in; same runtime flag. Same file, line 14. |

The existing `/v1/library` browser policy family remains in place. Retest its
host/origin relay, authorization, CSRF and no-store behavior after payload/identity
changes; no `/v2/library` family is added. The implementation plan now also
converts owned account/auth user/session identities to canonical text and requires
coordinated tester re-sign-in. OpenIddict-private keys and external provider values
retain their formats; first-party `sub`/`sid` values use the approved IDs.

## Health and middleware-mounted routes

| Existing route family | Proposed disposition | Evidence and scope |
| --- | --- | --- |
| API GET `/health/live` | Retain `/health/live` | Process liveness; [HealthController.cs](../../../RelistenApi/Controllers/HealthController.cs#L24). |
| API GET `/health/ready` | Retain `/health/ready` | Dependency readiness, 503 on failure; same controller line 30. |
| Accounts GET `/health/live` | Retain `/health/live` on account service | Process liveness; [Program.cs](../../../RelistenUserService/Program.cs#L76). |
| Accounts GET `/health/ready` | Retain `/health/ready` on account service | Database connectivity, 503 on failure; same file line 77. |
| `/relisten-admin/hangfire/**` | Retain operator dashboard | Non-development only, Hangfire middleware and authorization filter; [Startup.cs](../../../RelistenApi/Startup.cs#L379). Middleware owns its internal routes. |
| GET `/api-docs/{documentName}/swagger.json` | Add `v4` document; retain v2/v3 documents | [Startup.cs](../../../RelistenApi/Startup.cs#L385). V4 document must include every v4 action and typed string schema, including admitted operator API commands. |
| `/api-docs/**` | Retain v2 Swagger UI | Existing Swagger UI asset family; Startup line 390. |
| `/api-docs-v3/**` | Retain v3 Swagger UI | Existing Swagger UI asset family; Startup line 396. |
| `/api-docs-v4/**` | Add v4 Swagger UI | Proposed companion UI using `/api-docs/v4/swagger.json`. |
| Static files from API `wwwroot` | Retain static asset paths | `UseStaticFiles()` precedes MVC; Startup line 373. These are file-provider paths, not catalog actions or entity IDs. |
| GET `/.well-known/openid-configuration` | Retain protocol discovery on auth host | OpenIddict Server 7.6.0 default configuration endpoint; confirmed by the options probe below. |
| GET `/.well-known/oauth-authorization-server` | Retain protocol discovery alias on auth host | Second package-default configuration URI; same probe. |
| GET `/.well-known/jwks` | Retain protocol JWK endpoint on auth host | Package-default JSON Web Key Set URI; same probe. |

API `Startup.Configure` calls `UseMvc()` without a conventional route template
or URL rewrite in this checkout. Account `Program` calls `MapControllers()` plus
two health endpoints and conditional development endpoints. The reviewed
`WebOriginRelayMiddleware` changes effective origin, not request path. This
inventory does not infer deployed ingress rewrites or undocumented upstream
proxy aliases; those require deployment evidence if the contract promises them.

The OpenIddict route check instantiated `OpenIddictServerOptions` from the
installed `OpenIddict.Server` 7.6.0 package in an isolated .NET file-based app.
It reported both discovery paths and `.well-known/jwks`; this checkout then
adds `/connect/authorize`, `/connect/token`, and `/connect/token/` explicitly.
No end-session, user-info, revocation, introspection, device authorization, or
pushed-authorization endpoint is registered by those defaults or this server
configuration. This establishes local registration intent, not deployed reachability.

## Coverage boundaries and implementation checks

No public discovery or playback capability above is deferred. The obsolete
global-recent alias maps to one canonical v4 capability; operational HTML,
health, OAuth, and profile routes retain their appropriate service versions.
Import commands receive v4 equivalents without becoming anonymous reads.

Planning must freeze supported parameter spellings and error responses rather
than accidentally preserving quirks. Route-coverage tests should compare the
actual endpoint/action descriptors against this inventory, including middleware
registrations, while contract tests check nested typed IDs and the single
`normalization` object. Operational/auth endpoints need admission tests; route
inventory alone does not establish authorization correctness or production
availability.
