# Implement durable browser sessions for Relisten web

This ExecPlan is the execution source of truth for the browser-session work in
`RelistenApi`, `relisten-web`, and `relisten-flux`. Keep `Progress`,
`Surprises and discoveries`, `Decision log`, `Verification evidence`, and
`Outcomes and retrospective` current as the work proceeds.

## Purpose and user-visible outcome

Relisten web will sign a listener in through Relisten's existing OpenID Connect
(OIDC) authorization server. The browser will receive one opaque session cookie.
Browser JavaScript will never receive or store an access token or refresh token.
The User Service will persist only a SHA-256 hash of the cookie validator in
PostgreSQL.

The first product-facing result is an authentication and library foundation, not
a favorites user interface. A small typed Timber client will be able to read the
current account, read the library snapshot and change feed, submit idempotent
favorite mutations with antiforgery protection, and sign out. Layered evidence
will prove the complete local path:

    local Timber
      -> local User Service
      -> development persona
      -> Relisten authorization code with S256 PKCE
      -> local Timber callback
      -> opaque PostgreSQL-backed web session

Focused API tests own session, credential, authorization, antiforgery, and data
invariants. Vitest owns the pure client and proxy tests. One short Playwright
test owns browser-visible regression coverage. Agent-driven Browser or Chrome
inspection and direct read-only PostgreSQL queries own the local cross-layer
proof. No browser test parses a credential, queries PostgreSQL, supervises
services, or reimplements UUIDv7.

After that local proof passes, this plan will name the exact production Flux
changes and rollback steps. Production remains read-only until the repository
owner approves that proposal. A Google sign-in is a production write because it
creates session rows, so production Google testing also waits for approval.

## Authority and repository boundaries

Use only these existing worktrees:

- API: `/Users/alecgorge/code/relisten/RelistenApi`, branch
  `codex/web-session-foundation-api`, based on `master` at
  `a30c02e928bad97ceb1c0fa5eaeb8e3ee1afec5c`.
- Web: `/Users/alecgorge/code/relisten/relisten-web`, branch
  `codex/web-session-foundation-web`, based on `timber-migration-v1` at
  `16775dc1aa40c6cf0739e771c164b75280da7c36`.
- Flux: `/Users/alecgorge/code/relisten/relisten-flux`, branch
  `codex/web-session-foundation-flux`, based on `main` at
  `7488a952bb4ab7d174ce08e2d46c692ccb166033`.

Do not create a Git worktree. Do not stash, reset, discard, or overwrite user
work. Verify the active branch before every edit and commit. The primary agent
owns all edits, tests, branch changes, and commits. Read-only investigation and
review agents must not edit files, switch branches, or run Git mutations.

Before explicit production approval, the allowed production actions are local
Flux branch edits and read-only inspection of the PostgreSQL replica,
Kubernetes resources, image metadata, logs that contain no credentials, and
public anonymous endpoints. Local Flux changes may be validated and committed.
Do not apply a manifest, change a Kubernetes Secret, reconcile Flux, deploy an
image, run a migration, start a production sign-in, create a production
session, or mutate a production favorite.

## Context and responsibility map

`/Users/alecgorge/code/relisten/RelistenApi/RelistenUserService` is one ASP.NET
Core process with three protocol roles. `auth.relisten.net` is the Relisten OIDC
issuer and external-provider host. `accounts.relisten.net` serves the existing
resource API under `/v1/*`. The local and production proxies route
`/auth/session/*`, `/v1/library/*`, exact `/v1/me`, and exact
`/api/user/v1/csrf` by convention. Proxy routing never grants account access.
The reviewed controllers preserve native scopes and web capabilities through
separate credential validation.

`AuthorizationController` separates web and native authorizations before native
session creation. A web authorization creates no `NativeSession`, requests no
accounts audience, and receives no refresh token. OpenIddict can issue
short-lived bootstrap credentials inside its server-side client pipeline. The
callback discards them after creating the opaque web session.

External Google/Apple completion and Development persona completion use the
same durable `auth_sso` lifecycle. The auth-host cookie and web-resource cookie
use different names and hosts.

`CurrentAccountContext` is credential-neutral. Native authorization supplies a
native session identity. Browser authorization supplies a web session identity
and fixed capabilities. `GET /v1/me` returns the established account contract;
`native_session_uuid` remains present for a bearer request and is omitted for a
web-session request. Relisten never fabricates a native session for the browser.

`/Users/alecgorge/code/relisten/relisten-web` is a Timber application built with
Vite. The browser-session work belongs in Vite's development server
configuration, a small user-session client, a development-only diagnostic
route, and one short Playwright smoke. The existing public catalog client
remains separate because public catalog responses can be cached while user
responses must use `cache: "no-store"`.

`/Users/alecgorge/code/relisten/relisten-flux` contains the production User
Service, Timber, ingress, and Secret references. Its browser-session branch is
committed through `92769dd` and remains unapplied. The existing image workflow
remains unchanged.

## Progress

- [x] (2026-08-26) Read the repository instructions, verified clean and
  non-diverged bases, created the three requested branches, and committed the
  initial ExecPlan before implementation.
- [x] (2026-08-26) Proved the maintained OpenIddict protected-state callback,
  then implemented durable sessions, the confidential web client, browser
  lifecycle endpoints, and the first resource facade.
- [x] (2026-08-27) Consolidated reviewed resources onto `/v1`, made account
  context credential-neutral, rejected ambiguous credentials, and made library
  authorization plus cookie-mutation protection default at controller and
  middleware boundaries.
