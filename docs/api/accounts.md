# Account ownership and catalog references

Read this when adding account-owned data or connecting a client to the library.
[Account contracts](../../Relisten.Accounts.Contracts/) and
[controllers](../../RelistenUserService/Controllers/) define payloads and validation.
The distinction here is between personal membership and the catalog metadata used
to display it.

## Membership survives unavailable catalog data

The User Service owns `identity` and `user_data`; the catalog API owns music
metadata. Favorite writes validate target identity and exact membership without
querying the catalog. This lets account writes continue when catalog metadata is
unavailable and preserves favorites for removed or temporarily unresolved targets.
Clients resolve display data separately. A resolver failure is not an unfavorite.

Favorites are exact targets. After a canonical merge, an alias favorite and a
survivor favorite may coexist. Each removal addresses its own stored target;
new favorites use the currently displayed target. A global venue favorite covers
the physical venue across artists and never implicitly favorites its shows.

A snapshot establishes account membership; changes apply explicit ordered events
and advance the protected cursor atomically. Invalid/expired cursors require a
new snapshot. Retrying a mutation with the same identity and payload replays its
receipt; changing the payload under that identity is a conflict. These guarantees
belong to account persistence and do not depend on catalog ETags or availability.

## Application identity and authentication

Application user/session/favorite/operation IDs are canonical typed strings in
storage and public contracts. Token `sub`/`sid`, profile and client account scope
must agree. OpenIddict framework keys and external provider issuer/subject retain
their own formats. A favorite's membership ID, operation ID and catalog target ID
identify different things. See [account identity types](../../Relisten.Accounts.Contracts/Identifiers/AccountIds.cs).

Native authentication uses the system browser, authorization code with S256 PKCE
and rotating refresh tokens. Browser clients receive opaque server-side sessions;
JavaScript does not receive bearer or refresh tokens. Keep browser origin/CSRF
checks and account ownership at the service boundary when adding routes. The
catalog remains anonymous, so account outages do not prevent browsing or playback.

Use [local setup](../../local-dev/README.md#run-the-net-services) for dummy or real
Google sign-in and the existing [smoke commands](../../RelistenUserService/SmokeTests/)
for session checks. Broader playlist/history/Sonos proposals are future designs;
they do not describe implemented account features.
