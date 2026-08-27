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
invariants. Short Playwright tests own browser-visible regression coverage.
Agent-driven Browser or Chrome inspection and direct read-only PostgreSQL
queries own the local cross-layer proof. No browser test parses a credential,
queries PostgreSQL, supervises services, or reimplements UUIDv7.

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

Before explicit production approval, the allowed production actions are
read-only inspection of the PostgreSQL replica, Kubernetes resources, Flux
source files, image metadata, logs that contain no credentials, and public
anonymous endpoints. Do not edit production Flux manifests, change a Kubernetes
Secret, reconcile Flux, deploy an image, run a migration, start a production
sign-in, create a production session, or mutate a production favorite.

## Context and responsibility map

`/Users/alecgorge/code/relisten/RelistenApi/RelistenUserService` is one ASP.NET
Core process with three protocol roles. `auth.relisten.net` is the Relisten OIDC
issuer and external-provider host. `accounts.relisten.net` serves the existing
resource API under `/v1/*`. The local and production proxies route
`/auth/session/*`, `/v1/library/*`, exact `/v1/me`, and exact
`/api/user/v1/csrf` by convention. Proxy routing never grants account access.
The reviewed controllers preserve native scopes and web capabilities through
separate credential validation.

`AuthorizationController` currently creates an `identity.native_sessions` row
for every authorization. The web client cannot use that branch. A web
authorization must create no `NativeSession`, request no accounts audience, and
receive no refresh token. It may receive short-lived bootstrap credentials
inside the server-side OpenIddict client pipeline. The callback discards every
bootstrap credential after it creates the opaque web session.

`ExternalIdentityCallbackController` and the Development persona endpoints
currently use short cookie tickets as a bridge back to `/connect/authorize`.
This work replaces that bridge with a durable `auth_sso` session in
`identity.sessions`. The auth-host cookie and web-resource cookie use different
names and different hosts.

`CurrentAccountContext` is credential-neutral. Native authorization supplies a
native session identity. Browser authorization supplies a web session identity
and fixed capabilities. `GET /v1/me` returns the established account contract;
`native_session_uuid` remains present for a bearer request and is omitted for a
web-session request. Relisten never fabricates a native session for the browser.

`/Users/alecgorge/code/relisten/relisten-web` is a Timber application built with
Vite. The browser-session work belongs in Vite's development server
configuration, a small user-session client, an optional development-only test
harness, and short Playwright smoke tests. The existing public catalog client
remains separate because public catalog responses can be cached while user
responses must use `cache: "no-store"`.

`/Users/alecgorge/code/relisten/relisten-flux` contains the production User
Service, Timber, ingress, cache, and Secret references. The Flux branch exists
now, but it receives no manifest edit before approval.

## Progress

- [x] (2026-08-26) Read all three applicable `AGENTS.md` files.
- [x] (2026-08-26) Verified that each checkout was clean before branching.
- [x] (2026-08-26) Fetched `origin` in each checkout and confirmed that each
  requested base had `0` local-only and `0` remote-only commits.
- [x] (2026-08-26) Created the three requested branches in the existing
  worktrees.
- [x] (2026-08-26) Started independent read-only investigations of API
  authentication, Timber integration, and the local-to-production proxy
  boundary.
- [x] (2026-08-26) Committed this initial plan and the small link/status correction in the
  identity ExecPlan before implementation.
- [x] (2026-08-26) Proved the OpenIddict protected-state callback with both exact callback
  origins before building the session flow around it.
- [x] (2026-08-26) Added and tested durable `auth_sso` and `web` sessions.
- [x] (2026-08-26) Added and tested the confidential web OIDC client and session endpoints.
- [x] (2026-08-26) Committed the initial browser facade and focused library
  behavior before the later decision to consolidate it with `/v1/*`.
- [x] (2026-08-27) Committed the default-secure `/v1` consolidation as
  `38c17d3f3abf46dd538457bfa46fab03b150467f`. It includes the
  credential-neutral `/me` contract, simultaneous-credential rejection,
  route-family authorization convention, method-aware library authorization,
  and global cookie-mutation protection. The final focused slice passed 83
  tests; the full User Service project passed 140 tests; and the solution build
  passed with no warnings.