- [x] (2026-08-27) Organized `RelistenUserService/Authentication` by
  responsibility and completed the loopback HTTPS, Host Filtering, and .NET
  Secret Manager setup.
- [x] (2026-08-27) Added Timber's fixed HTTPS origin, exact proxy families,
  typed browser client, one-command certificate setup, ten focused Vitest
  tests, and one short Playwright smoke. No cross-layer test orchestrator
  remains.
- [x] (2026-08-27) Completed the local Browser and read-only PostgreSQL proof.
  The proof covered sign-in, `/v1/me`, library reads, favorite add/replay/remove,
  CSRF failures, route isolation, session revocation, and token-storage absence.
  It restored the initial favorite state.
- [x] (2026-08-27) Completed fresh API, web, E2E, and Flux reviews. Validated
  each finding, applied accepted fixes, ran `code-simplifier`, and reran the
  relevant focused and broad checks.
- [x] (2026-08-27) Inspected production read-only and committed the unapplied
  Flux configuration and runbook through `92769dd`. Prepared the exact
  production proposal below. No production state changed.
- [ ] Receive explicit production approval.
- [ ] After approval only: apply the approved Flux commit, deploy the approved
  API commit, run the Google and favorite smoke tests, restore favorite state,
  and record evidence.

## Surprises and discoveries

- Native authorization always created a `NativeSession` and
  `NativePrincipalFactory` always added the accounts audience. The web client
  therefore needs a separate authorization branch and bootstrap principal.
- Vite 8 skips its built-in Host check under HTTPS, and proxy keys use character
  prefix matching. Timber now enforces the exact development Host in middleware
  and uses segment-aware proxy matchers.
- A separate browser resource facade duplicated the existing `/v1` contracts.
  Reviewed `/v1/me` and `/v1/library/*` actions now accept either native scopes
  or persisted web capabilities, never both credentials.
- Per-action antiforgery filters and per-action proxy entries were easy to omit.
  Library authorization now applies at the controller boundary, and one global
  middleware protects every unsafe request carrying the web-session cookie.
- Production Traefik uses character-prefix matching and the existing web
  Ingress already owns `relisten.net`. Each routed family therefore uses an
  exact root plus slash prefix before the existing `/` catch-all.
- The User Service already emits `private, no-store`; Cloudflare reported the
  anonymous account checks as dynamic. The Flux branch needs no cache middleware.
- Production PostgreSQL 17.10 supports UUIDv7 inspection but has no
  `identity.sessions` table or `relisten-web` client. The first approved User
  Service start will create both through the additive migration and initializer.
- The auth-host cookie-clear endpoint could delete a concurrent active SSO
  cookie. Commit `708ec6a` binds deletion to the exact revoked parent validator,
  linked web session, origin, and revocation timestamp.

## Decision log

- Use one `identity.sessions` table for `auth_sso` and `web`. Encode a cookie as
  a versioned UUIDv7 session ID plus a random 256-bit validator, and store only
  its SHA-256 hash. Keep credential encoding, lifecycle, authentication, and
  authorization in separate types. Date: 2026-08-26.
- Persist exactly three web capabilities: account profile read, library read,
  and favorite mutation. `auth_sso` has no resource capability. A new API action
  remains browser-inaccessible until its controller policy and capability are
  reviewed. Date: 2026-08-26.
- Use maintained OpenIddict registrations for the exact canonical and local
  callbacks. Keep redirect validation, state, correlation, nonce, PKCE, and code
  exchange in OpenIddict. Keep Development personas separate from the external
  Google/Apple profile. Date: 2026-08-26.
- Use `https://web.relisten.localhost:5173` as the fixed development origin.
  Timber overwrites `X-Relisten-Web-Origin`; the User Service accepts it only on
  reviewed paths, from configured backend hosts, for an exact allowed HTTPS
  origin. Date: 2026-08-26.
- Require session-bound antiforgery plus exact Origin for unsafe cookie requests.
  Native bearer mutations retain their existing contract. Logout revokes the
  web and parent auth-SSO rows on the protected POST; the auth-host GET may only
  clear the already-revoked parent cookie. Date: 2026-08-26.
- Serve browser-capable resources from `/v1/me` and `/v1/library/*`. Native
  requests require native scopes; web requests require persisted capabilities.
  Reject simultaneous bearer and cookie credentials. Date: 2026-08-26.
- Route reviewed resource families by convention. Apply read/write policy at the
  library controller and cookie-mutation protection in global middleware so a
  new library action inherits both defaults. Date: 2026-08-27.
- Keep Playwright to one short browser-visible smoke. Focused API tests own
  protocol and security failures. Browser DevTools plus read-only PostgreSQL
  inspection own the local cross-layer proof. Retain a test only when it names a
  concrete failure or compatibility contract. Date: 2026-08-27.
- Provide one idempotent local setup command for the trusted certificate and
  client secret outside Git. Keep the fixed hosts, port, and host allowlist.
  Date: 2026-08-27.
- Put the six production route rules in the existing web Ingress and keep cache
  policy in the User Service. Keep all production writes approval-gated. After
  approval, use the existing image workflow in this order: configure, deploy,
  verify, expose routes, then smoke test. Date: 2026-08-27.

## API milestones

### A1. Maintained callback-state proof

The web client uses these exact callbacks:

    https://relisten.net/auth/session/callback
    https://web.relisten.localhost:5173/auth/session/callback

