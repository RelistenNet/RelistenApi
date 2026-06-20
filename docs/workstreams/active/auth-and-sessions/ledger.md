# Ledger: Auth And Sessions

## Preregistered Experiments

### AUTH-001: Provider Subject And Refresh Rotation

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: Provider-subject account creation and refresh-token rotation can be implemented with test fakes before live Apple/Google provider verification.
- Responsible agent: root Codex agent.
- Start commit: `0ebd7b7`
- Worktree or branch: branch `codex/user-library-auth` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: auth/profile models, auth services, auth/user controllers, auth migrations, and auth/session tests.
- Validator: targeted `RelistenUserApiTests` auth/session tests plus `dotnet build RelistenApi.sln`.
- Expected deliverable: Account creation by provider subject through fake provider verification, rotating refresh-token storage, session listing/revocation, and tests proving reuse/revocation behavior.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: none yet.

### AUTH-002: Live Apple/Google Provider Verification And Reauth Marker

- Timestamp: 2026-06-20T05:16:06Z
- Intention / hypothesis: The existing provider-verifier seam can be wired to real Apple/Google OIDC ID token validation without committing local OAuth client files or adding email/ATProto, and sensitive account endpoints can use a minimal recent-session-auth marker.
- Responsible agent: root Codex agent.
- Start commit: `b7394a0`
- Worktree or branch: branch `codex/user-library-provider-auth` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: auth options, provider verifier services, auth/session store, account endpoint gating, auth/account tests, and workstream docs.
- Validator: targeted auth/session/account tests, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, `git diff --check`, secret-path scan, and reviewer pass.
- Expected deliverable: Production `IAuthProviderVerifier` implementation for Apple/Google ID tokens using configured audiences, a reauth endpoint or session marker, and account export/delete rejection when recent reauth is absent/stale.
- Expected artifacts: Code diff, targeted/full test output, root AutoPlan board update, reviewer subagent report, and this ledger outcome.
- Linked ExecPlan: none unless OAuth callback shape changes beyond ID-token verification.

## Outcomes

### AUTH-001 Outcome

- End commit: committed immediately after this ledger update on branch `codex/user-library-auth`.
- Artifact location: `RelistenUserApi/Auth/`, `RelistenUserApi/Controllers/AuthController.cs`, `RelistenUserApi/Controllers/UsersController.cs`, `RelistenUserApi/Models/AuthDtos.cs`, `RelistenUserApi/Models/AuthEntities.cs`, `RelistenUserApi/Services/`, `RelistenUserApi/Migrations/002_CreateAuthTables.cs`, and behavior tests under `RelistenUserApiTests/`.
- Evidence summary:
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 17 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests.
  - `dotnet build RelistenApi.sln` passed with 0 warnings and 0 errors.
  - `git diff --check` passed.
  - Local Postgres smoke showed `user_data` tables: `refresh_tokens`, `user_auth_methods`, `user_service_migrations`, `user_sessions`, `users`; migration marker rows `1:Create user_data schema for Relisten user API` and `2:Create auth and session tables for Relisten user API`.
- Review summary:
  - First reviewer found real issues: default in-memory auth state, hard-coded access-token signing key, selector-only logout revocation, no Apple/Google allowlist, and revoked sessions in active listing. The implementation was revised to use `PostgresUserAuthStore` by default, configured signing key options, verified logout tokens, provider allowlisting, and active-session filtering.
  - User feedback rejected low-signal migration string tests. The reflection/string contract tests were removed and replaced with HTTP behavior tests plus store-level Postgres integration tests.
  - Second reviewer found real issues: rotated-token reuse detection was selector-only and tests did not exercise the Postgres store. The implementation now verifies the old token secret before reuse revocation and includes `PostgresUserAuthStoreTests`.
  - Final reviewer reported no findings. Residual risk: the Postgres rotation integration test covers sequential second rotation, not a true concurrent race, though the implementation uses `SELECT ... FOR UPDATE`.
- Conclusion: Auth/session first iteration is complete for server M1 foundation. It supports Apple/Google provider-subject sign-in through a verifier seam, configured access tokens, opaque refresh tokens with rotation/reuse handling, Postgres-backed sessions under `user_data`, session list/revoke, no email flow, and a Development/Test-only local simulator token endpoint.
- next_action: done
- Next move: Promote `playlists-and-sharing` now that stable user identities, session auth, and user-data schema helpers exist. Live Apple/Google verifier implementation remains deferred.

### AUTH-002 Outcome

- Timestamp: 2026-06-20T05:29:24Z
- End commit: committed immediately after this ledger update on branch `codex/user-library-provider-auth`.
- Artifact location: `RelistenUserApi/Services/OidcAuthProviderVerifier.cs`, `RelistenUserApi/Configuration/UserAuthOptions.cs`, `RelistenUserApi/Controllers/AuthController.cs`, `RelistenUserApi/Controllers/UsersController.cs`, `RelistenUserApi/Migrations/010_AddSessionReauthenticationMarker.cs`, auth/session DTOs and stores, and behavior tests under `RelistenUserApiTests/`.
- Evidence summary:
  - Focused auth/account/session/Postgres-store filter passed 22 tests.
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed 86 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed 47 tests.
  - `dotnet build RelistenApi.sln` passed with 0 warnings and 0 errors.
  - Local Postgres smoke showed migration marker 10 and `user_data.user_sessions.reauthenticated_at`.
  - `git diff --check` passed.
  - Secret-path scan found no local Google OAuth client files, downloaded paths, or secret references.
- Review summary:
  - Root review normalized provider-token failures after JWKS refresh so stale/missing keys do not bubble raw security exceptions.
  - Reviewer found two real issues: empty provider algorithm allowlists could disable the algorithm restriction, and the Postgres reauth marker update lacked endpoint coverage. The implementation now fails closed when nonblank algorithms are absent and tests that branch directly. The account-gating test now uses the real `/api/v3/library/auth/reauthenticate/google` endpoint with the production Postgres auth store before export/delete succeeds.
- Conclusion: AUTH-002 completes live Apple/Google OIDC ID-token verification wiring, keeps providers fail-closed until audiences are configured, preserves Development/Test local simulator auth, and enforces recent reauthentication for account export/delete.
- next_action: done
- Next move: Reassess final runtime docs and whether the server AutoPlan acceptance evidence is complete.