- [ ] Remove the abandoned comprehensive E2E and shell runner. Keep only the
  Timber HTTPS/proxy/client foundation and high-value smoke coverage.
- [ ] Make trusted local HTTPS and the confidential local client secret
  available through one setup command with no manual secret copying.
- [ ] Pass focused and broad API/web checks, then complete the agent-driven
  local browser and read-only database proof.
- [ ] Complete fresh API and web reviews, validate every finding, simplify the
  changed code, and rerun focused checks.
- [ ] Inspect production and Flux read-only, then replace the pending production
  fields below with exact files, values, evidence, and rollback commands.
- [ ] Present the production proposal and wait for explicit approval.
- [ ] After approval only: commit Flux changes, deploy approved commits, run the
  Google and favorite smoke tests, restore favorite state, and record evidence.

## Surprises and discoveries

- Observation: `RelistenUserService/Authentication/AuthorizationController.cs`
  creates a `NativeSession` without distinguishing the requesting client.
  Consequence: registering `relisten-web` before adding an explicit web branch
  would violate the native/browser credential boundary.
- Observation: `RelistenUserService/Authentication/NativePrincipalFactory.cs`
  always adds the accounts resource audience and native `sid` and
  `security_version` claims. Consequence: the web authorization branch needs a
  separate bootstrap-principal factory; omitting the `NativeSession` insert
  alone would still issue an accounts credential.
- Observation: `RelistenUserService/Authentication/AuthenticationServiceCollectionExtensions.cs`
  already uses the maintained OpenIddict client pipeline for Google and Apple.
  Consequence: the Relisten web client can use the same maintained client
  machinery for state, correlation, nonce, code exchange, and token validation.
- Observation: the OpenIddict client is currently registered only when external
  Google or Apple providers are enabled. Consequence: register the Relisten web
  self-client in both supported runtime profiles, while keeping Google and Apple
  registrations exclusive to the external-provider profile.
- Observation: the User Service currently enables ASP.NET antiforgery only with
  Development personas. Consequence: browser antiforgery registration and
  validation must be independent of the upstream identity provider profile.
- Observation: `relisten-web/vite.config.ts` sets `strictPort: false` and has no
  HTTPS or proxy configuration. Consequence: the browser origin is not stable
  enough for an exact OIDC redirect URI or `__Host-` cookie until the Vite
  configuration changes.
- Observation: Vite 8 skips its built-in `allowedHosts` validation when the
  development server uses HTTPS. Consequence: `server.allowedHosts` alone
  cannot enforce the required development host allowlist; a small development
  server middleware must reject every Host except
  `web.relisten.localhost:5173` before Timber handles the request.
- Observation: Vite proxy keys use prefix matching. Consequence: literal keys
  such as `/auth/session` would also proxy `/auth/session-evil`; use
  segment-aware regular expressions for the two reviewed prefixes.
- Observation: `HostBoundaryMiddleware` returns no expected host for an unknown
  path. Consequence: both new browser prefixes would accept any Host until they
  receive explicit host rules, and the raw backend Host must be checked before
  external-origin reconstruction.
- Observation: `relisten-web/package.json` has no browser-test command or
  Playwright dependency. Consequence: the web branch must add a focused test
  dependency and orchestration instead of relying on a manual sign-in.
- Observation: the older identity ExecPlan calls `relisten-web` a Next.js app,
  but the active branch uses Timber and Vite. Consequence: correct only those
  stale statements and link this more specific plan.
- Observation: a separate `/api/user/v1/*` resource facade duplicates the
  established `/v1/*` controllers and requires every future library feature to
  be wired twice. Consequence: share only explicitly reviewed `/v1` actions,
  while native scopes and persisted web capabilities remain separate
  authorization paths.