A completed local HTTPS spike proved callback completion, protected relative
`return_to` restoration, exact registration selection, and replay rejection.
Checked-in configuration tests preserve the exact confidential-client and S256
PKCE registrations. OpenIddict owns the correlation cookie, PKCE verifier,
nonce, code exchange, and 15-minute state-token lifetime. Redirect validation
and client token storage remain enabled.

### A2. Persist and authenticate durable sessions

`RelistenUserService/Identity/Entities/IdentitySession.cs` and its EF
configuration map to `identity.sessions`. Each row contains:

- UUIDv7 `id` and owning `user_id`;
- purpose `auth_sso` or `web`;
- exactly 32 bytes of `validator_hash` and no raw validator column;
- captured `security_version`;
- `authenticated_at`, `created_at`, `updated_at`, and `last_seen_at`;
- `sliding_expires_at` and `absolute_expires_at`;
- nullable `revoked_at`;
- nullable parent `auth_sso_session_id`, required only for `web`;
- nullable exact `web_origin`, required only for `web`;
- a fixed capability flags value. `auth_sso` must have no resource capability.

Use database check constraints for UUIDv7, purpose-specific nullability, hash
length, expiration order, capability bits, and exact parent/origin requirements.
Use a self-reference that does not cascade-delete session evidence.

The credential codec generates 32 random bytes with a cryptographic
random-number generator, encodes version plus session ID plus validator in a
cookie-safe form, hashes the validator with SHA-256, and compares hashes with
`CryptographicOperations.FixedTimeEquals`. Keep database queries out of the
codec. A lifecycle service creates, validates, touches, and revokes rows. An
authentication handler reads one named cookie and requests one required purpose
from the lifecycle service. Authorization policies check the fixed capability.

Every authenticated request joins or loads the current user and rejects the
credential if the purpose is wrong, the validator differs, the row or parent
relationship is invalid, the row is revoked, sliding or absolute expiry has
passed, the user is not active, or the user's current `security_version` differs
from the captured value. A touch can update `last_seen_at` and sliding expiry
only when at least one hour has passed. A web session slides for 30 days but
never beyond 180 days from creation. An auth-SSO session expires 30 days after
creation.

Migration `20260827052217_AddDurableBrowserSessions` contains no confidential
client secret and passed the local PostgreSQL migration tests.

### A3. Durable auth SSO and the web OIDC client

The external-provider callback and Development persona completion call the same
external-identity completion service and then create an
`auth_sso` row plus `__Host-relisten_auth`. The cookie is Secure, HttpOnly,
SameSite=Lax, Path `/`, and has no Domain. Development personas remain available
only when the host environment is `Development` and the explicit persona option
is enabled.

`relisten-web` is a confidential authorization-code client that
requires S256 PKCE. Provision its secret from local configuration and, after
approval, a deployment Secret reference. Never commit, log, or send the secret
to Timber. The server registration permits both exact callbacks and only the
scopes needed to return a validated subject and account profile. It permits no
`offline_access` and no accounts resource.

`AuthorizationController` branches on the validated web client ID.
Native clients keep the existing `NativeSession`, authorization, audience,
scope, access-token, and rotating refresh-token behavior. The web client creates
no `NativeSession` and receives no accounts-resource audience or refresh token.
It receives only the claims needed by the server-side web callback, including a
validated subject and a link to the authenticated auth-SSO session.
Build that principal with a separate web-bootstrap principal factory. Do not
reuse `NativePrincipalFactory`, because that factory always adds the accounts
resource audience and native session claims.

The browser lifecycle endpoints are:

    GET  /auth/session/start?return_to=<relative application path>
    GET  /auth/session/callback
    POST /auth/session/logout
    POST /auth/session/switch-account

`return_to` must start with one `/`, remain on the application origin, and
contain no authority, scheme, backslash, control character, or protocol-relative
form. Store it only in OpenIddict-protected state. The start endpoint chooses a
client registration from the validated external browser origin. The callback
uses `AuthenticateAsync` on the OpenIddict client scheme. The maintained
pipeline must validate state, correlation, nonce, code exchange and binding,
issuer, audience, token lifetime, and `sub`. The callback then loads the current
active user, checks the linked auth-SSO session, creates a fresh `web` session,
sets `__Host-relisten_session`, and discards all bootstrap credentials.

Logout and switch-account require the current web session, exact Origin, and a
session-bound antiforgery token. Both revoke the current web row and linked
auth-SSO row in one database transaction. Logout returns through the auth host to
expire `__Host-relisten_auth`, then returns to a fixed local or canonical web
origin. The auth host emits the deletion header only when the presented auth
cookie identifies the parent row that the logout transaction revoked with a
linked web row for that origin. Switch-account uses the same cleanup and
restarts authorization with provider account selection. Each host expires only
the cookie it owns.

### A4. Enforce cookie, CSRF, origin, host, and cache policy

Configure these exact cookies:

    __Host-relisten_auth
    __Host-relisten_session
    __Host-relisten_csrf

The two authentication cookies are Secure, HttpOnly, SameSite=Lax, Path `/`,
and host-only with no Domain. Configure ASP.NET antiforgery to read
`X-Relisten-CSRF`. `GET /api/user/v1/csrf` returns the session-bound request
token and sets the antiforgery cookie. The Timber client reads the response
token, not an authentication credential, and attaches it to mutation requests.

