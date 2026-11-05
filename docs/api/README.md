# Catalog and account APIs

The catalog API serves `/api/v4`; the separate User Service owns authentication,
`/v1/me` and `/v1/library`. Anonymous browsing and playback do not require login.
V4 has its own readers and resources. Existing v2/v3 implementations remain for
older clients and do not constrain v4's application interfaces.

Start here according to the change you need to make:

- [Catalog contract](catalog.md): response conventions, normalization, browsing, discovery and activity.
- [Public IDs](ids.md): approved prefixes, storage and saved-reference resolution.
- [Mobile sync](mobile-sync.md): endpoint membership, conditional reads and playback identity.
- [Accounts and library](accounts.md): typed identities, authentication and exact-target favorites.
- [Derived counts](derived-counts.md): venue/song definitions, maintenance and repair.
- [Stored popularity](../popularity/README.md): calculation/publication ownership and demand refresh.
- [Performance](performance.md): local measurements and their limits.
- [Deployment](deployment.md): migrations, writer coordination, warm-up and fix-forward recovery.

[OpenAPI](http://localhost:3823/api-docs-v4/index.html) documents request parameters,
DTOs and enums. The [endpoint map](endpoints.json) records each operation's response
shape and local replacement policy. The [HTTP examples](requests.http) use real
IDs from the local restored catalog; choose returned IDs when using another DB.
Use the [normal local setup](../../local-dev/README.md): v4 is served by the same
API process as the retained legacy routes, without a pilot launcher or separate DB.

## Responsibilities

Catalog controllers validate public requests and own HTTP/cache behavior.
[Catalog readers](../../RelistenApi/Services/Catalog/) select domain results;
[resource projections](../../RelistenApi/Models/Api/V4/) own the wire representation.
One read session supplies the connection and transaction to all readers in an
ordinary request. Serialization starts after that session is disposed.
Normalization owns accepted canonical identities, links and interpretation.
Imports retain original titles, venue labels and playable-file metadata.

The User Service owns membership and account history independently of catalog
availability. Mobile's HTTP client returns API DTOs; repositories apply them to
Realm and own query membership. Avoid a second persistent API client or sync engine.

## Release status

The stack has been exercised locally with restored catalog/normalization data,
real captured popularity aggregates and simulator sign-in/playback. That is not
production qualification. Release-signed physical-device callbacks, deployment
migrations and production load still need verification. Follow the deployment
guide before enabling the release. No combined artist-catalog endpoint exists;
load years, a selected year and recording details as the listener navigates.