- Observation: the first local E2E draft grew to 1,026 lines of Playwright and
  a 173-line shell runner. It supervised processes, called `psql`, parsed the
  opaque cookie, hashed its validator, generated UUIDv7 values, intercepted
  callbacks, and scanned logs. Consequence: delete that draft instead of
  polishing it. Each proof belongs at the narrowest layer that can observe the
  failure.
- Observation: action-level antiforgery filters and exact proxy route lists make
  every new library operation depend on repeated security wiring. Consequence:
  route reviewed families by convention, authorize library reads and mutations
  from the request method at the controller boundary, and enforce cookie
  mutation protection once in middleware.
- Observation: endpoint-count and attribute-shape tests can pass while request
  behavior is wrong, and they fail during harmless route refactors. Consequence:
  retain a test only when it names a concrete security failure or compatibility
  contract. The current consolidation deletes the exact browser-surface count,
  exact four-route shape, and startup-migration option echo tests. Behavioral
  tests continue to cover native `/me`, web `/me`, credential ambiguity,
  capability selection, CSRF and Origin enforcement, host and relay boundaries,
  session lifecycle, and native compatibility.

Add new observations with their evidence. Do not record a guess as evidence.

## Decision log

- Decision: use one `identity.sessions` table for the `auth_sso` and `web`
  purposes. Rationale: both purposes share validator verification, user and
  `security_version` checks, revocation, and expiry, while explicit purpose and
  capability checks prevent credential substitution. Date: 2026-08-26.
- Decision: encode the cookie credential as a versioned opaque value containing
  a UUIDv7 session ID and a random 256-bit validator. Store only the SHA-256
  validator hash. Rationale: a database read alone cannot authenticate a stolen
  session row. Date: 2026-08-26.
- Decision: separate credential encoding, database lifecycle, authentication,
  and authorization into distinct types. Rationale: each type owns one security
  invariant and can be tested without a generic authentication framework. Date:
  2026-08-26.
- Decision: represent web capabilities as a fixed flags value with only account
  profile read, library read, and favorite mutation. `auth_sso` has no resource
  capability. Rationale: a new bearer endpoint must not become browser-accessible
  through scope translation or naming convention. Date: 2026-08-26.
- Decision: configure one maintained OpenIddict client registration per exact
  external callback origin when the client library requires one redirect URI per
  registration. Both registrations use the same confidential `relisten-web`
  server application. Rationale: OpenIddict state selects the registration and
  keeps callback validation enabled for concurrent local and canonical flows.
  The protected-state spike confirmed this decision. Date: 2026-08-26.
- Decision: use `https://web.relisten.localhost:5173` as the only browser origin
  in development. Use exact `auth.relisten.localhost` and
  `accounts.relisten.localhost` hosts for local protocol boundaries. Rationale:
  exact HTTPS origins allow Secure host-only cookies without credentialed CORS
  or `SameSite=None`. Date: 2026-08-26.
- Decision: let the Timber development proxy overwrite
  `X-Relisten-Web-Origin`. The API accepts the header only on the exact session
  and facade prefixes, from a configured backend host, for an exact allowlisted
  HTTPS origin including its port. Rationale: the API needs the browser-visible
  callback URI behind the local proxy, but an arbitrary forwarded host would
  bypass redirect and origin validation. Date: 2026-08-26.
- Decision: protect browser mutations with both ASP.NET antiforgery and an exact
  `Origin` check. Bind antiforgery tokens to the web session ID. Rationale: a
  token from an old or different authenticated session must fail even when its
  antiforgery cookie remains. Date: 2026-08-26.
- Decision: perform database revocation for the current web session and linked
  auth-SSO session on the CSRF-protected web-host POST. Use a bounded auth-host
  redirect only to expire the auth-host cookie after revocation. Rationale: the
  redirect must not turn a cross-site GET into the authoritative revocation
  operation. Date: 2026-08-26.
- Decision: keep Development personas and external Google/Apple providers as
  separate runtime profiles. Rationale: a fixed persona must be impossible to
  enable outside ASP.NET Core `Development`. Date: 2026-08-26.