Every cookie-authenticated mutation must reject an absent Origin, a non-HTTPS
Origin, an origin with the wrong host or port, a missing antiforgery header, an
invalid token, and a token bound to another session. Apply
`Cache-Control: private, no-store` to all `/auth/session/*` responses, the
auth-SSO cookie-clear response, the CSRF response, and the segment-safe `/v1`
account family. Do not add credentialed CORS and do not configure
`SameSite=None`.

Extend host filtering for the exact development and production hosts. Add one
small external-origin reconstruction middleware before OpenIddict. It may honor
`X-Relisten-Web-Origin` only when all required conditions are true:

1. The request path is under `/auth/session` or `/v1/library`, or the path is
   exact `/v1/me` or `/api/user/v1/csrf`.
2. The actual backend Host equals a configured proxy target host.
3. The header parses as an absolute HTTPS origin with no path, query, userinfo,
   or fragment.
4. The entire origin string, including its explicit port when present, equals a
   configured allowed web origin.

The middleware must ignore or reject every other use. It must not trust
`X-Forwarded-Host` from an arbitrary caller. OpenIddict redirect validation
remains enabled.

### A5. Share explicitly reviewed resource routes

The proxy may reach these reviewed resource families:

    GET  /v1/me
    GET  /v1/library/snapshot
    GET  /v1/library/changes?after=<opaque cursor>
    POST /v1/library/favorite-mutations:batch

Keep `GET /api/user/v1/csrf` and `/auth/session/*` browser-specific. Reaching a
proxy family does not authorize a cookie. The API must still reject every
unreviewed `/v1/*` action.

Refactor `CurrentAccountContext` so it carries the active user and the verified
credential kind without fabricating a native session. Reuse
`AccountProfileFactory`, `LibraryReadService`, and `FavoriteMutationService`
through the established controllers. `native_session_uuid` is nullable in the
shared account response and uses property-level null omission. Bearer responses
must include the verified native session UUID unchanged. Web-session responses
must omit the property.

The library controller applies one policy to every action. Safe methods require
the native library-read scope or persisted web library-read capability. Unsafe
methods require the native library-write scope or persisted favorite-mutation
capability. A request with both credentials fails before identity selection. A
global boundary requires session-bound antiforgery and exact Origin for every
unsafe request that carries the web-session cookie. Native bearer mutations do
not acquire a browser CSRF requirement. Every other `/v1/*`, Sonos, adapter,
internal, and playback route stays bearer-only.

## Timber milestones

### W1. Install dependencies and inspect Timber ownership

From `/Users/alecgorge/code/relisten/relisten-web`, run:

    pnpm install --frozen-lockfile

Expected result: pnpm exits `0` without changing `pnpm-lock.yaml`. Read the
installed documentation under `node_modules/@timber-js/app/docs/` for Vite
configuration, route handlers, development server behavior, and environment
boundaries. Before editing each Timber-owned route or boundary, run:

    npx timber graph <relevant-file> --json

Record each relevant graph command and its result below. Do not infer a Next.js
API from a similar file name.

### W2. Fix the development origin and proxy only reviewed paths

Update `/Users/alecgorge/code/relisten/relisten-web/vite.config.ts` so the
development server uses exactly
`https://web.relisten.localhost:5173` and `strictPort: true`. Load a trusted
local certificate from environment-configured absolute paths. Do not commit the
certificate or private key. The setup command uses this local directory outside
all repositories by default:

    /Users/alecgorge/Library/Application Support/Relisten/local-browser-session-tls

Because Vite 8 disables its built-in Host check under HTTPS, add a small
development-only Vite middleware that accepts only the exact Host
`web.relisten.localhost:5173`. Prove that middleware before relying on it. Keep
the host check separate from Timber route code.

Configure the development-only reverse proxy for `/auth/session/*`,
`/v1/library/*`, exact `/v1/me`, and exact `/api/user/v1/csrf`. Do not proxy a
wildcard `/v1/*`. Select one target with a bounded environment value:

- `local`: the exact local HTTPS User Service target;
- `production`: `https://relisten.net`, usable only after the approved
  production rollout.

The proxy preserves method, query, body, Cookie, redirects, and the browser's
Origin. It relays every `Set-Cookie` header without merging them. It deletes any
browser-supplied `X-Relisten-Web-Origin`, `Forwarded`, and `X-Forwarded-*`
headers. It then sets only `X-Relisten-Web-Origin` to the configured fixed Timber
origin. Use segment-aware route families. Do not use a fetch-based proxy,
follow upstream redirects, rewrite `Location`, or rewrite cookie domains. Do
not proxy `/auth/session-evil`, `/api/user/v10`, unreviewed `/v1/*`, `/connect`,
a wildcard `/api`, or any other route. Keep TLS verification enabled; the
trusted local certificate must make the local target valid.

### W3. Add a small typed browser client and test harness

Add one browser-session client under `src/lib/` with types for `/me`, library
snapshot, library changes, favorite mutation batches, and CSRF responses. Every
request uses a relative URL, `credentials: "include"`, and `cache: "no-store"`.
Mutation methods first acquire a CSRF token, then attach
`X-Relisten-CSRF`. Do not share the public catalog client's cache or add a
generic API SDK, token manager, global auth state, or BFF.

Add a development-only diagnostic route that gives the browser smoke a stable,
redacted return target. The route contains only a test marker and returns 404
outside Development. The smoke invokes the reviewed routes without rendering
credentials or personal data. Do not add product favorites UI or login styling.

### W4. Keep browser automation small

