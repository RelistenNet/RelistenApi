# Accounts, authentication and favorites

The User Service owns account data in `identity` and `user_data`. The catalog API
owns music metadata. Account writes validate target syntax and exact membership
without querying catalog availability. Missing metadata never erases a favorite.
Anonymous browsing and playback continue independently of this service.

## Identities and public APIs

Application-owned identities are canonical stored typed strings: `user_`,
`session_`, `favorite_`, `mutation_`, `library_change_`, `command_`.
New values encode UUIDv7; migrated values preserve original UUID bits.
[Public IDs](ids.md) defines the formats. Stored PKs/FKs, issued `sub`/`sid`,
profile and mobile credential scope use the same strings. OpenIddict framework
keys and external Google/Apple issuer/subject remain in their existing formats.

The private-alpha `/v1/me` and `/v1/library` contracts change in place. There is
no second route version or raw-UUID compatibility mode. The account protocol's
`contract_version: 1` remains independent of catalog route versions and internal
cookie/cursor formats. [Account DTOs](../../Relisten.Accounts.Contracts/) and
[controllers](../../RelistenUserService/Controllers/) define exact fields/bounds.

- `GET /v1/me`: typed `user_id`, optional `native_session_id` and current profile.
- `PATCH /v1/me`: username command with `command_id`, retaining command replay/revision behavior.
- `GET /v1/library/snapshot`: authoritative membership and a protected account cursor.
- `GET /v1/library/changes?after=...`: explicit ordered changes since that cursor.
- `POST /v1/library/favorite-mutations:batch`: bounded, idempotent favorite mutations.

Library items use `favorite_id`, `mutation_id`, `change_id` and
`target: { kind, id }`; result fields include `submitted_favorite_id` and
`canonical_favorite_id`. Conflicts use `conflicting_mutation_ids` or
`conflicting_favorite_ids`. Targets may be `artist`, `show`, `source`, `track`,
`tour`, `imported_song`, `imported_venue`, `song` or `venue`. Prefix/kind mismatch
and raw UUID inputs are rejected. A membership ID, mutation ID and target ID are
different identities.

The active-target quota remains 10,000 per user. Mutation bodies are bounded at
256 KiB. Preserve exact-target uniqueness, receipt replay, account isolation,
revisions and existing batch/page limits. Reusing an operation with a different
payload is a conflict. Original migrated receipt hashes remain audit history;
new typed operations need fresh IDs. Apply changes and the cursor atomically.
Expired/invalid cursors return 410 and require a new snapshot.

Favorites remain exact targets across canonical merges. A saved alias may redirect
and coexist with a survivor favorite; removing one removes its exact membership.
New favorites use the current displayed target. Global venue favorites span
artists, have their own My Library section and never implicitly favorite shows.
This avoids cross-service alias reconciliation for an alpha edge case.

## Authentication boundaries

Native clients use the system browser, authorization code with S256 PKCE and
rotating refresh tokens. Browser clients receive opaque server-side sessions;
JavaScript does not receive bearer/refresh credentials. Keep existing host,
origin, CSRF, account-ownership and no-store protections. Token `sub`/`sid`,
`/v1/me` and stored mobile account scope must agree as canonical typed strings.
Never rewrite external provider subjects, signed tokens, nonces or PKCE values.

The production iOS client uses `https://relisten.net/auth/mobile/ios/callback`.
The separate development client retains `net.relisten.mobile:/oauth2redirect/ios`.
See [client configuration](../../RelistenUserService/Persistence/Migrations/20261105035500_ConfigureProductionIosHttpsCallback.cs).
Release-signed associated-link/provider handling still needs a physical-device
check; simulator Google sign-in alone does not prove it.

For routine local work, use `local-dev/accounts.sh dummy` or `google`. Both use
`relisten_db`, issuer `https://localhost:5443` and the accounts host
`https://accounts.relisten.localhost:5443`. Restart the service to change mode;
users, favorites, sessions and signing keys persist. Dummy personas and real
provider subjects remain distinct. Google mode disables persona endpoints.
Configure secrets through .NET Secret Manager and register the literal localhost
Google callback. Follow [local setup](../../local-dev/README.md#run-the-net-services).
Automated tests create and remove disposable databases; daily work needs no
separate account review database.

## Coordinated alpha cutover

Only private alpha users depended on the previous account payload. Stop old
writers for the typed-ID migration rather than support two credential formats.
[StoreTypedAccountIds](../../RelistenUserService/Persistence/Migrations/20261031035630_StoreTypedAccountIds.cs)
converts owned PKs/FKs and catalog target strings while preserving logical users,
provider links, usernames, memberships, revision counters, timestamps and receipts.
Old `source_track`, `song`, `venue` targets become `track`, `imported_song`,
`imported_venue` respectively. The old song/venue kinds referred to imported rows.

Revoke pre-cutover native/web/SSO sessions and linked first-party token families.
Cookie/cursor protection formats change; testers sign in again. Framework keys
stay intact while application-user subjects convert. Do not rewrite opaque
signed/encrypted data or accept UUID claims in the new format.

Mobile's durable identity-upgrade marker gates authentication while SecureStore
and Realm complete their separate, restartable changes. Clear old credentials,
account profiles/scopes, account caches/cursors/outboxes and pending commands;
server membership refills after login. Preserve imported catalog, files, queue,
personal playback history and anonymous favorites. Recreate anonymous favorite
rows with deterministically encoded typed keys/targets; do not mutate primary
keys in place or resend an old operation with a rewritten payload. Reset unfinished
anonymous imports so they can retry with fresh operations. Never replay completed
imports automatically. A failed upgrade stays gated and retries without clearing
credentials issued after a successful upgrade.

See [deployment](deployment.md) for backup, no-surge startup, write admission and
fix-forward policy. The migration preservation test uses the real pre-cutover schema and
[fixture](../../RelistenUserServiceTests/Fixtures/pre-typed-accounts.sql) in a
disposable database. Run it with:

```sh
dotnet test RelistenUserServiceTests --filter FullyQualifiedName~TestTypedAccountMigration
```

For manual native sign-in/session checks, use the existing
[smoke commands](../../RelistenUserService/SmokeTests/) against the normal local
service. `refresh-token-replay.sh` exercises the real refresh/session boundary.