- Decision: serve reviewed browser resources from the established `/v1/me` and
  `/v1/library/*` actions. A bearer request must pass native-session and scope
  validation. A cookie request must pass web-session and capability validation.
  Rationale: mobile and Timber must share one resource contract and one domain
  path without making every `/v1/*` action browser-accessible. Date: 2026-08-26.
- Decision: reject a request that presents both an Authorization credential and
  `__Host-relisten_session`. Rationale: the User Service must never choose an
  identity by handler order when two credentials are present. Date: 2026-08-26.
- Decision: apply session-bound antiforgery and exact Origin checks only when a
  reviewed mutation uses `__Host-relisten_session`. Rationale: browser cookies
  are ambient credentials; native bearer credentials are explicit and must
  retain the existing mobile request contract. Date: 2026-08-26.
- Decision: do not require comprehensive automated cross-layer E2E. Keep
  Playwright limited to short browser-visible smoke tests. Use focused API tests
  for database and security invariants, and use Browser or Chrome DevTools plus
  direct read-only PostgreSQL inspection for the local acceptance proof.
  Rationale: the rejected test duplicated product and infrastructure code and
  made the proof less trustworthy. Date: 2026-08-27.
- Decision: route `/auth/session/*`, `/v1/library/*`, exact `/v1/me`, and exact
  `/api/user/v1/csrf` through the development proxy by convention. Keep API
  authorization authoritative. Rationale: a new library route must not require
  a second proxy edit, and proxy reachability must not imply browser access.
  Date: 2026-08-27.
- Decision: apply method-aware read or write authorization at the library
  controller boundary. Apply session-bound antiforgery and exact Origin checks
  in one global boundary for every unsafe request that carries
  `__Host-relisten_session`. Rationale: a developer must not need to remember a
  per-action authorization or antiforgery filter. Date: 2026-08-27.
- Decision: provide one local setup command that creates a trusted certificate
  and confidential client secret outside Git. Use the fixed HTTPS hosts and
  port without manual secret copying. Rationale: every developer must get the
  same secure-cookie origin with actionable setup failures. Date: 2026-08-27.

## API milestones

### A1. Prove maintained callback state before session implementation

The completed focused spike in
`/Users/alecgorge/code/relisten/RelistenApi/RelistenUserServiceTests` configured
the OpenIddict server and client with the two exact callbacks:

    https://relisten.net/auth/session/callback
    https://web.relisten.localhost:5173/auth/session/callback

The spike proved protected relative `return_to` restoration, exact registration
selection, one-time callback state, and independent concurrent correlation
cookies. It did not decode an OIDC response or disable redirect validation.
Maintained OpenIddict tests and the focused configuration regression own those
protocol guarantees; Playwright will not reproduce the protocol client.

Keep OpenIddict client token storage enabled because it supplies one-time state
redemption. Let the client pipeline own its per-challenge correlation cookie,
PKCE verifier, nonce, and 15-minute state-token lifetime.

### A2. Persist and authenticate durable sessions

Add an entity such as
`RelistenUserService/Identity/Entities/IdentitySession.cs` and its focused EF
configuration under `RelistenUserService/Persistence/Configurations/`. Map it
to `identity.sessions`. The final row must contain:

- UUIDv7 `id` and owning `user_id`;
- purpose `auth_sso` or `web`;
- exactly 32 bytes of `validator_hash` and no raw validator column;
- captured `security_version`;
- `authenticated_at`, `created_at`, `updated_at`, and `last_seen_at`;
- `sliding_expires_at` and `absolute_expires_at`;
- nullable `revoked_at` and a bounded revocation reason if operations need it;
- nullable parent `auth_sso_session_id`, required only for `web`;
- nullable exact `web_origin`, required only for `web`;
- a fixed capability flags value. `auth_sso` must have no resource capability.

Use database check constraints for UUIDv7, purpose-specific nullability, hash
length, expiration order, capability bits, and exact parent/origin requirements.
Use a self-reference that does not cascade-delete session evidence.

Create a credential codec that generates 32 random bytes with a cryptographic
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

Generate the EF migration with the repository's configured context, inspect the
generated SQL and model snapshot, then test the migration against local
PostgreSQL. Do not add the confidential client secret to a migration.