Vitest runs the pure client and proxy suites in Node. Playwright runs only one
short smoke for development-persona sign-in, one authenticated read, and
logout. Playwright uses already-running services. It does not supervise
services, query PostgreSQL, parse credentials, implement UUIDv7, intercept
callbacks, or scan logs.

Do not enable Playwright traces, request dumps, videos, or screenshots by
default because callback URLs and cookies are credentials. A failed test may
write only a redacted assertion report. Never log a Cookie header, Set-Cookie
value, validator, state, code, ID token, access token, refresh token, or personal
profile field.

### W5. Keep only high-value tests

Retain a test only when its name and assertions identify a concrete failure mode
or compatibility contract. Delete exact endpoint counts, duplicated
configuration-shape assertions, and option round-trip tests that only restate
the implementation. Prefer request behavior and production code paths:
credential ambiguity, native response compatibility, web field omission,
method-aware authorization, session-bound antiforgery, exact Origin, segment
boundaries, durable session lifecycle, and favorite idempotency. A focused
boundary test must prove that `/v1/library/new-action` reaches the API while
`/v1/library-evil` and unrelated `/v1/*` do not. API authorization must still
deny a web session that lacks the new action's reviewed capability.

## Local/local E2E proof

### Local setup

`pnpm setup:browser-session` creates one trusted certificate for these exact
names:

    web.relisten.localhost
    auth.relisten.localhost
    accounts.relisten.localhost

The command stores the certificate and confidential local client secret under
`~/Library/Application Support/Relisten/local-browser-session-tls`. It writes
the User Service configuration through .NET Secret Manager. No certificate,
private key, or client secret belongs in Git.

From `/Users/alecgorge/code/relisten/RelistenApi`:

    ./start-local-databases.sh
    dotnet run --project RelistenUserService/RelistenUserService.csproj

PostgreSQL must answer on `127.0.0.1:15432`, and the User Service must listen on
`https://127.0.0.1:5443` with Development personas enabled. Startup must not
seed a web session.

From `/Users/alecgorge/code/relisten/relisten-web`:

    pnpm install --frozen-lockfile
    pnpm setup:browser-session
    pnpm dev

The Playwright smoke and agent-driven Browser proof use these already-running
services. Both enter through the same external-identity completion path used by
Google; neither seeds a web session.

### Behavioral acceptance criteria and proof ownership

Focused API tests establish the durable API invariants below. The completed
local HTTPS spike supplies the maintained callback-state evidence named in item
9.

1. Session credentials use UUIDv7 plus a random 256-bit validator, PostgreSQL
   stores only the 32-byte hash, and comparisons use the production codec.
2. Session purpose, user status, security version, expiry, touch throttling,
   parent linkage, and linked revocation are enforced.
3. A web authorization creates no native session, receives no accounts resource
   audience, and receives no refresh token.
4. Bearer `/v1/me` includes the verified native session UUID. Web `/v1/me`
   omits the property.
5. Native and web library reads require their distinct read permission. Native
   and web mutations require their distinct write permission.
6. Simultaneous bearer and cookie credentials fail. Unreviewed `/v1/*` routes
   remain unavailable to web sessions.
7. Every unsafe request that carries the web-session cookie requires the exact
   Origin and a valid antiforgery token bound to that session. Native bearer
   mutations do not acquire that requirement.
8. Favorite replay is idempotent, library changes are observable, and logout
   revokes the linked sessions.
9. The maintained OpenIddict pipeline completes a protected-state callback and
   rejects callback replay. OpenIddict retains ownership of correlation, PKCE,
   nonce, state lifetime, code exchange, and redirect validation.
10. Authentication cookies use the exact names, Secure, HttpOnly,
    SameSite=Lax, Path `/`, and no Domain. Antiforgery uses the exact cookie and
    request-header names.

The short Playwright smoke establishes browser-visible Development-persona
sign-in through the real OIDC flow, browser-safe `/v1/me`, library snapshot
reachability, and logout. The agent-driven Browser proof establishes `/v1/me`,
snapshot, changes, route isolation, credential ambiguity rejection, CSRF
failure, favorite add/replay/remove, and logout against the same running
services. Focused API tests own cookie metadata and absent or incorrect Origin
because browser APIs do not expose or forge those values safely.

Direct read-only PostgreSQL inspection may confirm the session purpose, UUID
version, validator-hash length, absence of a raw-validator column, lack of a
web-created native session, and revocation timestamps. The inspection must not
read or print the cookie or validator. Browser DevTools may inspect visible
network behavior, rendered data, and console output. Browser automation must
not inspect or expose cookie values, callback query values, tokens, or personal
data. No proof artifact may retain those values.

### Focused and broad validation order

Run the smallest relevant API tests after each API change. When the API work is
coherent, run from `/Users/alecgorge/code/relisten/RelistenApi`:

    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj --no-restore \
      --filter "FullyQualifiedName~TestSessionCredentialCodec|FullyQualifiedName~TestIdentitySessionLifecycleIntegration|FullyQualifiedName~TestBrowserFacadeBoundary|FullyQualifiedName~TestWebSessionAntiforgery|FullyQualifiedName~TestWebRequestBoundaries|FullyQualifiedName~TestFavoriteLibraryIntegration|FullyQualifiedName~TestReviewedAccountAccessAuthorization|FullyQualifiedName~TestHostBoundaryMiddleware|FullyQualifiedName~TestRefreshTokenEndpointBoundary"
    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
    dotnet build RelistenApi.sln

