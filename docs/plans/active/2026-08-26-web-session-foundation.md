# Implement durable browser sessions for Relisten web

This file is the execution source of truth for the browser-session work in
RelistenApi, relisten-web, and relisten-flux. Keep the progress, decisions,
evidence, and approval status current. Do not use this plan as an iteration log.

## Purpose and user-visible outcome

Relisten web will authenticate through the existing Relisten OpenID Connect
(OIDC) server and receive one opaque, PostgreSQL-backed session cookie. Browser
JavaScript will never receive or store an access token or refresh token.
PostgreSQL will store only SHA-256 validator hashes.

The first Timber slice provides authentication and library access, not product
UI. A small typed client can read the current account, read the library snapshot
and changes, submit idempotent favorite mutations with antiforgery protection,
and sign out. The local path is:

    local Timber
      -> local User Service
      -> Development persona
      -> Relisten authorization code with S256 PKCE
      -> local Timber callback
      -> opaque web session

Focused API tests own protocol, session, authorization, CSRF, and data-contract
invariants. Vitest owns pure Timber client and proxy behavior. Playwright owns
one short browser-visible smoke against already-running services. Agent-driven
Browser or Chrome inspection and direct read-only PostgreSQL queries own the
cross-layer proof. No browser test supervises services, parses credentials,
queries PostgreSQL, scans logs, or implements UUIDv7.

Production remains read-only until the repository owner approves the exact
proposal below. A production Google sign-in creates production rows and also
waits for approval.

## Authority and repository boundaries

Use only these existing worktrees and branches:

- API: /Users/alecgorge/code/relisten/RelistenApi,
  codex/web-session-foundation-api, based on master at
  a30c02e928bad97ceb1c0fa5eaeb8e3ee1afec5c.
- Web: /Users/alecgorge/code/relisten/relisten-web,
  codex/web-session-foundation-web, based on timber-migration-v1 at
  16775dc1aa40c6cf0739e771c164b75280da7c36.
- Flux: /Users/alecgorge/code/relisten/relisten-flux,
  codex/web-session-foundation-flux, based on main at
  7488a952bb4ab7d174ce08e2d46c692ccb166033.

Do not create a Git worktree. Do not stash, reset, discard, or overwrite user
work. Verify the active branch before every edit and commit. The primary agent
owns edits, tests, branch changes, and commits. Review agents remain read-only.

Before explicit production approval, local Flux branch edits and production
read-only inspection are allowed. Do not apply manifests, change Kubernetes
Secrets, reconcile Flux, deploy an image, run a production migration, start a
production sign-in, create a production session, or mutate a production
favorite.

## Responsibility map

