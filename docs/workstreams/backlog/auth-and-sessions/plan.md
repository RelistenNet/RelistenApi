# Workstream: Auth And Sessions

## Goal

Implement server-side Apple/Google account identity, user profiles, sessions, refresh-token rotation, revoke flows, and recent reauthentication markers for sensitive actions under `/api/v3/library/auth` and `/api/v3/library/users`.

## Why This Workstream Exists

Playlists, sync, and playback history all need stable authenticated users. The design explicitly rejects email sending in M1, so this workstream must keep the auth surface provider-based and small: Apple, Google, JWT access tokens, opaque rotating refresh tokens, and server-side sessions.

## Mutable Surface

Allowed files and directories:

- user auth/profile models and DTOs under `RelistenUserApi/Models/`
- auth/session services under `RelistenUserApi/Services/`
- auth/user controllers under `RelistenUserApi/Controllers/`
- user-data migrations for `users`, `user_auth_methods`, `user_sessions`, and `refresh_tokens`
- tests under `RelistenApiTests/` whose names contain `UserLibraryAuth`, `UserLibrarySessions`, or `UserLibraryUsers`

Out of scope:

- email magic links
- passkeys
- ATProto login implementation
- mobile secure storage implementation
- full OAuth provider UI

## Main Validator

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~UserLibraryAuth|FullyQualifiedName~UserLibrarySessions|FullyQualifiedName~UserLibraryUsers"
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

Run the token/session service unit tests first, before controller tests:

    dotnet test RelistenApiTests/RelistenApiTests.csproj --filter "FullyQualifiedName~RefreshToken"

## Dependencies or Blockers

Depends on `user-library-foundation` for serializer behavior, authenticated user context, route conventions, and migration placement. OAuth provider verification can be implemented with a provider-verifier interface and test fakes before live provider wiring.

## Current Hypothesis

Auth can remain maintainable by separating provider verification, session storage, token issuance, and controller orchestration into small classes. Avoid placing OAuth/provider logic in controllers. Do not auto-link Apple and Google accounts by email.

Most auth behavior can be tested before live provider credentials exist. Start with an `IProviderVerifier`-style seam and fake Apple/Google verifiers in tests. Add real provider signature/issuer/audience validation after the session and token model is already green. No email provider credentials are required in M1.

## Next Scoped Step

After foundation lands, implement provider-subject account creation and refresh-token rotation with tests. Defer live Apple/Google signature validation until the session/token data model is passing targeted tests.
