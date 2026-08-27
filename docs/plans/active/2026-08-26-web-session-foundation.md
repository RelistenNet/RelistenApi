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

The relisten3 manifests are applied manually from the Flux repository; the new
cluster is not reconciled by Flux. The current local Flux diff removes a
temporary fixed-node host alias and routes the canonical auth issuer through
Traefik with exact split-horizon DNS. It does not add a second ingress, cache
middleware, or deployment workflow.

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
- [x] 2026-08-27: Added a Google-only Development profile that uses the local
  User Service and local PostgreSQL. Focused configuration tests and a startup
  check proved that the profile does not load Apple credentials or production
  database configuration.
- [x] 2026-08-27: Completed the real Google flow through Chrome against the
  local User Service and local PostgreSQL. The repository owner temporarily
  authorized the localhost callback on the existing Google client and stored
  its existing secret through .NET Secret Manager.
- [x] 2026-08-27: Removed the temporary localhost callback from the existing
  Google client after the proof. The repository owner had already removed the
  unused Authorized JavaScript origins.
- [x] 2026-08-27: Presented the exact production proposal. The repository owner
  explicitly approved its Secret, manifest, deployment, migration, Google,
  session, reversible favorite, and logout writes. Opened API PR 85, Timber PR
  108, and Flux PR 17. No production write occurred while opening the PRs.
- [x] 2026-08-27: Addressed API PR review. PostgreSQL now enforces durable
  origin shape while runtime configuration owns exact origin membership.
  Security-boundary comments explain the invariants, and the PR test audit
  replaced configuration and mock-shape checks with concrete routing,
  authorization, CSRF, lifecycle, and compatibility contracts.
- [x] 2026-08-27: Merged API PR 85, built the exact merge commit, deployed its
  immutable User Service image, applied the additive session migration, and
  verified public discovery, readiness, route isolation, and no-store headers.
- [x] 2026-08-27: Consolidated the production browser paths into the existing
  web ingress. Removed the duplicate ingress and confirmed that reviewed auth
  and library paths reach the User Service while unrelated paths remain on
  Timber.
- [x] 2026-08-27: Replaced the temporary fixed node-IP host alias with a local,
  uncommitted CoreDNS exact-name rewrite plus NetworkPolicy egress to Traefik.
  Applied the local manifests directly and proved canonical TLS and the full
  OpenIddict backchannel exchange.
- [x] 2026-08-27: Completed the approved production-backed Chrome smoke through
  local Timber. Account, snapshot, changes, favorite toggle, idempotent replay,
  exact favorite restoration, logout, and linked session revocation passed.
- [x] 2026-08-27: Removed the temporary Timber mutation harness after fresh
  review, leaving the web branch clean. Committed the proven Flux correction as
  3bfe66e and opened PR 21. The Flux PR and Timber PR 110 remain unmerged.

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
- 2026-08-27: Keep the fixed web capability grant in the session constraint,
  but keep the deployment origin allowlist in runtime configuration. The
  database requires a non-null origin without duplicating environment policy.
- 2026-08-27: Remove tests that reconstruct static host configuration, inspect
  column names, count credentials without checking identity, or mock endpoint
  builders. Retain configuration-facing tests only when they protect a
  protocol, certificate-rotation, provider, origin, or database-routing
  contract.
- 2026-08-27: Use one idempotent local setup command. Store trusted local
  certificates and client secrets outside Git.
- 2026-08-27: Do not add an authorization-handoff table, credential, cookie, or
  lifecycle for a race that the real two-persona Browser path did not
  reproduce. Revisit flow-specific binding only after a deterministic failing
  case shows account confusion.
- 2026-08-27: Use the existing web Ingress and image workflow. After approval,
  configure, deploy, verify, expose, and smoke. Production writes remain
  approval-gated.
- 2026-08-27: Prove Google before the production approval request. The
  preferred durable setup is a separate development client. For this one-time
  proof, the repository owner chose the existing Google client and temporarily
  added https://localhost:5443/signin-google. The User Service and all Relisten
  writes remained local. Apple cannot use localhost; an Apple development
  proof would require a registered public development hostname and secure
  tunnel.

## API milestones

### A1. Persist and authenticate durable sessions