RelistenUserService is one ASP.NET Core process with separate protocol roles.
auth.relisten.net is the OIDC issuer and external-provider host.
accounts.relisten.net serves the resource API. The local and production proxies
route these same-origin browser paths:

    /auth/session/*
    /api/user/v1/csrf
    /v1/me
    /v1/library/*

Proxy reachability never grants account access. Reviewed resource controllers
accept either a native bearer credential or an opaque web-session cookie.
Native requests require native scopes. Web requests require fixed persisted
capabilities. Requests that present both credentials fail before identity
selection. Cookie-authenticated unsafe requests also require the exact Origin
and a session-bound antiforgery token.

AuthorizationController keeps native and web issuance distinct. A web
authorization creates no NativeSession, receives no accounts audience, and
receives no refresh token. OpenIddict owns state, correlation, nonce, PKCE,
code exchange, token validation, and redirect validation.

CurrentAccountContext is credential-neutral. GET /v1/me includes
native_session_uuid for native bearer authentication and omits the property for
web authentication. The browser never receives a fabricated native session.

Timber owns only local HTTPS and proxy setup, a small typed browser client, a
Development-only diagnostic page, focused unit tests, and one short smoke. It
does not own resource tokens, global auth state, or a generic API SDK.

The unapplied Flux branch adds configuration and six route entries to the
existing resources. It does not add a second ingress, cache middleware, or a
deployment workflow.

## Progress

- [x] 2026-08-26: Read applicable instructions, verified clean and
  non-diverged bases, created all three branches, and committed the initial
  ExecPlan.
- [x] 2026-08-27: Implemented durable auth-SSO and web sessions, confidential
  web OIDC, browser lifecycle endpoints, shared reviewed resource routes, and
  default-safe cookie mutation protection.
- [x] 2026-08-27: Organized RelistenUserService/Authentication by
  responsibility and implemented one-command local certificate and client
  secret setup.
- [x] 2026-08-27: Added Timber HTTPS, exact proxy families, the typed client,
  focused Vitest coverage, and one short Playwright smoke. Removed the bespoke
  cross-layer runner and custom UUIDv7 test code.
- [x] 2026-08-27: Completed a local Browser and read-only PostgreSQL proof for
  sign-in, account and library reads, favorite add/replay/remove, CSRF failures,
  route isolation, logout, hash-only persistence, and token-storage absence.
- [x] 2026-08-27: Fixed Development-persona form concurrency, redirect URL
  logging, and first-token antiforgery concurrency.
- [x] 2026-08-27: Tested the proposed different-account race before adding new
  persistence. Two preloaded tabs selected different personas concurrently and
  produced one matching auth-SSO and web session for each account. The alleged
  account mix-up did not reproduce, so no handoff table, credential, cookie, or
  migration was added.
- [x] 2026-08-27: Ran final focused and broad API and Timber checks. Fresh
  reviews found no runtime defect. One accepted regression gap added focused
  expiry and disabled-user tests. The code-simplifier pass found no safe
  production-code reduction.
- [ ] Receive explicit production approval for the exact proposal below.
- [ ] After approval only: deploy the approved API and Flux commits, run Google
  and favorite smoke tests, restore favorite state, and record evidence.

## Surprises and discoveries

- The old authorization path always created a NativeSession and accounts
  audience. The web client therefore needs a separate bootstrap principal.
- A separate browser facade duplicated established /v1 contracts. Reviewed
  account and library actions now share controllers while keeping credential
  validation distinct.
- Per-action proxy and antiforgery declarations were easy to omit. Segment-safe
  proxy families provide reachability, controller policies provide resource
  authority, and one global middleware protects unsafe cookie requests.
- Vite 8 does not apply its built-in Host check under HTTPS, and its proxy keys
  use character-prefix matching. Timber therefore uses an exact Host middleware
  and segment-aware proxy matchers.
- The Development persona form could race on a shared antiforgery cookie. The
  Development-only POST now requires the exact auth Origin instead.
- Priming antiforgery only on the first CSRF request let two concurrent requests
  issue incompatible cookie and token pairs. The session callback now primes
  the cookie before returning to Timber.
- Static inspection suggested that two identity completions might race on the
  global auth-SSO cookie. The current local Browser path did not expose that
  interleaving: concurrent different-persona selections created distinct,
  correctly linked web sessions. A new credential and table are not justified
  without a deterministic failure.
- The User Service already sends private, no-store for browser lifecycle and
  account responses. No ingress cache component is needed.
- Production PostgreSQL 17.10 supports UUIDv7 inspection and currently has no
  identity.sessions table or relisten-web client. The first approved API
  rollout will add them.

## Decision log

- 2026-08-26: Use identity.sessions for auth_sso and web. A session credential
  contains a versioned UUIDv7 ID and random 256-bit validator. Persist only the
  SHA-256 validator hash.
- 2026-08-26: Persist exactly account-profile-read, library-read, and
  favorite-mutation capabilities for web sessions. auth_sso has no resource
  capability.
- 2026-08-26: Keep OpenIddict redirect validation, state, correlation, nonce,
  S256 PKCE, code exchange, and token validation enabled. Development personas
  remain unavailable outside Development.
- 2026-08-26: Use https://web.relisten.localhost:5173 as the only local browser
  origin. Timber overwrites X-Relisten-Web-Origin, and the User Service accepts
  it only from configured backend hosts on reviewed path families.
- 2026-08-26: Require exact Origin and session-bound antiforgery for every
  unsafe request carrying the web-session cookie. Native bearer mutations keep
  their existing contract.
- 2026-08-26: Share GET /v1/me and /v1/library/* between native and web
  credentials. Reject simultaneous credentials. Keep lifecycle and CSRF-token
  endpoints browser-specific.
- 2026-08-27: Route auth and library families by convention. API policy and
  persisted capability checks remain authoritative.
- 2026-08-27: Retain a test only when it names a concrete failure mode or
  compatibility contract. Playwright remains a short smoke, not a cross-layer
  orchestrator.
- 2026-08-27: Use one idempotent local setup command. Store trusted local
  certificates and client secrets outside Git.
- 2026-08-27: Do not add an authorization-handoff table, credential, cookie, or
  lifecycle for a race that the real two-persona Browser path did not
  reproduce. Revisit flow-specific binding only after a deterministic failing
  case shows account confusion.
- 2026-08-27: Use the existing web Ingress and image workflow. After approval,
  configure, deploy, verify, expose, and smoke. Production writes remain
  approval-gated.

## API milestones

### A1. Persist and authenticate durable sessions

identity.sessions contains UUIDv7 identity, user ownership, purpose,
validator_hash, captured security_version, authentication and activity times,
sliding and absolute expiry, revocation, parent auth-SSO identity, exact web
origin, and fixed capabilities. Database constraints enforce purpose-specific
shape, 32-byte hashes, timestamp order, exact origin, and same-user parent
linkage.

SessionCredentialCodec owns credential generation, strict versioned parsing,
SHA-256 hashing, and constant-time comparison. IdentitySessionLifecycle owns
creation, validation, hourly touch, and linked revocation. Authentication
handlers read only their named cookie and required purpose. Authorization
policies own capabilities.

Every authenticated request rejects a wrong validator or purpose, revocation,
sliding or absolute expiry, an inactive user, a changed security_version, an
invalid parent, or unexpected capabilities. Web sessions slide for 30 days,
update at most hourly, and stop at 180 days. Auth SSO lasts 30 days.

Migration 20260827052217_AddDurableBrowserSessions contains no client secret.

### A2. Complete external identity without a parallel flow framework

Google/Apple completion and Development persona completion use the same
external-identity service, create the same durable auth-SSO session, set the
same host-only auth cookie, and redirect the exact protected
/connect/authorize target. OpenIddict continues to own the protected client
state and callback correlation. Relisten does not parse or rebuild those
protocol values.

The local scope test preloaded two authorization tabs, selected Alice and
Shared Google concurrently, and observed one auth_sso plus one linked web row
for each persona. Since no account confusion occurred, keep the existing
straight-line completion path. Do not add flow-specific persistence or cookies
unless a deterministic failing case proves the current path loses the selected
identity.

### A3. Preserve native issuance and add web lifecycle

Register relisten-web as a confidential authorization-code client with S256
PKCE and only these callbacks:

    https://relisten.net/auth/session/callback
    https://web.relisten.localhost:5173/auth/session/callback

The web branch creates no NativeSession, accounts audience, or refresh token.
The server-side callback authenticates through OpenIddict, requires the exact
issuer and web-client audience, validates sub, loads the active user and
auth-SSO link, creates a fresh web session, sets the web cookie, primes the
antiforgery cookie, and discards every bootstrap token.

Browser lifecycle endpoints:

    GET  /auth/session/start?return_to=<relative application path>
    GET  /auth/session/callback
    POST /auth/session/logout
    POST /auth/session/switch-account

return_to must be a relative application path with no authority, scheme,
backslash, control character, or protocol-relative form. Logout and
switch-account revoke the web session and linked auth-SSO transactionally.
Each host clears only its own cookie through the bounded auth-host return.

### A4. Enforce cookie, CSRF, origin, host, and cache boundaries

Exact cookies:

    __Host-relisten_auth
    __Host-relisten_session
    __Host-relisten_csrf

Authentication cookies are Secure, HttpOnly, SameSite=Lax, Path /, and
host-only. GET /api/user/v1/csrf returns a session-bound request token and sets
the antiforgery cookie. Every unsafe request carrying the web-session cookie
requires X-Relisten-CSRF and the exact expected Origin. Missing Origin fails.
Changing the web session invalidates old request tokens.

All /auth/session/*, the auth-host cookie-clear response,
/api/user/v1/csrf, /v1/me, and segment-safe /v1/library/* responses use
Cache-Control: private, no-store. Do not add credentialed CORS or SameSite=None.

The API may honor X-Relisten-Web-Origin only on those exact path families, when
the actual backend Host is configured, and when the supplied value is an exact
allowlisted HTTPS origin including port. Never trust browser-supplied forwarded
headers or disable OpenIddict redirect validation.

### A5. Share reviewed resources safely

Browser-capable resource actions:

    GET  /v1/me
    GET  /v1/library/snapshot
    GET  /v1/library/changes?after=<cursor>
    POST /v1/library/favorite-mutations:batch

The library controller applies method-aware read/write authorization to every
action. A global cookie-mutation boundary supplies CSRF and Origin protection.
Unreviewed /v1/*, Sonos, adapter, internal, and playback-control routes remain
bearer-only. New /v1/library/* actions reach the API through the proxy but stay
unavailable to web sessions until their policy and capability are reviewed.

## Timber milestones

### W1. Local HTTPS and exact proxy reachability

Run once:

    cd /Users/alecgorge/code/relisten/relisten-web
    pnpm install --frozen-lockfile
    pnpm setup:browser-session

The setup command generates and trusts one local certificate for exactly
web.relisten.localhost, auth.relisten.localhost, and
accounts.relisten.localhost. It stores certificates and the confidential local
client secret under Library/Application Support outside Git and writes User
Service configuration through .NET Secret Manager.

Vite serves exactly https://web.relisten.localhost:5173 with strictPort enabled
and exact Host middleware. The Development proxy uses segment-safe matches for:

    /auth/session/*
    /api/user/v1/csrf
    /v1/me
    /v1/library/*

The target is either local User Service or, only after approved rollout,
https://relisten.net. The proxy preserves method, query, body, cookies,
redirects, Origin, and every Set-Cookie header. It deletes browser-supplied
X-Relisten-Web-Origin and forwarded headers, then writes the fixed Timber
origin. It does not proxy /auth/session-evil, /v1/library-evil, or unrelated
/v1/*.

### W2. Small client and smoke coverage

The typed client uses relative URLs, credentials: include, and cache: no-store.
Mutation methods acquire a CSRF request token and attach X-Relisten-CSRF. The
public catalog client remains separate.

The Development-only diagnostic route contains only redacted test controls.
Vitest covers client and proxy behavior. Playwright covers Development-persona
sign-in, one authenticated read, and logout against services the developer
already started. It does not supervise services or emit traces, videos,
screenshots, request dumps, callback URLs, credentials, tokens, or personal
data.

Before editing Timber-owned boundaries, consult installed Timber documentation
and run:

    npx timber graph <relevant-file> --json

## Local/local E2E proof

### Local setup

Start the local databases and User Service:

    cd /Users/alecgorge/code/relisten/RelistenApi
    ./start-local-databases.sh
    dotnet run --project RelistenUserService/RelistenUserService.csproj

Expected: PostgreSQL answers on 127.0.0.1:15432, the User Service listens on
https://127.0.0.1:5443, Development personas are enabled, and startup does not
seed a web session.

Start Timber:

    cd /Users/alecgorge/code/relisten/relisten-web
    pnpm install --frozen-lockfile
    pnpm setup:browser-session
    pnpm dev

Expected: Timber listens only on https://web.relisten.localhost:5173.

### Behavioral acceptance criteria and proof ownership

Focused API tests must prove:

1. UUIDv7 session identity, a random 256-bit validator, hash-only persistence,
   constant-time comparison, purpose validation, expiry, user status,
   security_version, hourly touch, parent linkage, and linked revocation.
2. Native authorization behavior is unchanged. Web authorization creates no
   native session, accounts audience, or refresh token.
3. Bearer /v1/me includes native_session_uuid. Web /v1/me omits it.
4. Native and web reads and mutations require their distinct permissions.
   Simultaneous credentials fail. Unreviewed routes remain browser-inaccessible.
5. Unsafe cookie requests require exact Origin and session-bound antiforgery.
   Native bearer mutations do not acquire browser CSRF.
6. Favorite replay is idempotent and changes are observable.
7. Authentication cookies have their exact secure attributes.

The short Playwright smoke proves browser-visible Development sign-in, one
browser-safe authenticated read, and logout.

The maintained callback spike and agent-driven Browser or Chrome proof own the
full OpenIddict flow, callback replay and correlation failures, and concurrent
different-persona completion. Browser inspection plus direct read-only
PostgreSQL inspection also prove /v1/me, snapshot, changes, favorite
add/replay/remove with initial state restored, CSRF failures, route isolation,
linked revocation, hash-only persistence, no web-created native session, and no
token in app-origin storage, rendered data, visible URLs, or sanitized logs.
Do not inspect or record a cookie value, validator, token, callback query, or
personal field.

### Validation commands

From /Users/alecgorge/code/relisten/RelistenApi:

    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj --no-restore \
      --filter "FullyQualifiedName~TestSessionCredentialCodec|FullyQualifiedName~TestIdentitySessionLifecycleIntegration|FullyQualifiedName~TestWebSessionAntiforgery|FullyQualifiedName~TestWebRequestBoundaries|FullyQualifiedName~TestBrowserFacadeBoundary|FullyQualifiedName~TestReviewedAccountAccessAuthorization|FullyQualifiedName~TestFavoriteLibraryIntegration|FullyQualifiedName~TestHostBoundaryMiddleware|FullyQualifiedName~TestRefreshTokenEndpointBoundary"
    dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj
    dotnet ef migrations has-pending-model-changes \
      --project RelistenUserService/RelistenUserService.csproj \
      --startup-project RelistenUserService/RelistenUserService.csproj \
      --context AccountsDbContext
    dotnet build RelistenApi.sln

From /Users/alecgorge/code/relisten/relisten-web:

    pnpm typecheck
    pnpm lint
    pnpm build
    pnpm test:browser-session
    pnpm test:smoke:browser-session

Expected: every command exits 0. Do not rerun an unchanged failing command.
After implementation, obtain fresh read-only API and web reviews. Validate each
concrete finding, run code-simplifier only on changed code and tests, rerun the
smallest affected checks, and apply deslop to comments, this plan, runbooks,
commit bodies, status updates, and final handoff.

## Production approval checkpoint

The final local checks and fresh reviews are complete. The currently unapplied
proposal uses:

- API runtime and tests through c563a17 on
  codex/web-session-foundation-api. Later commits on that branch change only
  this plan.
- Development-only Timber branch
  2aeb1a6b3846ad9144ae940824be6314f332c15c.
- Unapplied Flux branch
  9c0ded6a00cd4af0fa7a835777906dcfa09d54a8.

The Flux branch changes only:

- clusters/relisten3-k3s/apps/relisten-user-service.yaml:
  - add relisten.net to AllowedHosts;
  - configure the exact canonical and local web origins;
  - read Accounts__WebClientSecret from WebClientSecret in the existing
    default/relisten-user-service-secrets Secret;
  - send Host accounts.relisten.net from startup, liveness, and readiness
    probes.
- clusters/relisten3-k3s/apps/relisten-web.yaml:
  - add exact /auth/session and prefix /auth/session/;
  - add exact /api/user/v1/csrf;
  - add exact /v1/me;
  - add exact /v1/library and prefix /v1/library/;
  - place all six before the existing Timber / catch-all and target
    relisten-user-service-srv:8080.
- clusters/relisten3-k3s/README.md:
  - document configuration, deployment, verification, exposure, smoke, and
    rollback.

The exact-root and slash-prefix pairs follow Traefik's current segment behavior.
They do not match /auth/session-evil or /v1/library-evil. The routes are
same-origin reverse proxying, not browser redirects. No second ingress, router
priority, certificate, DNS record, NetworkPolicy, cache middleware, or workflow
change is required.

The User Service supplies Cache-Control: private, no-store. The ingress does not
need another cache rule.

After approval, generate one stable 64-character letter-and-digit credential in
the existing 1Password vault without printing it:

    op item create \
      --category=password \
      --title='Relisten web OIDC client' \
      --vault=Private \
      --generate-password='letters,digits,64' \
      >/dev/null

The Flux runbook reads the value into a subshell and sends a one-key JSON merge
patch to kubectl over stdin. It changes only WebClientSecret and never prints
the credential. Do not commit or rotate the value during the rollout.

After approval, use the existing deployment workflow:

1. Configure. Confirm current health, create and patch WebClientSecret, and
   apply only the User Service configuration manifest. Keep the new web routes
   unexposed.
2. Deploy. Push the approved API commit and run:

       gh workflow run build_and_push_image.yml \
         --repo RelistenNet/RelistenApi \
         --ref codex/web-session-foundation-api \
         -f component=user-service

3. Verify. Wait for the workflow and rollout, then run:

       kubectl --context relisten3-k3s --namespace default rollout status \
         deployment/relisten-user-service --timeout=10m
       curl --fail --silent --show-error \
         https://auth.relisten.net/.well-known/openid-configuration >/dev/null
       curl --fail --silent --show-error \
         https://accounts.relisten.net/health/ready >/dev/null

   Expected: workflow and rollout succeed, discovery and readiness return 2xx,
   and User Service logs show no startup, migration, OpenIddict, host, or
   database error.
4. Expose. Apply only the web ingress manifest. Confirm /v1/me and
   /v1/library/snapshot reach the User Service, while /v1/library-evil and an
   unrelated /v1/* remain on Timber.
5. Smoke. Run the approved local-proxy Google proof, favorite
   add/replay/inverse sequence, logout, and canonical-host sign-in smoke.

The API pod applies additive identity migrations before listening. The approved
release is expected to add identity.sessions, its indexes and constraints, one
migration-history row, and one confidential relisten-web OpenIddict
application. A sign-in inserts one auth_sso session, one web session, and
short-lived OpenIddict authorization and token records. It inserts no native
session or refresh token.

The favorite smoke may update user_data.library_states and writes the favorite,
change, and idempotency-receipt rows needed by the add or remove. Record the
chosen favorite's initial state and restore that state exactly. Logout revokes
the linked web and auth-SSO sessions. Audit rows may remain.

Success criteria:

- User Service is Ready and public discovery/readiness stay healthy.
- Anonymous /v1/me and /v1/library/snapshot reach the User Service, return an
  authentication failure, and include private, no-store.
- Exact /auth/session reaches the User Service without creating OIDC state.
- /v1/library-evil and unrelated /v1/* remain on Timber.
- Local Timber can complete Google through production routes, then /v1/me,
  snapshot, changes, favorite add/replay/inverse, and logout all work.
- Relisten app-origin storage contains no bearer, refresh, or ID token.
- Read-only PostgreSQL inspection confirms hash-only session validators, no
  web-created native session, linked revocation, and restored favorite state
  without selecting personal or credential fields.
- A canonical-host sign-in, /v1/me read, and logout succeed without a favorite
  mutation.

Rollback removes the six browser route entries first, redeploys the last
known-good SHA-tagged User Service image, and waits for readiness. Recheck auth
discovery, accounts readiness, Timber, and the catalog API. Keep the compatible
configuration, additive tables, client registration, and Secret key. Do not run
migrations down or rotate the client secret.

Present the final branch heads, database migration names, expected writes,
health checks, smoke mutations, and rollback to the repository owner and ask
for explicit approval. Silence or code-review approval is not production
approval.

## Production rollout and rollback

Blocked pending explicit approval. After approval, record the workflow run,
rollout status, public checks, Google proof, favorite restoration,
canonical-host proof, and any rollback here. If approval is not received, leave
all local work committed and report production E2E as pending.

## Verification evidence

Never record a secret, cookie value, validator, token, callback query, or
production user field.

### Preflight

- API base master, web base timber-migration-v1, and Flux base main were clean
  and matched their remotes before branch creation.
- RelistenApi/AGENTS.md, relisten-web/AGENTS.md, and relisten-flux/AGENTS.md
  were read before branching.

### Committed work

- API: 78435dc plan; 0705bb5 persistence; 5e3ea68 web authorization;
  8e48042 initial facade; 38c17d3 shared default-secure resources; 2841c3e
  route evidence; 396c62a auth folder organization; 1209443 local HTTPS;
  2b7b114 canonical Host; dbb24c1 simplification; 708ec6a SSO clear binding;
  a8e637f local checkpoint; 270ef0b source-of-truth alignment; 3f31f93
  Development form concurrency; 9185ea0 rollout plan; 7c0c638 redirect log
  suppression; 0e1f290 antiforgery priming; bf8cf80 plan reduction; 5cabb8c
  removal of the unproven handoff design; c563a17 expiry and inactive-user
  regression coverage.
- Web: c463bf6 HTTPS, proxy, and client; eb86da6 browser smoke; c5b417c
  development docs; 86359c1 failure redaction; 9d20af1 first-run setup;
  2aeb1a6 smoke-test ownership.
- Flux, unapplied: 2442be7 production configuration and routes; a07f5e5,
  09b109f, 8b1fe69, 020cc17, and 92769dd runbook corrections; 9c0ded6
  simplified rollout. yq 4.53.6 confirmed the exact configuration, probe
  headers, and route order. Kustomize rendering, client-side apply dry-run, and
  diff whitespace checks passed. No production state changed.

### Local evidence

- Final API checks: the focused browser-session filter passed 108 tests; all
  144 User Service tests passed; EF reported no pending model changes; and the
  solution build passed with no warnings or errors. The native refresh-replay
  smoke also passed through the exact local hosts.
- Final Timber checks: two Vitest files and 10 tests passed; the one Playwright
  smoke passed; typecheck, lint, and build exited 0. Lint retained five
  pre-existing warnings outside changed files. The build retained existing
  React-compiler and chunk warnings plus Vite's current extensionless-config
  warning; the compatible default config loader builds successfully.
- Local setup: the repository owner ran pnpm setup:browser-session. mkcert
  issued and trusted the exact three-host certificate outside Git. File modes
  were 0700 for its directory, 0600 for private files, and 0644 for public
  certificates. The User Service validated local migrations and OIDC clients
  without printing the secret.
- Browser baseline: the real Development-persona OIDC flow returned to Timber;
  /v1/me, snapshot, and changes returned 200 with private, no-store; web /v1/me
  omitted native_session_uuid; simultaneous credentials failed; unreviewed
  routes returned 404; and missing or incorrect CSRF failed.
- Favorite baseline: a synthetic favorite was absent initially, added,
  observed, replayed idempotently, removed, and absent finally. Read-only
  PostgreSQL inspection confirmed no active synthetic favorite and the expected
  change and receipt rows.
- Session baseline: logout returned through the auth-host clear route and made
  /v1/me return 401. Read-only PostgreSQL inspection found only 32-byte
  validator hashes, UUIDv7 session IDs, valid purpose shapes, linked
  revocation, and no web-created native session. Browser-visible network,
  console, storage, rendered data, and sanitized logs contained no browser
  token.
- Development concurrency proof: two preloaded persona pages selected Alice
  and Shared Google concurrently and both returned to Timber. Read-only
  PostgreSQL inspection found exactly four new session rows: one auth_sso and
  one web row for each persona, with two distinct web accounts. The alleged
  wrong-account result did not occur. Origin failures returned 403, malformed
  same-origin form returned 400, the form returned private, no-store and denied
  framing, and protocol redirect parameters did not appear in sanitized logs.
- Production read-only: PostgreSQL was version 17.10 and read-only;
  identity.sessions was absent; UUIDv7 extraction was available; the User
  Service had one Ready replica on the existing latest image; WebClientSecret
  was absent without reading any Secret value; Traefik 3.7.4 used non-strict
  prefix matching; the existing certificate covered relisten.net; and the live
  relisten.net ingress still contained only Timber's / route. The latest
  identity migration remained 20260719193000_ConfigureProductionIosClient.
  No production state changed.

### Pending evidence

- Explicit production approval.
- Production rollout and Google E2E.

## Outcomes and retrospective

The durable session, OIDC, shared resource, Timber proxy/client, and local HTTPS
foundation is implemented and committed. The local baseline proved the intended
resource and session boundaries. A later static concurrency concern did not
reproduce with two different personas, so the proposed parallel handoff
framework was removed from scope. Final broad checks, the short browser smoke,
fresh reviews, code simplification, and documentation cleanup are complete.
The work is at the production approval checkpoint.

No browser access-token or refresh-token storage was introduced. No production
manifest was applied, no Secret changed, no image deployed, no production
migration ran, and no production sign-in, session, or favorite mutation
occurred. Keep this plan active until all approved work is complete.