Each command must exit `0`. Do not rerun an unchanged failing command. Diagnose
the failure, make a code or environment change, and then rerun the smallest
affected check.

Run from `/Users/alecgorge/code/relisten/relisten-web`:

    pnpm typecheck
    pnpm lint
    pnpm build
    pnpm test:browser-session
    pnpm test:smoke:browser-session

`pnpm test:browser-session` runs the focused Vitest client and proxy suites
without services. `pnpm test:smoke:browser-session` runs one short Playwright
smoke against already-running services. Each command must exit `0`. Record test
counts and relevant assertions without recording credentials or personal data.

After focused checks pass, request two fresh read-only reviews. One review must
cover API authentication, OpenIddict, session, CSRF, facade authorization, and
native regressions. The other must cover Timber proxy behavior and E2E
completeness. Each finding must name a concrete failure mode and file evidence.
Validate every finding before changing code.

Then apply the `code-simplifier` skill only to changed code and tests. Remove
iteration residue, duplication, unused helpers, and brittle test scaffolding
without changing a contract. Rerun the affected focused checks and inspect the
final diff. Apply the `deslop` skill to comments, this plan, runbooks, commit
bodies, status updates, and the final handoff.

## Production approval checkpoint

The local proof, final reviews, production read-only inspection, and unapplied
Flux authoring are complete. The proposed production artifacts are the API
branch whose runtime code ends at `708ec6a` and the Flux branch through
`92769dd`. The web commits are development-only; this rollout does not require
a Timber image or product UI change.

Flux commit `2442be7` changes exactly these files:

- `clusters/relisten3-k3s/apps/relisten-user-service.yaml` adds
  `relisten.net` to `AllowedHosts`; sets `Accounts__WebOrigins__0` to
  `https://relisten.net`; sets `Accounts__WebOrigins__1` to
  `https://web.relisten.localhost:5173`; reads
  `Accounts__WebClientSecret` from key `WebClientSecret` in existing Secret
  `default/relisten-user-service-secrets`; and sends Host
  `accounts.relisten.net` from the startup, liveness, and readiness probes.
- `clusters/relisten3-k3s/apps/relisten-web.yaml` adds six paths to the existing
  `relisten-web-production-ingress` under host `relisten.net`, before the
  Timber `/` catch-all. The paths, in source order, are exact
  `/auth/session`, prefix `/auth/session/`, exact `/api/user/v1/csrf`, exact
  `/v1/me`, exact `/v1/library`, and prefix `/v1/library/`. Every new path
  targets `relisten-user-service-srv:8080`.
- `clusters/relisten3-k3s/README.md` documents route ownership, the one-key
  confidential Secret patch, the existing image workflow, health checks, and
  rollback.

The route changes create same-origin reverse routing; they do not return a
redirect to the browser. The exact-root and slash-prefix pairs are required by
the live Traefik `strictPrefixMatching=false` setting. They do not match
`/auth/session-evil` or `/v1/library-evil`. No separate Ingress, router priority,
TLS certificate, DNS record, NetworkPolicy, cache middleware, or workflow file
changes.

The User Service already owns cache policy. It returns `Cache-Control: private,
no-store` for the account API and browser lifecycle responses. The Flux branch
adds no Traefik cache middleware, and Cloudflare currently reports anonymous
account checks as dynamic.

The confidential client accepts only these compiled callback URIs:

    https://relisten.net/auth/session/callback
    https://web.relisten.localhost:5173/auth/session/callback

Production uses the external-provider runtime profile. Development personas
remain disabled outside ASP.NET Core `Development`. The local production-backed
proxy overwrites `X-Relisten-Web-Origin` with the second exact origin. The User
Service accepts that header only on the reviewed paths, when the actual backend
Host is configured, and when the complete HTTPS origin matches the allowlist.

After approval, create one stable 64-character letter-and-digit credential in
the existing 1Password vault. The command suppresses its output:

    op item create \
      --category=password \
      --title='Relisten web OIDC client' \
      --vault=Private \
      --generate-password='letters,digits,64' \
      >/dev/null

Create the item only once. The runbook reads it into a subshell and sends a
one-key JSON merge patch to `kubectl` over stdin. That command updates only
`WebClientSecret` in `default/relisten-user-service-secrets`; it does not print
the credential or rebuild unrelated Secret keys. Do not rotate the credential
on later rollouts because the persisted OpenIddict client must keep the same
secret.

After approval, use the existing deployment workflow in this order:

1. **Configure.** Record current health and the running User Service image ID.
   Create `WebClientSecret`, patch that one Secret key, and apply only
   `clusters/relisten3-k3s/apps/relisten-user-service.yaml`. Do not expose the
   new `relisten.net` paths yet.
2. **Deploy.** Push the approved API commit and run the unchanged workflow:

       gh workflow run build_and_push_image.yml \
         --repo RelistenNet/RelistenApi \
         --ref codex/web-session-foundation-api \
         -f component=user-service

   Watch that workflow with `gh run watch <run-id> --exit-status`. The workflow
   builds the normal User Service image and restarts only its Deployment.
3. **Verify.** Wait for the rollout and check the existing public hosts:

       kubectl --context relisten3-k3s --namespace default rollout status \
         deployment/relisten-user-service --timeout=10m
       curl --fail --silent --show-error \
         https://auth.relisten.net/.well-known/openid-configuration >/dev/null
       curl --fail --silent --show-error \
         https://accounts.relisten.net/health/ready >/dev/null

