# Workstream: Auth And Sessions

## Goal

Implement server-side Apple/Google account identity, user profiles, sessions, refresh-token rotation, revoke flows, and local-development token issuance under `/api/v3/library/auth` and `/api/v3/library/users`.

## Why This Workstream Exists

Playlists, sync, and playback history all need stable authenticated users. The design explicitly rejects email sending in M1, so this workstream must keep the auth surface provider-based and small: Apple, Google, JWT access tokens, opaque rotating refresh tokens, and server-side sessions.

## Mutable Surface

Allowed files and directories:

- user auth/profile models and DTOs under `RelistenUserApi/Models/`
- auth/session services under `RelistenUserApi/Services/`
- auth/user controllers under `RelistenUserApi/Controllers/`
- user-data migrations for `users`, `user_auth_methods`, `user_sessions`, and `refresh_tokens`
- tests under `RelistenUserApiTests/` whose names contain `UserLibraryAuth`, `UserLibrarySessions`, or `UserLibraryUsers`

Out of scope:

- email magic links
- passkeys
- ATProto login implementation
- mobile secure storage implementation
- full OAuth provider UI

## Main Validator

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

Run the auth/session HTTP and store tests first:

    dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter "FullyQualifiedName~UserLibraryAuth|FullyQualifiedName~UserLibrarySessions|FullyQualifiedName~PostgresUserAuthStore"

## Dependencies or Blockers

Depends on `user-library-foundation` for serializer behavior, authenticated user context, route conventions, and migration placement. OAuth provider verification can be implemented with a provider-verifier interface and test fakes before live provider wiring.

## Current Hypothesis

Auth can remain maintainable by separating provider verification, session storage, token issuance, and controller orchestration into small classes. Avoid placing OAuth/provider logic in controllers. Do not auto-link Apple and Google accounts by email.

Most auth behavior can be tested before live provider credentials exist. The current implementation uses a real OIDC `IAuthProviderVerifier` for Apple/Google ID tokens and fake provider verifiers in tests. Production provider auth fails closed until client IDs/audiences are configured. No email provider credentials are required in M1.

Mobile local development needs real Bearer auth/refresh behavior without Apple/Google credentials. The current implementation provides `POST /api/v3/library/auth/development/session` only in `Development` or `Test`; it creates a server-side session and issues the same access/refresh token shape used by the normal auth flow. The endpoint must stay closed outside Development/Test.

## Next Scoped Step

AUTH-001 and AUTH-002 are complete. If reopened, the next scoped step is provider link/unlink management; do not add email or ATProto login in this M1 workstream.
