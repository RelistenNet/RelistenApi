# Integrating the catalog API

Read this when adding a catalog screen or changing how catalog data reaches a
client. [Swagger](http://localhost:3823/api-docs-v4/index.html) defines the routes,
parameters and response fields. This guide explains how the music entities relate
and which data a client may treat as authoritative.

## Shows group recordings; tracks identify playable files

One show can have several recordings, each represented by a source. A source
contains sets and playable tracks. A track identifies a file, so a medley can
contain several songs without becoming several playable tracks. Normalized parts
provide song matches, qualifiers and transitions; they do not provide seek offsets.
Normalization adds metadata to a file while preserving its imported title,
playback URL and ordering.

Start with artist → years → selected year → show. Fetch sources, song file matches
and other collections when the listener opens them. Full show detail includes the
recording hierarchy; a show summary does not. Retain that distinction when applying
responses to a local database: a summary's omitted children say nothing about the
children already stored. See [local persistence](mobile-sync.md).

## Imported observations and canonical entities serve different purposes

An imported venue or song belongs to an artist's upstream catalog. A canonical
venue identifies a shared physical place; a canonical song is still artist-owned.
Changing an imported ID's prefix cannot convert one into the other. Their link
requires accepted normalization evidence. A show's venue assignment can differ
from its source's imported venue observation.

Normalization is optional and can be incomplete. Artist venue/song browse lists
choose canonical targets and imported fallbacks on the server. Follow each entry's
kind; a client does not need a feature-flag decision to select a browsing route.
`features.normalization` permits normalized reads but does not promise coverage.

Display imported show venue labels and track titles. Link a venue label to its
canonical venue when one exists; use canonical display metadata only when the
imported observation is absent. Canonical aliases aid discovery. They are not a
history from which a client should infer the venue's name on the concert date.

A show's `artist_venue` represents the artist/place pair, with that artist's show
count. Its nested venue's global count spans approved artists. An imported venue's
count covers its entire imported group. During partial normalization, an imported
group can overlap canonical groups, so adding browse-entry counts can double-count
shows. Canonical song counts/shows require exact recording matches; imported song
counts/shows use setlist evidence and cannot identify a playable file. See
[count maintenance](../normalization-counts.md) before changing these definitions.

## Identity and saved references

UUID-backed public IDs losslessly encode the existing catalog UUID. Canonical
venue/song and artist-venue IDs are native short strings. Clients generally keep
all IDs opaque; mobile decodes UUID-backed catalog IDs at its storage adapter to
preserve existing download filenames and playback references.

Canonical merges return a survivor ID. Deleted canonical IDs are reserved against
reuse so a saved reference cannot silently point to a different place or song.
Saved references may therefore resolve as redirected, retired or unavailable.
Unavailable metadata does not imply that a listener removed a favorite. The
resolver returns entity patches, without ownership of unreturned child collections.

The [catalog types and codec](../../Relisten.Identifiers/),
[normalization IDs](../../Relisten.Normalization/NormIds.cs) and
[reference lookup](../../RelistenApi/Services/Catalog/CatalogReferenceLookup.cs)
define supported identities and lookup schemes. Swagger publishes their wire forms.

## Ranking scores have a comparison population

Ordinary show/year resources carry artist-local popularity. Artist resources and
global popular/trending views use global comparisons; momentum is a separate
discovery score. The same show can legitimately have different scores in different
views. Treat ranking views as online results rather than writing their contextual
scores into the ordinary offline catalog. Opening a ranked item loads its ordinary
detail before catalog persistence. [Stored popularity](../popularity/README.md)
explains refresh ownership and failure recovery.

## Where changes belong

Ordinary [catalog reads](../../RelistenApi/Services/Catalog/) select data in one primary,
read-only repeatable-read session. [Resource projections](../../RelistenApi/Models/Api/V4/)
produce the public representation. Normalization owns canonical links and vocabulary;
imports own original recording metadata. Keep batch hydration shared across routes.
Existing v2/v3 readers remain independent for older clients.

A client transport returns API responses; its repositories own local mapping and
collection membership. The [User Service](accounts.md) owns account membership
independently of catalog availability. Public play activity represents anonymous
track starts, not an authenticated listener's history or measured listening time.
Live history selects events separately from catalog hydration and is a best-effort
activity view.