identity.sessions contains UUIDv7 identity, user ownership, purpose,
validator_hash, captured security_version, authentication and activity times,
sliding and absolute expiry, revocation, parent auth-SSO identity, exact web
origin, and fixed capabilities. Database constraints enforce purpose-specific
shape, 32-byte hashes, timestamp order, required origin, fixed capabilities,
and same-user parent linkage. Runtime configuration and lifecycle validation
enforce the exact supported origin allowlist.

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
bearer-only. New segment-safe /v1/library/* actions reach the API and inherit
the library policy by convention. Safe methods require library-read authority.
Unsafe methods require favorite-mutation authority plus the global cookie CSRF
and Origin boundary. Authority outside those fixed capabilities requires an
explicit policy and capability review.

## Timber milestones

### W1. Local HTTPS and exact proxy reachability

Run once:

    cd /Users/alecgorge/code/relisten/relisten-web
    pnpm install --frozen-lockfile
    pnpm setup:browser-session

The setup command generates and trusts one local certificate for exactly
localhost, web.relisten.localhost, auth.relisten.localhost, and
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

The Development-only diagnostic route is a static callback-return marker.
Vitest covers client and proxy behavior. Playwright covers Development-persona
sign-in, account and library reads, and logout against services the developer
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

For the real Google proof, create a separate Google Web OAuth client with the
exact https://localhost:5443/signin-google redirect. Store only its client ID
and client secret in .NET Secret Manager as documented in
relisten-web/docs/browser-session-development.md. Then run:

    cd /Users/alecgorge/code/relisten/RelistenApi
    dotnet run --project RelistenUserService/RelistenUserService.csproj \
      --launch-profile RelistenUserService.LocalGoogle

Expected: the User Service uses the local PostgreSQL connections pinned by the
launch profile, enables Google, disables Apple and Development personas, and
accepts the Google callback only on https://localhost:5443/signin-google.

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

The short Playwright smoke proves browser-visible Development sign-in,
browser-safe account and library reads, and logout.

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

The real local Google proof passed, and the repository owner explicitly
approved the production writes on 2026-08-27. The approved rollout is complete.
The current source state is:

- API PR 85 merged as ef7fd7d9f3fe7d17aae8bbf4bc619b6cc5e78d5b.
- Timber PR 110 remains open. It may be updated but must not be merged.
- Production route changes were applied from the Flux repository. The final
  backchannel correction remains local and uncommitted until the complete patch
  is reviewed.

The final local Flux diff is limited to:

- clusters/relisten3-k3s/apps/relisten-user-service.yaml: remove the temporary
  10.77.0.3 hostAliases entry and allow User Service egress to Traefik pods on
  TCP 8443;
- clusters/relisten3-k3s/platform/coredns-custom.yaml: rewrite only
  auth.relisten.net to traefik.kube-system.svc.cluster.local;
- clusters/relisten3-k3s/kustomization.yaml: include the CoreDNS ConfigMap in
  the manually applied relisten3 bundle;
- clusters/relisten3-k3s/README.md: apply the CoreDNS prerequisite explicitly
  and delete it explicitly during rollback because manual apply does not prune.

The exact-root and slash-prefix pairs follow Traefik's current segment behavior.
They do not match /auth/session-evil or /v1/library-evil. The routes are
same-origin reverse proxying, not browser redirects. The stable self-issuer
backchannel requires the CoreDNS and NetworkPolicy changes above. It does not
require a second ingress, certificate change, public DNS record, cache
middleware, or deployment workflow.

The User Service supplies Cache-Control: private, no-store. The ingress does not
need another cache rule.

The confidential client credential is stored outside Git and was never printed
or exposed to Timber. Certificate rotation was not part of this rollout by the
repository owner's decision.

The rollout used this sequence:

1. Configure the existing Secret and User Service settings.
2. Build and deploy the exact merged API commit.
3. Apply the additive migration and verify discovery and readiness.
4. Apply and verify the reviewed ingress paths.
5. Apply the local split-DNS and NetworkPolicy correction.
6. Run the Google, resource, reversible favorite, and logout smoke.

The API pod applied 20260827052217_AddDurableBrowserSessions before listening.
The migration added identity.sessions, its indexes and constraints, and the
confidential relisten-web OpenIddict application. The production sign-in added
one auth_sso session and one linked web session. It did not create a native
session or issue a browser refresh token.

The favorite smoke updated the library state and wrote the expected change and
idempotency receipts. The diagnostic harness restored the exact initial
favorite identity before it reported success. Logout revoked the linked web and
auth-SSO sessions. Audit rows remain by design.

Success criteria:

- User Service is Ready and public discovery/readiness stay healthy.
- Anonymous /v1/me and /v1/library/snapshot reach the User Service, return an
  authentication failure, and include private, no-store.
- Exact /auth/session returns the User Service 404 without Set-Cookie or OIDC
  state.
- /v1/library-evil and unrelated /v1/* retain Timber HTML responses.
- Local Timber can complete Google through production routes, then /v1/me,
  snapshot, changes, favorite add/replay/inverse, and logout all work.
- Relisten app-origin storage contains no bearer, refresh, or ID token.
- Read-only PostgreSQL inspection confirms hash-only session validators, no
  web-created native session, linked revocation, and restored favorite state
  without selecting personal or credential fields.
- The canonical issuer remains healthy through the same ingress and User
  Service deployment used by the local-proxy smoke.

Rollback removes the reviewed browser route entries first and redeploys the
previous immutable User Service image. Remove the CoreDNS override and Traefik
egress rule only after browser routes are hidden. Recheck auth discovery,
accounts readiness, Timber, and the catalog API. Keep the compatible Secret,
client registration, and additive tables. Do not run migrations down or rotate
the client credential.

## Production rollout and rollback

The approved rollout completed on 2026-08-27.

- GitHub Actions run 33100620480 built API merge commit ef7fd7d9.
- Production runs
  ghcr.io/relistennet/relisten-user-service@sha256:0e75d817446a91e2e21820fc2adf4f66cb894d720823d1716944e8acefeaa348.
- The User Service is Ready with zero restarts. Public issuer discovery and
  accounts readiness return 200.
- The existing web ingress sends /auth/session, /api/user/v1/csrf, /v1/me, and
  /v1/library/* to the User Service before Timber's catch-all. Segment-adjacent
  and unrelated paths remain on Timber.
- auth.relisten.net resolves to the Traefik Service only inside the cluster.
  The User Service reaches it with the canonical HTTPS hostname, valid SNI, and
  successful certificate verification. No node IP is fixed in the pod.
- The relisten3 cluster is not reconciled by Flux. The final three-file local
  patch remains applied but uncommitted while review completes.

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
  regression coverage; dcfcca0 local Google runtime; 659436d local Google
  setup evidence; 25579c5 local Google proof; f8b8dcc production baseline;
  f3bc6d2 reviewed security boundaries; 726958a focused security contracts.
- Web: c463bf6 HTTPS, proxy, and client; eb86da6 browser smoke; c5b417c
  development docs; 86359c1 failure redaction; 9d20af1 first-run setup;
  2aeb1a6 smoke-test ownership; 845cc45 local Google setup and documentation.
- Flux rollout history: 2442be7 production configuration and routes; a07f5e5,
  09b109f, 8b1fe69, 020cc17, and 92769dd runbook corrections; 9c0ded6
  simplified rollout; f4b25e4 executable rollback and route checks. PRs 17,
  18, and 19 merged the configuration and final ingress shape. PR 20 added a
  temporary fixed node-IP host alias; the current local diff removes it in
  favor of service-based split DNS.

### Local evidence

- Final API checks: the focused browser-session filter passed 108 tests; all
  144 User Service tests passed; EF reported no pending model changes; and the
  solution build passed with no warnings or errors. The native refresh-replay
  smoke also passed through the exact local hosts.
- API PR review checks: 107 focused browser, session, authorization, and host
  tests passed; all 151 User Service tests passed; EF reported no pending model
  changes; and the solution build passed with no warnings or errors. The audit
  removed static configuration reconstruction, an information_schema column
  scan, a fake endpoint builder, and a thin validator wrapper test. Real MVC
  endpoint metadata now proves default library authorization. PostgreSQL tests
  prove unsupported-origin rejection, active-parent creation, expiry caps,
  linked sibling revocation, and revoked native-session rejection.
- Final Timber checks: two Vitest files and 10 tests passed; the one Playwright
  smoke passed; typecheck, lint, and build exited 0. Lint retained five
  pre-existing warnings outside changed files. The build retained existing
  React-compiler and chunk warnings plus Vite's current extensionless-config
  warning; the compatible default config loader builds successfully.
- Local setup: the repository owner ran pnpm setup:browser-session. mkcert
  issued and trusted the exact four-host certificate outside Git. File modes
  were 0700 for its directory, 0600 for private files, and 0644 for public
  certificates. The User Service validated local migrations and OIDC clients
  without printing the secret.
- Local Google profile: a startup check with placeholder provider metadata
  reached Ready on local PostgreSQL without loading Apple credentials. Issuer
  discovery and accounts readiness returned 200; /signin-google was confined
  to localhost and rejected missing state. After fresh review fixes, 61 focused
  tests and all 148 User Service tests passed, as did the solution build and
  Timber checks. Lint retained five pre-existing warnings outside changed
  files.
- Real local Google proof: normal Chrome completed Google authorization with
  S256 PKCE and the exact https://localhost:5443/signin-google callback, then
  returned to /browser-session-development on Timber. /v1/me, snapshot,
  changes, and CSRF returned 200 with private, no-store. The web account omitted
  native_session_uuid. Aggregate read-only PostgreSQL inspection found one
  auth_sso row and one linked web row, two 32-byte validator hashes, valid
  purpose shapes, and no recent native session. Logout revoked both rows,
  returned to Timber, and made /v1/me return 401. No favorite was changed.
- The successful Chrome flow reproduced a macOS Kestrel HTTP/2 connection-close
  error with `Bad address`. The error did not affect any request or health
  result, and logged database parameters remained redacted. Treat it as local
  transport noise unless a request fails.
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
- Production preflight, refreshed 2026-08-27: PostgreSQL was version 17.10 and
  read-only. identity.sessions was absent, UUIDv7 extraction was available,
  and the latest identity migration remained
  20260719193000_ConfigureProductionIosClient. The User Service had one Ready
  replica on image digest
  sha256:7682323baf6a3f8c8a294359a4c52476ca43da8edf52f43fe681d30d73894f47.
  Auth discovery and accounts readiness returned 200. The WebClientSecret key
  was absent without reading any Secret value. The live relisten.net ingress
  still contained only Timber's / route. /auth/session and /v1/me returned
  Timber HTML. /v1/library/snapshot returned Timber's HTML 404.
  /v1/library-evil and /v1/not-reviewed remained on Timber. Flux origin/main
  had two newer PgBouncer commits with no overlap in the three browser-session
  files. Record the running image digest again immediately before the first
  approved write. No production state changed.
- Production rollout: API workflow 33100620480 built the exact merged commit.
  The User Service runs digest
  sha256:0e75d817446a91e2e21820fc2adf4f66cb894d720823d1716944e8acefeaa348,
  is Ready with zero restarts, and serves issuer discovery and accounts
  readiness with 200 responses. Migration
  20260827052217_AddDurableBrowserSessions is applied.
- Production routes: /auth/session reaches the User Service and returns its
  expected root 404; anonymous /api/user/v1/csrf, /v1/me, and
  /v1/library/snapshot reach the User Service and return authentication
  failures with private, no-store. /v1/library-evil and unrelated /v1 paths
  remain on Timber.
- Production backchannel: the User Service resolves auth.relisten.net to the
  Traefik Service inside the cluster. TLS 1.3 completed with the public
  auth.relisten.net certificate and hostname verification. Chrome completed
  Google authorization and returned to local Timber through the configured
  callback. Aggregate replica inspection found one auth_sso row and one linked
  web row with 32-byte validator hashes, zero native sessions created during
  the smoke, and no refresh token record.
- Production resource and mutation smoke: /v1/me returned the browser contract
  without native_session_uuid. Snapshot and changes returned contract version
  1. The favorite state toggled, the exact request replay returned the stored
  result, and the original favorite identity was restored. The diagnostic page
  rendered booleans only.
- Production logout: the bounded auth-host clear returned Chrome to local
  Timber. /v1/me then returned 401. Aggregate replica inspection found the one
  auth_sso row and one web row revoked, with zero active linked sessions.
- Final web review: a temporary favorite-mutating diagnostic UI could not
  guarantee recovery after every lost response or reused mutation ID. The UI
  was deleted instead of adding another orchestration layer. The web branch is
  clean; typecheck and all 10 focused browser-session tests pass.
- Final Flux review: two accepted runbook findings now apply the CoreDNS
  prerequisite before removing the host alias and explicitly delete the
  ConfigMap during rollback. Kustomize rendering and yq assertions pass.
  `kubectl diff` reports no difference between the local CoreDNS and User
  Service manifests and the live objects. Final public route checks match the
  expected User Service and Timber boundaries.

### Handoff state

- Flux PR 21 records the already-applied, proven configuration and remains open
  for repository-owner review.
- Timber PR 110 remains open and must not be merged.
- This plan update is committed only to the local
  codex/web-session-production-evidence branch. No extra API PR is needed for
  the production runtime.

## Outcomes and retrospective

The durable session, OIDC, shared resource, Timber proxy/client, and local HTTPS
foundation is implemented. Local personas and local Google proved the intended
credential, resource, CSRF, origin, and session boundaries. The production
rollout then proved the same flow through the canonical issuer without a fixed
node IP.

No browser access-token or refresh-token storage was introduced. Bootstrap
tokens remain inside the maintained OpenIddict server/client pipeline and are
discarded after callback completion. The production favorite returned to its
exact initial state, and logout revoked both linked sessions. The web branch is
clean. The authorized implementation, rollout, and production proof are
complete. Flux PR 21 and Timber PR 110 remain repository-owner decisions.