4. **Expose.** Apply only
   `clusters/relisten3-k3s/apps/relisten-web.yaml`. Confirm that `/v1/me` and
   `/v1/library/snapshot` now reach the User Service, while
   `/v1/library-evil` and unrelated `/v1/*` paths remain on Timber.
5. **Smoke.** Run the approved local-proxy Google proof, the favorite
   add/replay/inverse sequence, logout, and the canonical-host sign-in smoke.

The Deployment runs one replica with `maxSurge: 0`, `maxUnavailable: 1`, and
`Accounts__ApplyMigrationsOnStartup=true`. The new pod connects to the direct
PostgreSQL primary before it listens. Migration
`20260827052217_AddDurableBrowserSessions` creates additive table
`identity.sessions`, its constraints and indexes, and one
`identity.__EFMigrationsHistory` row. Production currently has no
`relisten-web` application, so this rollout is expected to insert one
confidential-client row in `identity.openiddict_applications`. Later restarts
treat an exact row as a no-op; a secret or redirect mismatch aborts startup. A
Data Protection key row may be inserted if key rotation is due.

The approved local-callback and canonical-callback Google proofs are expected to
write these rows:

- An existing `identity.users` or `identity.external_identities` row can receive
  refreshed provider metadata. A first-time account would insert both rows.
- Each sign-in writes protected state, authorization-code, access-token, and ID
  token records in `identity.openiddict_authorizations` and
  `identity.openiddict_tokens`. Neither sign-in writes a refresh token or an
  `identity.native_sessions` row.
- Each completed sign-in inserts one `auth_sso` row and one linked `web` row
  in `identity.sessions`, for two linked pairs in total.
- The first library read may create `user_data.library_states`.
- The favorite add and remove update `user_data.favorites` and append
  `user_data.library_changes` and
  `user_data.favorite_mutation_receipts`. Replaying the same command adds no
  second mutation. Restoring the active favorite state does not remove the
  audit change or receipt rows.
- Each logout sets `revoked_at` on its linked auth-SSO and web pair.

After route exposure, anonymous checks must show Ready pods, healthy auth
discovery and accounts readiness, working Timber and catalog pages, and these
route results:

- `https://relisten.net/v1/me` and
  `https://relisten.net/v1/library/snapshot` reach the User Service, reject the
  anonymous request, and include `Cache-Control: private, no-store`;
- exact `/auth/session` reaches the User Service, returns 404, and includes
  `Cache-Control: private, no-store` without creating OIDC state;
- `/v1/library-evil` and an unrelated `/v1/*` path remain on the Timber
  catch-all. The browser can send the path-wide cookie, but the requests never
  reach the User Service and cannot authenticate there;
- User Service logs contain no startup, migration, OpenIddict, Host Filtering,
  or database errors and contain no credential value.

The approved Chrome test records the chosen favorite's initial active state,
then runs local Timber to the production proxy target, Relisten issuer, Google,
local callback, `/v1/me`, snapshot, changes, add or remove, replay, inverse
mutation, and logout. The final active state must equal the initial state. A
read-only replica query must confirm the session row shapes, validator-hash
length, absent web-created native session, linked revocation, and restored
favorite state without selecting a validator, token, cookie, or personal field.
Inspect Relisten app-origin storage only; do not inspect Google storage or
credentials. After the local-callback proof, complete a second sign-in through
the canonical callback, verify `/v1/me`, and log out. The canonical proof does
not mutate a favorite.

Rollback starts by removing browser reachability with the parent
`2442be7^` version of `clusters/relisten3-k3s/apps/relisten-web.yaml`. Apply the
parent User Service manifest, restore the recorded prior image to the User
Service Deployment, and wait for readiness. Recheck auth discovery, accounts
health, Timber, catalog API, and that `/v1/me` again reaches Timber. Keep the
additive session table, client registration, and Secret key. Do not run the
migration down or rotate the client secret.

Present the completed proposal to the repository owner and ask for explicit
approval. Do not interpret silence, review comments, or approval of local code
as production approval. If approval is not received, leave all local work
committed, report production E2E as pending approval, and keep the goal active.

## Production rollout and rollback

This section remains blocked until explicit approval. The approved operator must
follow the checkpoint order: configure, deploy, verify, expose, then smoke.
Record the workflow run, Kubernetes rollout, public checks, Google proof,
favorite restoration, canonical-host proof, and any rollback here.

## Verification evidence

Record commands, exit codes, test counts, and sanitized observable results here.
Never record a secret, cookie value, validator, token, callback query, or
production user field.

### Preflight evidence

- API: clean `master`; `master...origin/master` was `0 0`; branch created at
  `a30c02e928bad97ceb1c0fa5eaeb8e3ee1afec5c`.
- Web: clean `timber-migration-v1`;
  `timber-migration-v1...origin/timber-migration-v1` was `0 0`; branch created
  at `16775dc1aa40c6cf0739e771c164b75280da7c36`.
- Flux: clean `main`; `main...origin/main` was `0 0`; branch created at
  `7488a952bb4ab7d174ce08e2d46c692ccb166033`.
- Applicable instructions read:
  `RelistenApi/AGENTS.md`, `relisten-web/AGENTS.md`, and
  `relisten-flux/AGENTS.md`.

### Implementation and test evidence