### A3. Replace the temporary identity bridge and add the real web OIDC client

Refactor the external-provider callback and Development persona completion to
call the same external-identity completion service and then create an
`auth_sso` row plus `__Host-relisten_auth`. The cookie is Secure, HttpOnly,
SameSite=Lax, Path `/`, and has no Domain. Development personas remain available
only when the host environment is `Development` and the explicit persona option
is enabled.

Register `relisten-web` as a confidential authorization-code client that
requires S256 PKCE. Provision its secret from local configuration and, after
approval, a deployment Secret reference. Never commit, log, or send the secret
to Timber. The server registration permits both exact callbacks and only the
scopes needed to return a validated subject and account profile. It permits no
`offline_access` and no accounts resource.

Change `AuthorizationController` to branch on the validated web client ID.
Native clients keep the existing `NativeSession`, authorization, audience,
scope, access-token, and rotating refresh-token behavior. The web client creates
no `NativeSession` and receives no accounts-resource audience or refresh token.
It receives only the claims needed by the server-side web callback, including a
validated subject and a link to the authenticated auth-SSO session.
Build that principal with a separate web-bootstrap principal factory. Do not
reuse `NativePrincipalFactory`, because that factory always adds the accounts
resource audience and native session claims.

Add these endpoints:

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
origin. Switch-account uses the same cleanup and restarts authorization with
provider account selection. Each host expires only the cookie it owns.

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
callback, the CSRF response, and every reviewed `/v1` resource response. Do not
add credentialed CORS and do not configure `SameSite=None`.

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
certificate or private key. The setup documentation will use a local directory
outside all repositories, for example:

    /Users/alecgorge/Library/Application Support/Relisten/local-tls

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

Add a development-only diagnostic route or harness that lets Playwright start
sign-in, invoke the typed client, and show status without rendering credentials
or personal data. Production builds must omit or return 404 for the diagnostic
route. Do not add product favorites UI or login styling.

### W4. Keep browser automation small

Delete `scripts/run-browser-session-e2e.sh` and the abandoned comprehensive
Playwright draft. Do not replace them with Playwright `webServer`, another shell
supervisor, raw HTTPS helpers, PostgreSQL calls, cookie parsing, custom UUIDv7,
callback interception, or log scanning.

Keep focused unit tests for the proxy boundary and typed client. Keep at most a
short Playwright smoke test for browser-visible behavior that is expensive to
regress manually: development-persona sign-in through the real OIDC redirect,
one authenticated read, and logout. A favorite round trip belongs in the smoke
test only if it stays short and restores state in `finally`. Browser tests use
already-running local services and normal development setup.

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

Use a trusted certificate for these exact names:

    web.relisten.localhost
    auth.relisten.localhost
    accounts.relisten.localhost

Keep certificate files and the confidential local client secret outside the
repositories. Provide one idempotent setup command that installs or verifies a
standard trusted local CA, creates the exact host certificate, and creates the
client secret without printing it. Vite and Kestrel use stable default paths in
the developer's application-support directory. A missing tool, failed trust
installation, wrong file permission, or missing certificate must produce an
actionable error. Add ignore rules for certificate and local-secret file names
even though the documented files live outside the repositories.

From `/Users/alecgorge/code/relisten/RelistenApi`:

    ./start-local-databases.sh
    dotnet restore RelistenApi.sln
    dotnet build RelistenApi.sln

Expected result: PostgreSQL answers on `127.0.0.1:15432`; restore and build exit
`0`. Run the documented User Service development command with Development
personas, the exact HTTPS issuer and hosts, and the generated external files. It
must not seed a web session.

From `/Users/alecgorge/code/relisten/relisten-web`:

    pnpm install --frozen-lockfile
    pnpm setup:browser-session
    pnpm dev

In a separate terminal, run the optional short Playwright smoke command. The
agent-driven acceptance proof uses Browser or Chrome against the same running
services. Expected result: one real browser completes the same
external-identity completion path used by Google, then receives a
database-backed opaque session.

### Behavioral acceptance criteria and proof ownership

