# Complete v4 route coverage proposal

Status: route coverage mapped, not implemented. Alias consolidation and the
13-prefix vocabulary are approved; playable tracks use `track_`. Eras remain
embedded with no prefix.

The v4 catalog must cover every existing catalog, discovery, and playback
capability. Authenticated catalog writes and import commands also have explicit
v4 coverage. Existing HTML operations, health, and authentication protocols keep
their own paths and authorization; they are accounted for rather than silently
omitted or renamed into catalog routes.

## Coverage index

- [Catalog routes](catalog-route-inventory.md): 39 current registrations across
  artists, full artist bootstrap, shows, source reviews, years, venues, songs,
  tours, eras, search, and catalog resolution, plus new direct resource reads.
- [Discovery, playback, operations, and accounts](other-route-inventory.md):
  the other 39 API registrations, 22 explicit account/auth registrations,
  and separately enumerated middleware route families.
- [Machine-readable current registrations](current-route-registrations.json):
  100 static registrations with method, path, source file, and line. This is
  source enumeration, not proof of runtime route reachability or admission.

The counts include legacy aliases and GET/POST callback registrations separately.
The proposed v4 implementation may consolidate aliases; it must retain their
lookup/filter capability. A route name alone does not establish coverage: the
linked tables specify the response hierarchy, filters, counts, and identity
meaning that need to survive.

The current artist `/normalized` route is a flat bootstrap over imported
catalog tables. V4 calls that capability `/artists/{artistId}/catalog`, with
normalized overlays. Its name must not be confused with the `norm` schema.

## Contract rules across routes

1. Every resource reference uses its approved prefix. Provider identifiers,
   calendar values, ranking metrics, cursors, and account UUIDs retain their
   own semantics; see the [prefix approval table](prefixes.md).
2. Every returned track object has one `normalization` key containing `state`
   and ordered `parts`. No sibling `normalization_state`/`contents` pair. The
   imported track title, file identity, and playback URLs remain available.
3. Source and show detail retain sources → sets → playable tracks. Direct
   source/set/track reads supplement that hierarchy, rather than replacing it.
4. Canonical venue reads span artists; artist-scoped venue reads filter by
   artist and venue. Imported venue/song resources remain separately addressable.
5. Song association/count fields distinguish imported setlist plays, distinct
   shows, and recording-track matches. A count cannot silently change meaning.
6. Complete collections, pages, and individual entities have different sync
   semantics. Declare scope/revision before mobile may hide omitted associations.
7. Search, popular/trending, random, anniversary, recently-added/performed/updated,
   history, reviews, and full artist bootstrap obey the same ID/projection rules.
8. Global favorite targets use the typed account-library contract; OAuth,
   session, CSRF, and identity protocols retain separate versioning. A new library
   route must be included in browser authorization, relay, and no-store policy.

## Design review versus implementation planning

Prefix approval is complete. The route tables identify remaining details in
alias/lookup policy, song association semantics, and response envelopes. Any unresolved contract choice in a route row remains a design item,
not permission to let an implementation decide public behavior accidentally.

During planning: assign target files/work units, codec and binding code, SQL,
indexes, pagination mechanics, cache generations, migrations, rollout order,
and tests. Runtime route coverage should later be checked against action/endpoint
descriptors and middleware configuration. This source inventory does not claim
that such implementation tests have passed.