Committed API slices: `78435dc` plan; `0705bb5` persistence; `5e3ea68` web
authorization; `8e48042` initial facade; `38c17d3` shared default-secure
resources; `2841c3e` shared-route evidence; `396c62a` authentication folder
organization; `1209443` local HTTPS configuration; `2b7b114` canonical Host;
`dbb24c1` simplification; and `708ec6a` SSO cookie-clear binding.

Committed Timber slices: `c463bf6` HTTPS, proxy, and client; `eb86da6` short
browser smoke; `c5b417c` development documentation; `86359c1` callback failure
redaction; `9d20af1` first-run setup; and `2aeb1a6` Vitest and Playwright test
ownership.

Committed, unapplied Flux slices: `2442be7` exact configuration and six routes;
runbook corrections `a07f5e5`, `09b109f`, `8b1fe69`, `020cc17`, and
`92769dd`. Source assertions, Kustomize render, client-side apply dry-run, shell
syntax, and `git diff --check` passed. No production state changed.

- Final API validation: the exact security filter above passed 105 tests; all
  141 User Service tests passed; EF reported no pending model changes; and
  `dotnet build RelistenApi.sln --no-restore` passed with no warnings or
  errors.
- Maintained callback-state spike: the local HTTPS authorization completed,
  restored the protected relative return path, selected the exact client
  registration, and rejected callback replay. Checked-in tests preserve the
  exact callback registrations and S256 requirement.
- Timber graph: `npx timber graph
  'src/app/(bare)/browser-session-development/page.tsx' --json` hit Timber's
  packaged data-URL/`fileURLToPath` defect. The running graph endpoint
  classified the diagnostic page as an RSC route with no poisoning or graph
  error.
- Timber focused and broad checks: Vitest passed two files and 10 tests;
  Playwright discovery listed only the one smoke; `pnpm typecheck` exited `0`;
  `pnpm lint` exited `0` with five pre-existing warnings outside changed files;
  and `pnpm build` exited `0` with pre-existing React compiler and chunk
  warnings. Targeted `oxlint` and `oxfmt --check` passed for changed files.
- Local setup: the repository owner ran `pnpm setup:browser-session`. `mkcert`
  installed the local CA and issued one certificate for exactly
  `web.relisten.localhost`, `auth.relisten.localhost`, and
  `accounts.relisten.localhost`. Read-only file inspection confirmed mode 0700
  for the directory, 0600 for the private key and client-secret file, and 0644
  for public certificates. The User Service then started on
  `https://127.0.0.1:5443`, found migrations current, and validated the local
  OpenIddict applications without printing a secret.
- Agent-driven Browser proof: the in-app browser completed Development-persona
  sign-in through the real authorization-code flow and returned to the fixed
  Timber origin. `/v1/me`, snapshot, and changes returned status 200 and
  `Cache-Control: private, no-store`; web `/v1/me` omitted
  `native_session_uuid`. A request with both a dummy bearer credential and the
  web cookie failed. `/v1/library/new-unreviewed-action`,
  `/v1/library-evil`, and an unrelated `/v1/*` route returned 404. Missing and
  incorrect CSRF tokens both returned 403.
- Agent-driven favorite proof: a platform-generated UUIDv7 test favorite was
  absent from the initial nine-item snapshot, added once, observed in snapshot
  and changes, replayed with the same revision, removed, and observed as
  absent. The final active-favorite count again equaled nine. Read-only
  PostgreSQL inspection confirmed zero active synthetic rows, two change rows,
  and two mutation receipts for the add and remove commands.
- Agent-driven session proof: logout returned through the auth-host cookie-clear
  route, returned to the fixed Timber origin, and changed `/v1/me` to 401.
  Read-only PostgreSQL inspection found only the `validator_hash` validator
  column. Recent session IDs were UUIDv7, every validator hash was 32 bytes,
  every purpose-specific row shape was valid, and all ten recent web/auth-SSO
  pairs were revoked after logout. Browser network and console metadata showed
  no access-token, refresh-token, or ID-token URL parameter or log match. No
  cookie value, validator, callback query, token, or personal field was read or
  retained.
- Fresh API, web, E2E, and Flux reviews named concrete failure modes. Accepted
  fixes are in the commit ledger above. The `code-simplifier` pass removed
  duplicate predicates, unused branches, and brittle test scaffolding.
- Production read-only inspection: PostgreSQL `17.10` was read-only;
  `identity.sessions` was absent; UUIDv7 extraction was available; and the
  latest identity migration was `20260719193000_ConfigureProductionIosClient`.
  The User Service had one Ready replica on tag `latest`; the new
  Secret key was absent; all existing Secret values remained unread; Traefik
  was `3.7.4` with non-strict prefix matching; and the existing web TLS
  certificate covered `relisten.net`. Public `/v1/me` still reached Timber and
  Cloudflare reported dynamic cache status. No production state changed.
- Production approval: proposal ready; explicit approval pending.
- Production rollout and Google E2E: pending explicit approval.

## Outcomes and retrospective

The API and web foundations are implemented, reviewed, validated, and committed.
The unapplied Flux branch contains the exact production configuration and
runbook. Local OIDC, resource, favorite, revocation, and database proofs passed.
Product favorites and login UI remain intentionally unimplemented.

No browser access-token or refresh-token storage was introduced. No production
manifest apply, Secret change, deployment, migration, sign-in, session, or
favorite mutation has occurred. Production read-only inspection and the exact
proposal are complete. Explicit approval, rollout, and Google proof remain
pending. Keep this plan active while that approval checkpoint remains open.
