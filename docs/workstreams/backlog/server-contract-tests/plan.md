# Workstream: Server Contract Tests And Hardening

## Goal

Add broad contract and hardening tests for the completed user-library server: route shape, snake-case JSON, cache headers, token scrubbing, migration placement, account deletion/export gates, and no regression of existing catalog API behavior.

## Why This Workstream Exists

Feature-specific tests prove individual services. This workstream proves the server behaves coherently as an API surface and remains safe to operate: no user tables in `public`, no authenticated response caching, no token leakage, no accidental v3 JSON drift, and no broken existing catalog endpoints.

## Mutable Surface

Allowed files and directories:

- `RelistenApiTests/` endpoint/contract/hardening fixtures
- small production fixes required by contract tests
- docs updates for local validation commands

Out of scope:

- implementing primary feature behavior that belongs in other workstreams
- mobile app tests

## Main Validator

    dotnet test RelistenApiTests/RelistenApiTests.csproj
    dotnet build RelistenApi.sln

## Fastest Useful Current Check

Run the specific failing contract fixture first, then the full suite at milestone boundaries.

## Dependencies or Blockers

Depends on endpoint families existing. Can add serializer and migration placement tests early, but most broad contract tests should wait until endpoints are implemented.

## Current Hypothesis

A small set of integration-style tests gives better maintenance value than a large brittle snapshot suite. Prefer explicit route/header/body assertions for behavior that matters to mobile/web clients.

## Next Scoped Step

After the foundation stream lands, add serializer and migration placement tests. After each later milestone, add focused contract tests for the new endpoint family.

