# Approved public ID prefixes

Status: approved by Alec on 2026-10-29, with `track_` replacing the originally
proposed playable-track prefix. The stored artist–venue relationship prefix was
added with the approved foundation on 2026-10-30.

These tables define public catalog and account identity, so they belong in API design.
Implementation planning will specify codec code, binders, database migrations,
and rollout tests using this approved vocabulary.

`U26` means 26 lowercase Crockford base32 characters encoding the exact existing
128-bit UUID value. The prefix ends at the **last** underscore, so prefixes
such as `source_set` are unambiguous. Do not use a first-underscore split.
`S8` means the existing eight-character normalized ID suffix. Full IDs are
opaque to clients; this notation describes the server contract.

## Approved catalog prefixes

| Resource / meaning | Approved prefix | Suffix | Identity source | Evidence |
|---|---|---|---|---|
| Artist | `artist_` | U26 | Existing artist UUID | [Artist](../../../RelistenApi/Models/Artist.cs) |
| Recording-backed show | `show_` | U26 | Existing show UUID | [Show](../../../RelistenApi/Models/Show.cs) |
| Source / selectable recording | `source_` | U26 | Existing source UUID | [Source](../../../RelistenApi/Models/Source.cs) |
| Set within a source | `source_set_` | U26 | Existing source-set UUID | [SourceSet](../../../RelistenApi/Models/SourceSet.cs) |
| Playable file / source track | `track_` | U26 | Existing source-track UUID | [SourceTrack](../../../RelistenApi/Models/SourceTrack.cs) |
| Artist's year aggregate | `year_` | U26 | Existing year-row UUID; calendar year remains a number | [Year](../../../RelistenApi/Models/Year.cs) |
| Tour | `tour_` | U26 | Existing tour UUID | [Tour](../../../RelistenApi/Models/Tour.cs) |
| Imported artist-specific venue | `imported_venue_` | U26 | Existing imported venue UUID | [Venue](../../../RelistenApi/Models/Venue.cs) |
| Imported artist-specific song | `imported_song_` | U26 | Existing `SetlistSong.uuid` | [SetlistSong](../../../RelistenApi/Models/SetlistSong.cs) |
| Imported setlist show | `setlist_show_` | U26 | Existing setlist-show UUID, distinct from recording-backed show | [SetlistShow](../../../RelistenApi/Models/SetlistShow.cs) |
| Source review | `source_review_` | U26 | Existing source-review UUID | [SourceReview](../../../RelistenApi/Models/SourceReview.cs) |
| Artist–venue relationship | `artist_venue_` | S8 | Native stored `norm.artist_venues.id` | [Artist-venue foundation](../../plans/active/2026-10-30-artist-venues/README.md) |
| Shared normalized venue | `venue_` | S8 | Existing normalized ID, unchanged | [NormIds](../../../Relisten.Normalization/NormIds.cs) |
| Artist-scoped normalized song | `song_` | S8 | Existing normalized ID, unchanged | [NormIds](../../../Relisten.Normalization/NormIds.cs) |

These are 14 approved catalog prefixes. Eleven wrap existing UUIDs; venue and
song IDs retain their existing short identities, and artist–venue relationships
use native stored short IDs. An imported ID never
turns into a normalized ID just by changing its prefix: those are different
entities connected by evidence or show-specific links.

Use the target's approved type for every exposed catalog reference: for example
`SourceReview.source_id` becomes a `source_...` string even though its current
DTO contains only an integer parent key. The read query obtains the source UUID.
Never encode a numeric foreign key as if it were the parent's UUID.

`id` on a resource and fields such as `artist_id`, `source_id`, and `song_id`
use these public string types. The database can retain its current UUIDs and
numeric join keys. Legacy UUIDs are encoded without modifying version/variant
bits. The [codec experiment](experiments/README.md) demonstrates the suffix
encoding, not the final prefix parser.

## Approved account prefixes

Alec approved these additional first-party account identifiers on 2026-10-29.
Each uses U26, initially encoding the existing UUID bits without reminting the
logical identity. Canonical stored typed strings and a coordinated auth/tester re-sign-in are
approved. See the implementation plan for owned PK/FK conversion and recovery.

| Resource / meaning | Approved prefix | Suffix |
|---|---|---|
| Account user | `user_` | U26 |
| First-party session | `session_` | U26 |
| Saved favorite membership | `favorite_` | U26 |
| Favorite mutation / idempotency operation | `mutation_` | U26 |
| Library change event | `library_change_` | U26 |
| Client command, including username updates | `command_` | U26 |

Account IDs are separate from catalog targets: a `favorite_` row can target a
`venue_` resource, and its add/remove request has a `mutation_` identity. Numeric
library/username revisions remain counters. Protocol tokens, provider identifiers
and framework-internal keys do not acquire these prefixes merely because they
contain an ID.

## Era decision — approved: embedded metadata, no prefix

Alec chose embedded artist-scoped era metadata on 2026-10-29. Keep the era-list
route, names, configured order, and needed show associations. No public `era_id`,
standalone era identity, era UUID backfill, or `era_` prefix is required in this
release. Existing integer database keys remain internal. Keeping era-list
functionality does not require inventing an era UUID.

## Values that do not get catalog prefixes

| Value | Proposed v4 treatment |
|---|---|
| Upstream-source integer key | Use stable provider name plus metadata, such as `archive.org`; retain unknown source names |
| Provider item ID, MusicBrainz ID, upstream identifier | Preserve provider-qualified external value |
| Artist feature-row ID | Embedded features; no independent feature identity |
| Source link ID | Embedded provider, URL, label and link-purpose flags; parent source reference uses `source_` |
| Live play event integer / `lastSeenId` | Explicit event sequence and/or opaque history cursor; track reference uses `track_` |
| Calendar year/date, set index, track position, era order | Domain values stay numbers/dates |
| Normalized track-part ordinal | Integer within a replaceable interpretation; no standalone part ID |
| Mobile browse-entry cache key | Local query-result key; no public prefix |
| Account-owned user/session/favorite/mutation/change/command ID | Use the approved account prefixes above; preserve the logical identity during migration |
| Anonymous live-play client UUID | Preserve its existing telemetry meaning; it is not authenticated account identity |
| Cursor, token, CSRF value, OIDC identifier | Preserve protocol-defined opaque value |
| Normalization run, model call, operator review and change-log ID | Internal provenance; no public catalog prefix |

The six account prefixes above are now approved in addition to the 14 catalog
prefixes. OAuth tokens, protected cursors, PKCE values and external provider IDs
retain their protocol-defined formats.

People, events, performances, global compositions, timed occurrences, and
playlists do not receive speculative prefixes in this release. Allocate their
names when their public resources and ownership are designed. Existing songs
remain artist-scoped; the `song_` prefix will not silently change meaning.

The `track_` prefix names the same source-track/file identity; it does not
create a new normalized song occurrence or change its stored UUID. Public
reference fields may retain source-track terminology while their values use
`track_`.

## Approval record

- Prefix names: approved with the sole amendment `source_track` → `track` (2026-10-29).
- Era identity strategy: approved embedded metadata, no era prefix (2026-10-29).
- Route aliases: consolidation approved when every capability is preserved (2026-10-29).
- Account prefixes: all six approved; `/v1/library` changes in place (2026-10-29).
- Native account/auth storage: approved canonical prefixed text for owned IDs,
  preserving logical identity via exact UUID encoding.
- Auth transition: approved coordinated tester re-sign-in; retire old first-party
  sessions/credentials while preserving accounts/provider links/favorites.
- No runtime API, database, or account mutation has been made by this design work.