Focused API tests must establish these durable invariants:

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
9. Maintained OpenIddict state rejects missing correlation and replay while
   concurrent challenges remain independent.

The short Playwright smoke and agent-driven Browser or Chrome proof must
establish browser-visible behavior: Development-persona sign-in completes
through the real OIDC flow, the web cookie has the required metadata, `/v1/me`
and library reads succeed, and logout removes authenticated access. A favorite
round trip may be agent-driven. It records the initial state and restores it.

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

    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj --filter <browser-session-filter>
    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
    dotnet test RelistenApiTests/RelistenApiTests.csproj
    dotnet build RelistenApi.sln

Replace the filter placeholder with the final focused filter. Each command must
exit `0`. Do not rerun an unchanged failing command. Diagnose the failure, make
a code or environment change, and then rerun the smallest affected check.

Run from `/Users/alecgorge/code/relisten/relisten-web`:

    pnpm typecheck
    pnpm lint
    pnpm build
    pnpm test:browser-session

The browser-session command runs only short smoke tests against already-running
services. Each command must exit `0`. Record test counts and relevant assertions
without recording credentials or personal data.

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

Stop here after the local/local proof and final reviews pass. Update this plan
with exact evidence, then inspect production and Flux read-only. The approval
proposal must replace every pending item below with repository evidence:

- Exact Flux files and fields: **pending read-only inspection after local proof**.
- Exact ingress objects, paths, path types, and precedence for
  `/auth/session/*`, `/api/user/v1/csrf`, and the reviewed `/v1` resource
  routes: **pending**.
- Exact cache configuration and bypass behavior for both path prefixes:
  **pending**.
- Exact User Service image tag or digest fields: **pending**.
- Exact configuration keys for canonical and local web origins, exact callback
  URIs, proxy backend hosts, cookie behavior, and runtime provider profiles:
  **pending**.
- Exact existing Secret resource and new key references for the confidential
  web client: **pending**. The proposal will describe a no-output generation
  command and an input path that does not expose the value to logs or Git.
- Exact migration mechanism and replica count/surge behavior: **pending**.
- Expected production database writes: the reviewed migration, OpenIddict
  confidential-client registration or update, auth-SSO and web session rows from
  the approved sign-in, OpenIddict authorization/token rows, and temporary
  favorite/change/receipt rows needed by the smoke test. The final proposal must
  confirm the exact table set from code and read-only schema inspection.
- Exact Google and favorite smoke-test steps: **pending**. The test will record
  the chosen favorite's initial state and restore it before completion.
- Health checks, rollout observations, public route checks, log checks, and
  database success queries: **pending**.
- Exact rollback commits, image/config reversion, migration compatibility, and
  post-rollback checks: **pending**.

Present the completed proposal to the repository owner and ask for explicit
approval. Do not interpret silence, review comments, or approval of local code
as production approval. If approval is not received, leave all local work
committed, report production E2E as pending approval, and keep the goal active.

## Production rollout and rollback

This section is intentionally non-executable until the approval checkpoint is
complete and approved.

After approval, edit only the approved fields on
`codex/web-session-foundation-flux`. Follow existing Flux Secret-reference and
image conventions. Never commit a secret value. Commit the Flux change as one
passing logical unit.

Deploy only approved commits. Observe the User Service rollout and health before
starting any browser flow. Confirm that anonymous catalog and Timber routes
remain healthy. Confirm that the longer session/facade prefixes reach the User
Service before the Timber catch-all and bypass every public cache.

For the production-backed Google test, load the Chrome-control skill and use the
user's explicit Chrome browser with its existing signed-in Google session. Do
not request, inspect, reveal, or handle Google credentials. Do not inspect
Google cookies or storage. Inspect only the Relisten app origin to prove that it
contains no access or refresh token.

Run this approved path:

    local Timber
      -> local development proxy
      -> production relisten.net session routes
      -> production Relisten issuer
      -> Google
      -> local Timber callback
      -> production opaque web session

Prove `/me`, library snapshot, library changes, favorite add/observe/remove or
the inverse needed to restore the initial state, mutation idempotency, and
logout. Run the canonical-host smoke test if the approved deployment exposes
the route without adding product UI. Confirm that no synthetic favorite remains.

Rollback must first prevent new session traffic from reaching incompatible
code. Revert the exact ingress/cache/config/image changes named in the approved
proposal. Keep the additive session table unless the reviewed migration proves
that removal is safe and the owner separately approves destructive rollback.
Revoke test sessions if the rollback runbook requires it. Recheck anonymous
catalog, Timber, auth discovery, User Service health, and the absence of cached
user responses. Record every rollback command before deployment, not during an
incident.

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

- `78435dc046dd01c8ff86cd294be1c74cc9c40319`: initial browser-session
  ExecPlan plus identity-plan link/status correction. `git diff --cached
  --check` passed before commit.
- `0705bb5a73319f31fb333a95a3fedf482e5cfd7b`: durable session schema,
  credential codec, and lifecycle. The full User Service test project passed
  with 64 tests. EF reported no pending model changes after the migration.
- `5e3ea68120f2c41f3c07bf23991fa270f6305467`: confidential web OIDC,
  auth-SSO, and browser-session lifecycle. The full User Service test project
  and solution build passed. The local HTTPS protected-state spike completed
  the callback and rejected callback replay.
- `8e480429436f57e2bf1a0b325ccc7b052b48bd71`: initial browser-specific
  profile, library, favorite, and CSRF facade. Focused profile/library tests and
  the full User Service test project passed before the later shared-`/v1`
  consolidation decision.
- `38c17d3f3abf46dd538457bfa46fab03b150467f`: consolidated the reviewed
  resources onto `/v1`, added credential ambiguity rejection, applied
  method-aware native-scope or web-capability authorization, and enforced
  cookie mutation protection globally. The affected suite passed 83 tests, the
  full User Service project passed 140 tests, and the solution build passed
  with no warnings.
- Shared-`/v1` authorization test: `TestReviewedAccountAccessAuthorization`
  now passes 3 tests. The final affected boundary filter passed 83 tests. The
  full User Service project passed 140 tests. `dotnet build RelistenApi.sln
  --no-restore` passed with no warnings or errors.
- Fresh default-secure API review: accepted and fixed the native uppercase-host
  compatibility defect and the missing route-family authorization convention.
  A focused endpoint-convention test proves that new `/v1/library/*` and
  `/auth/session/*` controller actions inherit policy while
  `/v1/library-evil` and unrelated `/v1/*` do not. The proposed full-host
  pipeline fixture was not added because the short real-browser smoke will
  exercise the deployed middleware order without recreating the rejected test
  orchestrator.
- Timber graph commands: pending.
- Timber typecheck, lint, and build: pending.
- Short local Playwright smoke: pending after the abandoned comprehensive draft
  is deleted.
- Agent-driven local Browser or Chrome and direct read-only database proof:
  pending.
- Final API review: pending after the web boundary is stable. The current API
  consolidation received one fresh read-only review and incorporated its two
  validated code findings.
- Fresh web/E2E review and accepted findings: pending.
- API code simplification: removed E2E-only logging residue, shared segment
  predicates between reachability and authorization, and exercised both read
  and write policy branches. Post-simplification focused and full checks passed.
  The final cross-repository simplification pass remains pending.
- Production read-only inspection: pending until local/local proof passes.
- Production approval: not requested yet.
- Production rollout and Google E2E: pending explicit approval.

## Outcomes and retrospective

The three feature branches exist. The API branch contains five logical commits
for the plan, durable persistence, OIDC lifecycle, initial browser facade, and
default-secure shared resources. The API worktree is clean. The web worktree
contains uncommitted development work. No production Flux manifest edit, Secret
change, deployment, production migration, production sign-in, production
session, or production favorite mutation has occurred.

Replace this paragraph with the final outcome, residual risks, lessons, and
remaining UI work after all authorized work is complete. Move the plan only when
its approved scope is finished. If production approval remains pending, keep the
plan active and describe production proof as pending approval.
